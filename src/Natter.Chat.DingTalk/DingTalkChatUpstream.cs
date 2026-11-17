using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Natter.Abstractions.Ports;

namespace Natter.Chat.DingTalk;

/// <summary>
/// DingTalk NLMP upstream. DingTalk is text-first: no edit/delete/typing/slash in this version.
/// The sessionWebhook from the last callback is the reply token for the conversation.
/// </summary>
public sealed class DingTalkChatUpstream : IChatUpstream, IChatUpstreamHealth, IDisposable
{
    private readonly IDingTalkTransport _transport;
    private readonly DingTalkOptions _options;
    private readonly IClock _clock;
    private readonly Subject<ChatEvent> _events = new();
    private readonly IObservable<ChatEvent> _synchronized;
    private readonly ConcurrentDictionary<string, SessionWebhook> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ChatAck> _idempotency = new(StringComparer.Ordinal);
    private readonly Capabilities _capabilities;
    private readonly Lock _gate = new();
    private IDisposable? _subscription;
    private long _sequence;
    private bool _connected;
    private bool _disposed;
    private int _reconnectCount;
    private DateTimeOffset? _lastEventAt;
    private DateTimeOffset? _lastConnectedAt;
    private DateTimeOffset? _lastDisconnectAt;

    public DingTalkChatUpstream(
        Source source,
        IDingTalkTransport transport,
        DingTalkOptions options,
        IClock clock)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _synchronized = Subject.Synchronize(_events);
        _capabilities = new Capabilities
        {
            ConversationKinds = [ConversationKind.Direct, ConversationKind.Group],
            MaxMessageLength = Math.Max(1, options.MaxMessageLength),
            Markdown = false,
            Attachments = false,
            Mentions = true,
            Reply = false,
            Edit = false,
            Delete = false,
            Typing = false,
            Ephemeral = false,
            SlashCommands = false,
            Buttons = false,
            Modals = false,
            MaxAttachmentBytes = 0,
        };
    }

    public Source Source { get; }

    public bool IsConnected
    {
        get { lock (_gate) return _connected; }
    }

    public IObservable<ChatEvent> Events => _synchronized;

    public string? LastError { get; private set; }

    public UpstreamHealth Health
        => new(Source.Key, IsConnected, _lastEventAt, _lastConnectedAt, _lastDisconnectAt, _reconnectCount, LastError, 0, null);

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsConnected)
            return;

        _subscription ??= _transport.Events.Subscribe(new Observer(this));
        await _transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
            _connected = true;
        LastError = null;
        _lastConnectedAt = _clock.UtcNow;
        PublishStatus(UpstreamState.Ready);
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var wasConnected = IsConnected;
        lock (_gate)
            _connected = false;
        if (wasConnected)
        {
            _lastDisconnectAt = _clock.UtcNow;
            Interlocked.Increment(ref _reconnectCount);
        }
        _subscription?.Dispose();
        _subscription = null;
        await _transport.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        PublishStatus(UpstreamState.Disconnected);
    }

    public ValueTask<Capabilities> GetCapabilitiesAsync(
        Conversation? conversation = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_capabilities);

    public async ValueTask<ChatAck> ExecuteAsync(ChatCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!string.Equals(command.Source.Key, Source.Key, StringComparison.Ordinal))
            return ChatAck.Failure(command, ChatError.Create(ChatErrorCodes.InvalidRequest, "Source does not match DingTalk upstream."));

        if (command.IdempotencyKey is { Length: > 0 } key && _idempotency.TryGetValue(key, out var cached))
            return cached;

        try
        {
            switch (command)
            {
                case MessageSendCommand send:
                    return await SendAsync(send, cancellationToken).ConfigureAwait(false);
                default:
                    return ChatAck.Failure(command, ChatError.Create(
                        ChatErrorCodes.Unsupported,
                        $"DingTalk does not support '{command.Type}' in this version."));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ChatAck.Failure(command, ChatError.Create(ChatErrorCodes.Internal, ex.Message));
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _subscription?.Dispose();
        _events.OnCompleted();
        _events.Dispose();
        if (_transport is IDisposable disposable)
            disposable.Dispose();
    }

    private async ValueTask<ChatAck> SendAsync(MessageSendCommand command, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(command.Payload.Conversation.Id, out var session)
            || session.ExpiresAt <= _clock.UtcNow)
        {
            return ChatAck.Failure(command, ChatError.Create(
                ChatErrorCodes.NotFound,
                "DingTalk sessionWebhook is missing or expired; wait for the next inbound message."));
        }

        var degraded = ChatCapabilityDegrader.DegradeSend(command, _capabilities);
        degraded.Payload.Content.EnsureValid();
        var chunks = ChatTextChunker.Chunk(degraded.Payload.Content, _capabilities.MaxMessageLength);
        var ids = new List<string>(chunks.Count);

        foreach (var chunk in chunks)
        {
            var result = await _transport.SendAsync(
                new DingTalkReply(command.Payload.Conversation.Id, session.Webhook, chunk.Text),
                cancellationToken).ConfigureAwait(false);
            if (!result.Ok)
                return ChatAck.Failure(command, ToError(result));
            ids.Add("dingtalk:" + Guid.NewGuid().ToString("N"));
        }

        var ack = ChatAck.Success(command, ids, _clock.UtcNow);
        if (command.IdempotencyKey is { Length: > 0 } idempotencyKey)
            _idempotency.TryAdd(idempotencyKey, ack);
        return ack;
    }

    private void OnTransportEvent(DingTalkTransportEvent transportEvent)
    {
        _lastEventAt = _clock.UtcNow;
        switch (transportEvent.Kind)
        {
            case DingTalkTransportEventKind.Message when transportEvent.Message is { } message:
                OnMessage(message);
                break;
            case DingTalkTransportEventKind.Error:
                LastError = transportEvent.ErrorMessage;
                PublishStatus(UpstreamState.Error, transportEvent.ErrorMessage);
                break;
            case DingTalkTransportEventKind.Disconnected:
                lock (_gate)
                    _connected = false;
                _lastDisconnectAt = _clock.UtcNow;
                Interlocked.Increment(ref _reconnectCount);
                PublishStatus(UpstreamState.Disconnected);
                break;
        }
    }

    private void OnMessage(DingTalkCallbackMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Content)
            || !_options.IsConversationAllowed(message.ConversationId)
            || !_options.IsUserAllowed(message.EffectiveUserId))
        {
            return;
        }

        var atSelf = message.IsInAtList
                     || message.AtUserIds.Contains(Source.SelfId, StringComparer.Ordinal)
                     || string.Equals(message.ChatbotUserId, Source.SelfId, StringComparison.Ordinal);

        if (_options.RequireMention && !message.IsDirect && !atSelf)
            return;

        if (string.Equals(message.EffectiveUserId, Source.SelfId, StringComparison.Ordinal))
            return;

        if (!string.IsNullOrWhiteSpace(message.SessionWebhook))
        {
            _sessions[message.ConversationId] = new SessionWebhook(
                message.SessionWebhook,
                message.SessionWebhookExpiresAt ?? _clock.UtcNow.AddMinutes(30));
        }

        var conversation = new Conversation(
            message.ConversationId,
            message.IsDirect ? ConversationKind.Direct : ConversationKind.Group,
            GuildId: message.IsGroup ? message.ConversationId : null,
            Title: message.ConversationTitle);

        var mentions = message.AtUserIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => new Mention(MentionKind.User, id, id))
            .ToList();

        if (atSelf && mentions.All(mention => mention.Id != Source.SelfId))
            mentions.Insert(0, new Mention(MentionKind.User, Source.SelfId, "Natter"));

        var text = message.Content.Trim();
        var nlmpMessage = new Message(
            message.MsgId,
            conversation,
            new User(message.EffectiveUserId, message.SenderNick, UserKind.Human, message.SenderNick),
            new Content(text, ContentFormat.Plain, Mentions: mentions.Count == 0 ? null : mentions),
            message.CreatedAt);

        nlmpMessage.EnsureValid();
        _events.OnNext(new MessageCreatedEvent
        {
            Id = $"dingtalk:message:{message.MsgId}",
            Source = Source,
            Sequence = Interlocked.Increment(ref _sequence),
            OccurredAt = message.CreatedAt,
            ReceivedAt = _clock.UtcNow,
            Payload = new MessagePayload(nlmpMessage),
        });
    }

    private static ChatError ToError(DingTalkSendResult result)
        => ChatError.Create(
            result.ErrorCode switch
            {
                "rate_limited" => ChatErrorCodes.RateLimited,
                "unauthorized" => ChatErrorCodes.Unauthorized,
                "not_found" => ChatErrorCodes.NotFound,
                _ => ChatErrorCodes.Unavailable,
            },
            result.ErrorMessage ?? "DingTalk send failed.");

    private void PublishStatus(UpstreamState state, string? detail = null)
        => _events.OnNext(new UpstreamStatusEvent
        {
            Id = $"dingtalk:status:{Interlocked.Increment(ref _sequence)}",
            Source = Source,
            Sequence = _sequence,
            OccurredAt = _clock.UtcNow,
            ReceivedAt = _clock.UtcNow,
            Payload = new UpstreamStatusPayload(state, detail),
        });

    private sealed class Observer(DingTalkChatUpstream owner) : IObserver<DingTalkTransportEvent>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
            lock (owner._gate)
                owner._connected = false;
            owner.PublishStatus(UpstreamState.Error, error.Message);
        }

        public void OnNext(DingTalkTransportEvent value) => owner.OnTransportEvent(value);
    }

    private sealed record SessionWebhook(string Webhook, DateTimeOffset ExpiresAt);
}
