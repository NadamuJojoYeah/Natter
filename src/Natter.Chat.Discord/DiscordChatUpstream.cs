using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Natter.Abstractions.Ports;

namespace Natter.Chat.Discord;

/// <summary>
/// Discord transport upstream: source identity, allowlists, NLMP event mapping, command
/// execution and capability declaration. Networking lives in <see cref="IDiscordTransport"/>.
/// </summary>
public sealed class DiscordChatUpstream : IChatUpstream, IDisposable
{
    private readonly IDiscordTransport _transport;
    private readonly DiscordOptions _options;
    private readonly IClock _clock;
    private readonly DiscordEventMapper _mapper;
    private readonly Subject<ChatEvent> _events = new();
    private readonly IObservable<ChatEvent> _synchronizedEvents;
    private readonly ConcurrentDictionary<string, ChatAck> _idempotency = new(StringComparer.Ordinal);
    private readonly Capabilities _capabilities;
    private readonly Lock _gate = new();
    private IDisposable? _transportSubscription;
    private long _sequence;
    private bool _connected;
    private bool _disposed;

    public DiscordChatUpstream(
        Source source,
        IDiscordTransport transport,
        DiscordOptions options,
        IClock clock)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _mapper = new DiscordEventMapper(source, options);
        _synchronizedEvents = Subject.Synchronize(_events);
        _capabilities = DiscordCapabilities.Create(options);
    }

    public Source Source { get; }

    public bool IsConnected
    {
        get
        {
            lock (_gate)
                return _connected;
        }
    }

    public IObservable<ChatEvent> Events => _synchronizedEvents;

    public string? LastError { get; private set; }

    public int? GuildCount { get; private set; }

    public int? ChannelCount { get; private set; }

    public int? LatencyMs { get; private set; }

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(Source.SelfId))
            throw new InvalidOperationException("Discord Source.SelfId must be configured before connecting.");

        lock (_gate)
        {
            if (_connected)
                return;

            _transportSubscription ??= _transport.Events.Subscribe(new TransportObserver(this));
        }

        try
        {
            await _transport.ConnectAsync(cancellationToken).ConfigureAwait(false);

            if (LastError is not null)
            {
                await _transport.DisconnectAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"Discord transport failed to become ready: {LastError}");
            }

            lock (_gate)
                _connected = true;

            PublishStatus(UpstreamState.Ready);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetError("connect_failed", ex.Message);
            PublishStatus(UpstreamState.Error, ex.Message);
            throw;
        }
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _transportSubscription?.Dispose();
            _transportSubscription = null;
            _connected = false;
        }

        try
        {
            await _transport.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }

        PublishStatus(UpstreamState.Disconnected);
    }

    public ValueTask<Capabilities> GetCapabilitiesAsync(
        Conversation? conversation = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_capabilities);

    public async ValueTask<ChatAck> ExecuteAsync(ChatCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ThrowIfDisposed();

        if (!string.Equals(command.Source.Key, Source.Key, StringComparison.Ordinal))
            return ChatAck.Failure(command, ChatError.Create(
                ChatErrorCodes.InvalidRequest,
                $"Command source '{command.Source.Key}' does not match upstream '{Source.Key}'."));

        var idempotencyKey = command.IdempotencyKey;
        if (!string.IsNullOrWhiteSpace(idempotencyKey)
            && _idempotency.TryGetValue(idempotencyKey, out var cached))
        {
            return cached;
        }

        var ack = await ExecuteCoreAsync(command, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(idempotencyKey) && ack.Ok)
            _idempotency.TryAdd(idempotencyKey, ack);

        return ack;
    }

    public DiscordStatusSnapshot GetStatus()
        => new(
            Source,
            IsConnected,
            LastError,
            GuildCount,
            ChannelCount,
            LatencyMs,
            _capabilities,
            _options.Guilds.ToArray(),
            _options.Channels.ToArray(),
            _options.Users.ToArray(),
            _options.RequireMention,
            _options.SlashCommands);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _transportSubscription?.Dispose();
        _events.OnCompleted();
        _events.Dispose();
        if (_transport is IDisposable disposable)
            disposable.Dispose();
    }

    private async ValueTask<ChatAck> ExecuteCoreAsync(ChatCommand command, CancellationToken cancellationToken)
    {
        try
        {
            switch (command)
            {
                case MessageSendCommand send:
                    return await SendAsync(send, cancellationToken).ConfigureAwait(false);
                case MessageUpdateCommand update:
                    return await UpdateAsync(update, cancellationToken).ConfigureAwait(false);
                case MessageDeleteCommand delete:
                    return await DeleteAsync(delete, cancellationToken).ConfigureAwait(false);
                case TypingStartCommand typing:
                    return await TypingAsync(typing, cancellationToken).ConfigureAwait(false);
                case InteractionRespondCommand respond:
                    return await RespondAsync(respond, cancellationToken).ConfigureAwait(false);
                default:
                    return ChatAck.Failure(command, ChatError.Create(
                        ChatErrorCodes.Unsupported,
                        $"Discord upstream does not support command '{command.Type}'."));
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

    private async ValueTask<ChatAck> SendAsync(MessageSendCommand command, CancellationToken cancellationToken)
    {
        var payload = ChatCapabilityDegrader.DegradeSend(command, _capabilities).Payload;
        payload.Content.EnsureValid();

        var chunks = ChatTextChunker.Chunk(payload.Content, _capabilities.MaxMessageLength);
        var messageIds = new List<string>(chunks.Count);
        var mentionIds = payload.Content.Mentions?
            .Where(mention => mention.Kind == MentionKind.User && mention.Id is not null)
            .Select(mention => mention.Id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        for (var index = 0; index < chunks.Count; index++)
        {
            var request = new DiscordSendMessage(
                payload.Conversation.Id,
                chunks[index].Text,
                index == 0 ? payload.ReplyTo : null,
                mentionIds);

            var result = await _transport.SendMessageAsync(request, cancellationToken).ConfigureAwait(false);
            if (!result.Ok)
                return ChatAck.Failure(command, ToError(result));

            if (!string.IsNullOrWhiteSpace(result.MessageId))
                messageIds.Add(result.MessageId);
        }

        return ChatAck.Success(command, messageIds, _clock.UtcNow);
    }

    private async ValueTask<ChatAck> UpdateAsync(MessageUpdateCommand command, CancellationToken cancellationToken)
    {
        if (!_capabilities.Edit)
            return Unsupported(command, "message.update");

        var payload = ChatCapabilityDegrader.DegradeUpdate(command, _capabilities).Payload;
        payload.Content.EnsureValid();

        var mentionIds = payload.Content.Mentions?
            .Where(mention => mention.Kind == MentionKind.User && mention.Id is not null)
            .Select(mention => mention.Id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var result = await _transport.UpdateMessageAsync(
            new DiscordUpdateMessage(payload.Conversation.Id, payload.MessageId, payload.Content.Text, mentionIds),
            cancellationToken).ConfigureAwait(false);

        return result.Ok
            ? ChatAck.Success(command, result.MessageId is null ? null : [result.MessageId], _clock.UtcNow)
            : ChatAck.Failure(command, ToError(result));
    }

    private async ValueTask<ChatAck> DeleteAsync(MessageDeleteCommand command, CancellationToken cancellationToken)
    {
        if (!_capabilities.Delete)
            return Unsupported(command, "message.delete");

        var result = await _transport.DeleteMessageAsync(
            new DiscordDeleteMessage(command.Payload.Conversation.Id, command.Payload.MessageId),
            cancellationToken).ConfigureAwait(false);

        return result.Ok
            ? ChatAck.Success(command, deliveredAt: _clock.UtcNow)
            : ChatAck.Failure(command, ToError(result));
    }

    private async ValueTask<ChatAck> TypingAsync(TypingStartCommand command, CancellationToken cancellationToken)
    {
        if (!_capabilities.Typing)
            return Unsupported(command, "typing.start");

        await _transport.TriggerTypingAsync(
            new DiscordConversationRef(command.Payload.Conversation.Id, command.Payload.Conversation.GuildId),
            cancellationToken).ConfigureAwait(false);

        return ChatAck.Success(command, deliveredAt: _clock.UtcNow);
    }

    private async ValueTask<ChatAck> RespondAsync(InteractionRespondCommand command, CancellationToken cancellationToken)
    {
        if (!_capabilities.SlashCommands && !_capabilities.Buttons && !_capabilities.Modals)
            return Unsupported(command, "interaction.respond");

        var payload = ChatCapabilityDegrader.DegradeRespond(command, _capabilities).Payload;
        payload.Content.EnsureValid();

        var result = await _transport.RespondInteractionAsync(
            new DiscordInteractionResponse(
                payload.InteractionId,
                payload.Content.Text,
                payload.Ephemeral,
                payload.UpdateOriginal),
            cancellationToken).ConfigureAwait(false);

        return result.Ok
            ? ChatAck.Success(command, result.MessageId is null ? null : [result.MessageId], _clock.UtcNow)
            : ChatAck.Failure(command, ToError(result));
    }

    private void OnTransportEvent(DiscordTransportEvent transportEvent)
    {
        switch (transportEvent.Kind)
        {
            case DiscordTransportEventKind.Ready:
                OnReady(transportEvent);
                break;
            case DiscordTransportEventKind.MessageCreated:
                PublishMappedMessage(transportEvent, isEdit: false);
                break;
            case DiscordTransportEventKind.MessageUpdated:
                PublishMappedMessage(transportEvent, isEdit: true);
                break;
            case DiscordTransportEventKind.MessageDeleted:
                PublishDeleted(transportEvent);
                break;
            case DiscordTransportEventKind.InteractionCreated:
                PublishInteraction(transportEvent);
                break;
            case DiscordTransportEventKind.Error:
                SetError(transportEvent.ErrorCode ?? "transport_error", transportEvent.ErrorMessage ?? "Discord transport error.");
                PublishStatus(UpstreamState.Error, LastError);
                break;
            case DiscordTransportEventKind.Disconnected:
                lock (_gate)
                    _connected = false;
                PublishStatus(UpstreamState.Disconnected, transportEvent.ErrorMessage);
                break;
        }
    }

    private void OnReady(DiscordTransportEvent transportEvent)
    {
        GuildCount = transportEvent.GuildCount;
        ChannelCount = transportEvent.ChannelCount;
        LatencyMs = transportEvent.LatencyMs;

        if (!string.IsNullOrWhiteSpace(transportEvent.SelfId)
            && !string.Equals(transportEvent.SelfId, Source.SelfId, StringComparison.Ordinal))
        {
            SetError("self_id_mismatch", $"Discord login user '{transportEvent.SelfId}' does not match configured selfId '{Source.SelfId}'.");
            return;
        }

        LastError = null;
        lock (_gate)
            _connected = true;
    }

    private void PublishMappedMessage(DiscordTransportEvent transportEvent, bool isEdit)
    {
        if (transportEvent.Message is null)
            return;

        Message? message;
        try
        {
            message = _mapper.MapMessage(transportEvent.Message, isEdit);
        }
        catch (NlmpValidationException ex)
        {
            SetError("mapping_failed", ex.Message);
            return;
        }

        if (message is null)
            return;

        var occurredAt = isEdit
            ? message.EditedAt ?? message.CreatedAt
            : message.CreatedAt;

        ChatEvent chatEvent = isEdit
            ? new MessageUpdatedEvent
            {
                Id = $"discord:message:{message.Id}:updated",
                Source = Source,
                Sequence = Interlocked.Increment(ref _sequence),
                OccurredAt = occurredAt,
                ReceivedAt = _clock.UtcNow,
                Payload = new MessagePayload(message),
            }
            : new MessageCreatedEvent
            {
                Id = $"discord:message:{message.Id}:created",
                Source = Source,
                Sequence = Interlocked.Increment(ref _sequence),
                OccurredAt = occurredAt,
                ReceivedAt = _clock.UtcNow,
                Payload = new MessagePayload(message),
            };

        _events.OnNext(chatEvent);
    }

    private void PublishDeleted(DiscordTransportEvent transportEvent)
    {
        if (string.IsNullOrWhiteSpace(transportEvent.MessageId) || transportEvent.Channel is null)
            return;

        var conversation = _mapper.MapConversation(transportEvent.Channel);
        _events.OnNext(new MessageDeletedEvent
        {
            Id = $"discord:message:{transportEvent.MessageId}:deleted",
            Source = Source,
            Sequence = Interlocked.Increment(ref _sequence),
            OccurredAt = _clock.UtcNow,
            ReceivedAt = _clock.UtcNow,
            Payload = new MessageDeletedPayload(conversation, transportEvent.MessageId, _clock.UtcNow),
        });
    }

    private void PublishInteraction(DiscordTransportEvent transportEvent)
    {
        if (transportEvent.Interaction is null)
            return;

        Interaction? interaction;
        try
        {
            interaction = _mapper.MapInteraction(transportEvent.Interaction);
        }
        catch (NlmpValidationException ex)
        {
            SetError("mapping_failed", ex.Message);
            return;
        }

        if (interaction is null)
            return;

        _events.OnNext(new InteractionCreatedEvent
        {
            Id = $"discord:interaction:{interaction.Id}",
            Source = Source,
            Sequence = Interlocked.Increment(ref _sequence),
            OccurredAt = interaction.CreatedAt,
            ReceivedAt = _clock.UtcNow,
            Payload = new InteractionPayload(interaction),
        });
    }

    private void PublishStatus(UpstreamState state, string? detail = null)
    {
        _events.OnNext(new UpstreamStatusEvent
        {
            Id = $"discord:status:{Interlocked.Increment(ref _sequence)}",
            Source = Source,
            Sequence = _sequence,
            OccurredAt = _clock.UtcNow,
            ReceivedAt = _clock.UtcNow,
            Payload = new UpstreamStatusPayload(state, detail),
        });
    }

    private void SetError(string code, string message)
    {
        LastError = $"{code}: {message}";
        lock (_gate)
            _connected = false;
    }

    private static ChatError ToError(DiscordSendResult result)
    {
        var code = result.ErrorCode switch
        {
            "rate_limited" => ChatErrorCodes.RateLimited,
            "forbidden" => ChatErrorCodes.Forbidden,
            "not_found" => ChatErrorCodes.NotFound,
            "timeout" => ChatErrorCodes.Timeout,
            "unavailable" => ChatErrorCodes.Unavailable,
            "invalid_request" => ChatErrorCodes.InvalidRequest,
            _ => ChatErrorCodes.Internal,
        };

        return ChatError.Create(code, result.ErrorMessage ?? "Discord request failed.", result.RetryAfterMs);
    }

    private static ChatAck Unsupported(ChatCommand command, string type)
        => ChatAck.Failure(command, ChatError.Create(
            ChatErrorCodes.Unsupported,
            $"Discord does not support '{type}' in the current configuration."));

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class TransportObserver(DiscordChatUpstream owner) : IObserver<DiscordTransportEvent>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
            owner.SetError("transport_stream", error.Message);
            owner.PublishStatus(UpstreamState.Error, owner.LastError);
        }

        public void OnNext(DiscordTransportEvent value) => owner.OnTransportEvent(value);
    }
}

/// <summary>Serializable status payload for <c>discord status</c>.</summary>
public sealed record DiscordStatusSnapshot(
    Source Source,
    bool Connected,
    string? LastError,
    int? GuildCount,
    int? ChannelCount,
    int? LatencyMs,
    Capabilities Capabilities,
    IReadOnlyList<string> Guilds,
    IReadOnlyList<string> Channels,
    IReadOnlyList<string> Users,
    bool RequireMention,
    bool SlashCommands);
