using System.Threading.Channels;
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
    private readonly Lock _tailsLock = new();
    private readonly Dictionary<string, Task> _tails = new(StringComparer.Ordinal);
    private IDisposable? _subscription;
    private Channel<ChatEvent>? _queue;
    private CancellationTokenSource? _lifecycle;
    private Task? _eventDispatcher;
    private SemaphoreSlim? _workerSlots;
    private bool _started;
    private long _enqueued;
    private long _dropped;
    private long _processed;
    private long _failed;
    private long _drainTimeouts;
    private long _abandoned;

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

    /// <summary>Inbound events accepted into the bounded queue.</summary>
    public long EnqueuedCount => Interlocked.Read(ref _enqueued);

    /// <summary>Events rejected because the bounded queue was full and one because of lifecycle stop.</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>Events handled successfully.</summary>
    public long ProcessedCount => Interlocked.Read(ref _processed);

    /// <summary>Events whose handler threw (logged and counted, never silently swallowed).</summary>
    public long FailedCount => Interlocked.Read(ref _failed);

    /// <summary>StopAsync drain timeouts (in-flight work was cancelled).</summary>
    public long DrainTimeoutCount => Interlocked.Read(ref _drainTimeouts);

    /// <summary>Events left in the queue when the drain timed out.</summary>
    public long AbandonedCount => Interlocked.Read(ref _abandoned);

    /// <summary>Current queue depth (0 when not started).</summary>
    public int QueueLength => Volatile.Read(ref _queue)?.Reader.Count ?? 0;

    public ValueTask StartAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_started)
                return ValueTask.CompletedTask;

            _started = true;
            // Host lifecycle token: workers stop when the host stops, even if StopAsync is missed.
            _lifecycle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var queue = Channel.CreateBounded<ChatEvent>(new BoundedChannelOptions(Math.Max(1, _options.QueueCapacity))
            {
                // TryWrite returns false when full (no blocking on the Rx thread) and the caller
                // counts/logs the drop; "Wait" keeps the policy explicit instead of silent loss.
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
            _queue = queue;
            _workerSlots = new SemaphoreSlim(Math.Max(1, _options.WorkerCount), Math.Max(1, _options.WorkerCount));
            // One dispatcher reads the channel in FIFO order and assigns each event to its
            // conversation chain synchronously; chains run in parallel under a fixed slot count.
            _eventDispatcher = Task.Run(() => DispatchLoopAsync(queue.Reader, _lifecycle.Token), CancellationToken.None);
            _subscription = _events.Events.Subscribe(new OrchestratorObserver(this, _logger));
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? lifecycle;
        Channel<ChatEvent>? queue;
        Task? dispatcher;
        SemaphoreSlim? workerSlots;
        IDisposable? subscription;
        lock (_gate)
        {
            if (!_started)
                return;

            _started = false;
            subscription = _subscription;
            _subscription = null;
            lifecycle = _lifecycle;
            _lifecycle = null;
            queue = _queue;
            _queue = null;
            dispatcher = _eventDispatcher;
            _eventDispatcher = null;
            workerSlots = _workerSlots;
            _workerSlots = null;
        }

        subscription?.Dispose();
        queue?.Writer.TryComplete();

        var drainTimeout = _options.DrainTimeout <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(1)
            : _options.DrainTimeout;
        try
        {
            var drain = DrainAsync(dispatcher);
            await drain.WaitAsync(drainTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Interlocked.Increment(ref _drainTimeouts);
            Interlocked.Add(ref _abandoned, queue?.Reader.Count ?? 0);
            lifecycle?.Cancel();
            // Give cancelled handlers a short, bounded window to observe the token.
            await Task.WhenAny(DrainAsync(dispatcher), Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Interlocked.Add(ref _abandoned, queue?.Reader.Count ?? 0);
            lifecycle?.Cancel();
            throw;
        }
        finally
        {
            workerSlots?.Dispose();
            lifecycle?.Dispose();
            lock (_tailsLock)
                _tails.Clear();
        }
    }

    internal bool Enqueue(ChatEvent chatEvent)
    {
        var queue = Volatile.Read(ref _queue);
        if (queue is null)
        {
            Interlocked.Increment(ref _dropped);
            return false;
        }

        if (queue.Writer.TryWrite(chatEvent))
        {
            Interlocked.Increment(ref _enqueued);
            return true;
        }

        Interlocked.Increment(ref _dropped);
        _logger?.LogWarning(
            "Chat orchestrator queue full (capacity={Capacity}); event {EventId} dropped.",
            _options.QueueCapacity,
            chatEvent.Id);
        return false;
    }

    private async Task DispatchLoopAsync(ChannelReader<ChatEvent> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var chatEvent in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                Route(chatEvent, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task DrainAsync(Task? dispatcher)
    {
        if (dispatcher is not null)
            await dispatcher.ConfigureAwait(false);

        Task[] tails;
        lock (_tailsLock)
            tails = _tails.Values.ToArray();
        if (tails.Length > 0)
            await Task.WhenAll(tails).ConfigureAwait(false);
    }

    private void Route(ChatEvent chatEvent, CancellationToken cancellationToken)
    {
        var key = ConversationKey(chatEvent);
        Task chain;
        lock (_tailsLock)
        {
            _tails.TryGetValue(key, out var previous);
            chain = ProcessAfterAsync(previous ?? Task.CompletedTask, chatEvent, cancellationToken);
            _tails[key] = chain;
        }

        _ = chain.ContinueWith(
            _ =>
            {
                lock (_tailsLock)
                {
                    if (_tails.TryGetValue(key, out var current) && ReferenceEquals(current, chain))
                        _tails.Remove(key);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ProcessAfterAsync(Task previous, ChatEvent chatEvent, CancellationToken cancellationToken)
    {
        // Same-conversation events await their predecessor, so create/updated ordering is stable.
        await previous.ConfigureAwait(false);
        var slots = Volatile.Read(ref _workerSlots);
        try
        {
            if (slots is not null)
                await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await HandleAsync(chatEvent, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _processed);
            }
            finally
            {
                slots?.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failed);
            _logger?.LogError(ex, "The chat orchestrator failed to handle event {EventId}.", chatEvent.Id);
        }
    }

    private static string ConversationKey(ChatEvent chatEvent)
    {
        var conversationId = chatEvent switch
        {
            InteractionCreatedEvent interaction => interaction.Payload.Interaction.Conversation.Id,
            MessageCreatedEvent created => created.Payload.Message.Conversation.Id,
            MessageUpdatedEvent updated => updated.Payload.Message.Conversation.Id,
            _ => "global",
        };
        return chatEvent.Source.Key + "|" + conversationId;
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
            _started = false;
            _lifecycle?.Cancel();
            _lifecycle?.Dispose();
            _lifecycle = null;
            _queue = null;
            _eventDispatcher = null;
            _workerSlots = null;
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
            => orchestrator.Enqueue(value);
    }
}
