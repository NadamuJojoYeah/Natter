using System.Reactive.Subjects;
using Natter.Abstractions.Ports;
using Natter.Chat;

namespace Natter.Infrastructure.Chat;

/// <summary>
/// In-process NLMP upstream used by tests, demos and embedded hosts. It implements the
/// degradation contract locally as well, so it can be swapped for a real adapter without
/// changing callers. No transport, credentials or platform calls are involved.
/// </summary>
public sealed class InMemoryChatUpstream : IChatUpstream, IDisposable
{
    private readonly Subject<ChatEvent> _subject = new();
    private readonly ISubject<ChatEvent> _events;
    private readonly IClock _clock;
    private readonly ChatIdempotencyCache _idempotency;
    private readonly Dictionary<(string ConversationId, string MessageId), Message> _messages = [];
    private readonly Lock _gate = new();
    private long _sequence;
    private bool _connected;
    private bool _disposed;

    public InMemoryChatUpstream(Source source, Capabilities capabilities, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(clock);

        if (string.IsNullOrWhiteSpace(source.Protocol)
            || string.IsNullOrWhiteSpace(source.Platform)
            || string.IsNullOrWhiteSpace(source.SelfId))
        {
            throw new ArgumentException(
                "The source requires non-empty protocol, platform and selfId.",
                nameof(source));
        }

        Source = source;
        Capabilities = capabilities;
        _clock = clock;
        _events = Subject.Synchronize(_subject);
        _idempotency = new ChatIdempotencyCache(clock: () => _clock.UtcNow);
        BotUser = new User(source.SelfId, "Natter", UserKind.Bot, "Natter");
    }

    public Source Source { get; }

    public Capabilities Capabilities { get; }

    public User BotUser { get; }

    public bool IsConnected => _connected;

    public IObservable<ChatEvent> Events => _events;

    public IReadOnlyList<Message> Messages
    {
        get
        {
            lock (_gate)
                return _messages.Values.OrderBy(message => message.CreatedAt).ToArray();
        }
    }

    public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (_connected)
            return ValueTask.CompletedTask;

        _connected = true;
        PublishStatus(UpstreamState.Ready);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_connected)
            return ValueTask.CompletedTask;

        _connected = false;
        PublishStatus(UpstreamState.Disconnected);
        return ValueTask.CompletedTask;
    }

    public ValueTask<Capabilities> GetCapabilitiesAsync(
        Conversation? conversation = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Capabilities);
    }

    public ValueTask<ChatAck> ExecuteAsync(ChatCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_connected)
        {
            return ValueTask.FromResult(Fail(
                command,
                ChatErrorCodes.Unavailable,
                $"In-memory upstream '{Source.Key}' is not connected."));
        }

        if (command.Source != Source)
        {
            return ValueTask.FromResult(Fail(
                command,
                ChatErrorCodes.InvalidRequest,
                $"Command source '{command.Source.Key}' does not match upstream '{Source.Key}'."));
        }

        var key = command.IdempotencyKey;
        if (!string.IsNullOrWhiteSpace(key) && _idempotency.TryGet(key, out var cached) && cached is not null)
            return ValueTask.FromResult(cached);

        var ack = command switch
        {
            MessageSendCommand send => Send(send),
            MessageUpdateCommand update => Update(update),
            MessageDeleteCommand delete => Delete(delete),
            TypingStartCommand typing => Typing(typing),
            InteractionRespondCommand respond => Respond(respond),
            _ => Fail(command, ChatErrorCodes.Unsupported, $"Command type '{command.Type}' is not supported."),
        };

        if (ack.Ok && !string.IsNullOrWhiteSpace(key))
            _idempotency.Store(key, ack);

        return ValueTask.FromResult(ack);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _subject.Dispose();
    }

    private ChatAck Send(MessageSendCommand command)
    {
        var conversation = command.Payload.Conversation;
        if (conversation is null || string.IsNullOrWhiteSpace(conversation.Id))
            return Fail(command, ChatErrorCodes.InvalidRequest, "message.send requires conversation.id.");

        if (command.Payload.Content is null)
            return Fail(command, ChatErrorCodes.InvalidRequest, "message.send requires content.");

        var content = ChatCapabilityDegrader.DegradeContent(command.Payload.Content, Capabilities);
        var errors = content.Validate();
        if (errors.Count > 0)
            return Fail(command, ChatErrorCodes.InvalidRequest, "message.send content is invalid.");

        var chunks = ChatTextChunker.Chunk(content, Capabilities.MaxMessageLength);
        var messageIds = new List<string>(chunks.Count);

        for (var index = 0; index < chunks.Count; index++)
        {
            var replyTo = index == 0 && Capabilities.Reply ? command.Payload.ReplyTo : null;
            var message = new Message(
                NewMessageId(),
                conversation,
                BotUser,
                chunks[index],
                _clock.UtcNow,
                replyTo);

            lock (_gate)
                _messages[(conversation.Id, message.Id)] = message;

            messageIds.Add(message.Id);
            PublishMessage(message, created: true);
        }

        return ChatAck.Success(command, messageIds, _clock.UtcNow);
    }

    private ChatAck Update(MessageUpdateCommand command)
    {
        if (!Capabilities.SupportsEdit)
            return Fail(command, ChatErrorCodes.Unsupported, "Message editing is not supported by this upstream.");

        var conversation = command.Payload.Conversation;
        if (conversation is null || string.IsNullOrWhiteSpace(conversation.Id))
            return Fail(command, ChatErrorCodes.InvalidRequest, "message.update requires conversation.id.");

        var conversationId = conversation.Id;
        var messageId = command.Payload.MessageId;
        if (string.IsNullOrWhiteSpace(messageId))
            return Fail(command, ChatErrorCodes.InvalidRequest, "message.update requires messageId.");

        Message? message;
        lock (_gate)
            _messages.TryGetValue((conversationId, messageId), out message);

        if (message is null)
            return Fail(command, ChatErrorCodes.NotFound, $"Message '{messageId}' does not exist in conversation '{conversationId}'.");

        if (command.Payload.Content is null)
            return Fail(command, ChatErrorCodes.InvalidRequest, "message.update requires content.");

        var content = ChatCapabilityDegrader.DegradeContent(command.Payload.Content, Capabilities);
        if (content.Validate().Count > 0)
            return Fail(command, ChatErrorCodes.InvalidRequest, "message.update content is invalid.");

        var updated = message with { Content = content, EditedAt = _clock.UtcNow };

        lock (_gate)
            _messages[(conversationId, messageId)] = updated;

        PublishMessage(updated, created: false);
        return ChatAck.Success(command, [messageId], _clock.UtcNow);
    }

    private ChatAck Delete(MessageDeleteCommand command)
    {
        if (!Capabilities.Delete)
            return Fail(command, ChatErrorCodes.Unsupported, "Message deletion is not supported by this upstream.");

        var conversation = command.Payload.Conversation;
        if (conversation is null || string.IsNullOrWhiteSpace(conversation.Id))
            return Fail(command, ChatErrorCodes.InvalidRequest, "message.delete requires conversation.id.");

        var conversationId = conversation.Id;
        var messageId = command.Payload.MessageId;
        if (string.IsNullOrWhiteSpace(messageId))
            return Fail(command, ChatErrorCodes.InvalidRequest, "message.delete requires messageId.");

        Message? message;
        lock (_gate)
        {
            _messages.TryGetValue((conversationId, messageId), out message);
            if (message is not null)
                _messages.Remove((conversationId, messageId));
        }

        if (message is null)
            return Fail(command, ChatErrorCodes.NotFound, $"Message '{messageId}' does not exist in conversation '{conversationId}'.");

        var deletedAt = _clock.UtcNow;
        var sequence = Interlocked.Increment(ref _sequence);
        _events.OnNext(new MessageDeletedEvent
        {
            Id = ChatEventId.Derive(Source, ChatEventTypes.MessageDeleted, sequence),
            Source = Source,
            Sequence = sequence,
            OccurredAt = deletedAt,
            ReceivedAt = deletedAt,
            Payload = new MessageDeletedPayload(message.Conversation, messageId, deletedAt),
        });

        return ChatAck.Success(command, [messageId], deletedAt);
    }

    private ChatAck Typing(TypingStartCommand command)
    {
        if (!Capabilities.Typing)
            return Fail(command, ChatErrorCodes.Unsupported, "Typing indicators are not supported by this upstream.");

        return ChatAck.Success(command, deliveredAt: _clock.UtcNow);
    }

    private ChatAck Respond(InteractionRespondCommand command)
    {
        var payload = command.Payload;
        if (string.IsNullOrWhiteSpace(payload.InteractionId))
            return Fail(command, ChatErrorCodes.InvalidRequest, "interaction.respond requires interactionId.");

        if (!Capabilities.Buttons && !Capabilities.Modals && !Capabilities.SlashCommands)
            return Fail(command, ChatErrorCodes.Unsupported, "Interactive responses are not supported by this upstream.");

        if (string.IsNullOrWhiteSpace(payload.ReplyToken))
            return Fail(command, ChatErrorCodes.InvalidRequest, "interaction.respond requires replyToken.");

        return ChatAck.Success(command, deliveredAt: _clock.UtcNow);
    }

    private void PublishMessage(Message message, bool created)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        var type = created ? ChatEventTypes.MessageCreated : ChatEventTypes.MessageUpdated;
        var payload = new MessagePayload(message);

        ChatEvent chatEvent = created
            ? new MessageCreatedEvent
            {
                Id = ChatEventId.Derive(Source, type, sequence),
                Source = Source,
                Sequence = sequence,
                OccurredAt = message.CreatedAt,
                ReceivedAt = _clock.UtcNow,
                Payload = payload,
            }
            : new MessageUpdatedEvent
            {
                Id = ChatEventId.Derive(Source, type, sequence),
                Source = Source,
                Sequence = sequence,
                OccurredAt = message.EditedAt ?? _clock.UtcNow,
                ReceivedAt = _clock.UtcNow,
                Payload = payload,
            };

        _events.OnNext(chatEvent);
    }

    private void PublishStatus(UpstreamState state)
    {
        var now = _clock.UtcNow;
        var sequence = Interlocked.Increment(ref _sequence);

        _events.OnNext(new UpstreamStatusEvent
        {
            Id = ChatEventId.Derive(Source, ChatEventTypes.UpstreamStatus, sequence),
            Source = Source,
            Sequence = sequence,
            OccurredAt = now,
            ReceivedAt = now,
            Payload = new UpstreamStatusPayload(state),
        });
    }

    private static string NewMessageId() => $"mem-{Guid.NewGuid():N}";

    private static ChatAck Fail(ChatCommand command, string code, string message)
        => ChatAck.Failure(command, ChatError.Create(code, message));
}
