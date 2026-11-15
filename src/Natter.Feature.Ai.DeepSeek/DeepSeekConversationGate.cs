namespace Natter.Feature.Ai.DeepSeek;

/// <summary>
/// Serializes requests per conversation and caps global in-flight requests. This prevents
/// overlapping AI calls from racing on history or producing out-of-order replies.
/// </summary>
public sealed class DeepSeekConversationGate : IDisposable
{
    private readonly SemaphoreSlim _global;
    private readonly Dictionary<string, SemaphoreSlim> _conversations = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private bool _disposed;

    public DeepSeekConversationGate(DeepSeekOptions options)
        => _global = new SemaphoreSlim(
            Math.Max(1, options?.MaxConcurrentRequests ?? 2),
            Math.Max(1, options?.MaxConcurrentRequests ?? 2));

    public async ValueTask<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _global.WaitAsync(cancellationToken).ConfigureAwait(false);

        SemaphoreSlim? conversation;
        lock (_gate)
        {
            if (!_conversations.TryGetValue(key, out conversation))
            {
                conversation = new SemaphoreSlim(1, 1);
                _conversations[key] = conversation;
            }
        }

        try
        {
            await conversation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _global.Release();
            throw;
        }

        return new Lease(conversation, _global);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _global.Dispose();

        lock (_gate)
        {
            foreach (var semaphore in _conversations.Values)
                semaphore.Dispose();
            _conversations.Clear();
        }
    }

    private sealed class Lease(SemaphoreSlim conversation, SemaphoreSlim global) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            conversation.Release();
            global.Release();
        }
    }
}
