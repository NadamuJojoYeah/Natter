using Microsoft.Extensions.Logging;
using Natter.Abstractions.Plugins;
using Natter.Chat;

namespace Natter.Application.Chat;

/// <summary>
/// Application-layer bridge from NLMP events to feature responders and back to NLMP
/// commands. Events remain facts: the orchestrator only issues commands through the
/// dispatcher, never by calling a platform SDK.
/// </summary>
public sealed class ChatOrchestrator : IChatMessageHandler, IPluginLifecycle, IDisposable
{
    private readonly IChatEventSource _events;
    private readonly IChatCommandDispatcher _dispatcher;
    private readonly IChatResponder _responder;
    private readonly ChatOrchestrationOptions _options;
    private readonly ILogger<ChatOrchestrator>? _logger;
    private readonly Lock _gate = new();
    private IDisposable? _subscription;

    public ChatOrchestrator(
        IChatEventSource events,
        IChatCommandDispatcher dispatcher,
        IChatResponder responder,
        ChatOrchestrationOptions options,
        ILogger<ChatOrchestrator>? logger = null)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _responder = responder ?? throw new ArgumentNullException(nameof(responder));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    public ValueTask StartAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _subscription ??= _events.Events.Subscribe(new OrchestratorObserver(this, _logger));
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _subscription?.Dispose();
            _subscription = null;
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask HandleAsync(ChatEvent chatEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatEvent);

        if (chatEvent is InteractionCreatedEvent interactionEvent)
        {
            var interaction = interactionEvent.Payload.Interaction;
            if (interaction.User.Kind == UserKind.Bot)
                return;

            var interactionCommand = await _responder.RespondAsync(chatEvent, cancellationToken).ConfigureAwait(false);
            if (interactionCommand is not null)
            {
                var interactionAck = await _dispatcher.ExecuteAsync(interactionCommand, cancellationToken)
                    .ConfigureAwait(false);
                if (!interactionAck.Ok)
                {
                    _logger?.LogWarning(
                        "NLMP interaction response for {Source} failed: {ErrorCode} {ErrorMessage}",
                        chatEvent.Source.Key,
                        interactionAck.Error?.Code,
                        interactionAck.Error?.Message);
                }
            }

            return;
        }

        var message = chatEvent switch
        {
            MessageCreatedEvent created => created.Payload.Message,
            MessageUpdatedEvent updated when _options.ProcessEditedMessages => updated.Payload.Message,
            _ => null,
        };

        if (message is null)
            return;

        if (message.Sender.Kind == UserKind.Bot && !_options.ProcessBotMessages)
            return;

        if (message.Sender.Kind == UserKind.System && !_options.ProcessSystemMessages)
            return;

        if (_options.IgnoreSelfMessages && message.Sender.Id == chatEvent.Source.SelfId)
            return;

        if (_options.RequireMentionOrDirect
            && message.Conversation.Kind != ConversationKind.Direct
            && !MentionsSelf(message, chatEvent.Source.SelfId))
        {
            _logger?.LogDebug(
                "NLMP message dropped by mention gate: source={Source} conversation={Conversation} message={MessageId}",
                chatEvent.Source.Key,
                message.Conversation.Id,
                message.Id);
            return;
        }

        var command = await _responder.RespondAsync(chatEvent, cancellationToken).ConfigureAwait(false);
        if (command is null)
            return;

        var ack = await _dispatcher.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        if (!ack.Ok)
        {
            _logger?.LogWarning(
                "NLMP reply for {Source} failed: {ErrorCode} {ErrorMessage}",
                chatEvent.Source.Key,
                ack.Error?.Code,
                ack.Error?.Message);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _subscription?.Dispose();
            _subscription = null;
        }
    }

    private static bool MentionsSelf(Message message, string selfId)
        => message.Content.Mentions is { Count: > 0 } mentions
           && mentions.Any(mention => mention.Kind == MentionKind.User && mention.Id == selfId);

    private sealed class OrchestratorObserver(
        ChatOrchestrator orchestrator,
        ILogger<ChatOrchestrator>? logger) : IObserver<ChatEvent>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
            => logger?.LogError(error, "The NLMP event stream failed; the chat orchestrator stopped.");

        public void OnNext(ChatEvent value)
            => _ = HandleAsync(value);

        private async Task HandleAsync(ChatEvent chatEvent)
        {
            try
            {
                await orchestrator.HandleAsync(chatEvent).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "The chat orchestrator failed to handle event {EventId}.", chatEvent.Id);
            }
        }
    }
}
