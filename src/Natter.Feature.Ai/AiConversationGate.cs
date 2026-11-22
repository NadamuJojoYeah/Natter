namespace Natter.Feature.Ai;

/// <summary>
/// Serializes requests per conversation and caps global in-flight requests. Conversation gates are
/// pruned when idle / above a soft entry cap, and <see cref="Dispose"/> waits for in-flight leases
/// (bounded) instead of disposing semaphores under them.
/// </summary>
public sealed class AiConversationGate : IDisposable
{
    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Users;

        public DateTimeOffset LastUsed;
    }

    private readonly SemaphoreSlim _global;
    private readonly Dictionary<string, Entry> _conversations = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly ManualResetEventSlim _idle = new(initialState: true);
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _disposeTimeout;
    private readonly int _maxEntries;
    private int _activeOperations;
    private bool _disposed;

    public AiConversationGate(AiResponderOptions options)
    {
        var configured = options ?? new AiResponderOptions();
        var concurrency = Math.Max(1, configured.MaxConcurrentRequests);
        _global = new SemaphoreSlim(concurrency, concurrency);
        _maxEntries = Math.Max(1, configured.MaxConversationEntries);
        _idleTimeout = TimeSpan.FromSeconds(Math.Max(0, configured.ConversationIdleSeconds));
        _disposeTimeout = TimeSpan.FromMilliseconds(Math.Max(0, configured.GateDisposeTimeoutMilliseconds));
    }

    /// <summary>Current number of retained per-conversation gates (diagnostics/tests).</summary>
    public int ConversationCount
    {
        get { lock (_gate) return _conversations.Count; }
    }

    /// <summary>Operations that have entered AcquireAsync and not yet released their lease.</summary>
    public int ActiveOperationCount
    {
        get { lock (_gate) return _activeOperations; }
    }

    public async ValueTask<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(AiConversationGate));

            _activeOperations++;
            _idle.Reset();
        }

        Entry? entry = null;
        var entrySelected = false;
        try
        {
            await _global.WaitAsync(cancellationToken).ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            lock (_gate)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(AiConversationGate));

                PruneLocked(now, ensureCapacity: true);

                if (!_conversations.TryGetValue(key, out entry))
                {
                    entry = new Entry { LastUsed = now };
                    _conversations[key] = entry;
                }

                entry.Users++;
                entry.LastUsed = now;
                entrySelected = true;
            }

            await entry!.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (entrySelected && entry is not null)
                ReleaseEntryReference(entry);

            SafeRelease(_global);
            CompleteOperation();
            throw;
        }

        return new Lease(this, entry!);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        // Wait for leases/acquires to drain. If they outlive the timeout, keep the semaphores
        // alive so a late Lease.Dispose() can still release them without ObjectDisposedException.
        _idle.Wait(_disposeTimeout);

        lock (_gate)
        {
            if (_activeOperations != 0)
                return;

            _global.Dispose();
            foreach (var entry in _conversations.Values)
            {
                try
                {
                    entry.Semaphore.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            _conversations.Clear();
        }
    }

    private void LeaseReleased(Entry entry)
    {
        SafeRelease(entry.Semaphore);
        SafeRelease(_global);

        lock (_gate)
        {
            entry.Users = Math.Max(0, entry.Users - 1);
            entry.LastUsed = DateTimeOffset.UtcNow;
            PruneLocked(entry.LastUsed, ensureCapacity: false);

            _activeOperations = Math.Max(0, _activeOperations - 1);
            if (_activeOperations == 0)
                _idle.Set();
        }
    }

    private void ReleaseEntryReference(Entry entry)
    {
        lock (_gate)
        {
            entry.Users = Math.Max(0, entry.Users - 1);
            entry.LastUsed = DateTimeOffset.UtcNow;
            PruneLocked(entry.LastUsed, ensureCapacity: false);
        }
    }

    private void CompleteOperation()
    {
        lock (_gate)
        {
            _activeOperations = Math.Max(0, _activeOperations - 1);
            if (_activeOperations == 0)
                _idle.Set();
        }
    }

    private void PruneLocked(DateTimeOffset now, bool ensureCapacity)
    {
        var cutoff = now - _idleTimeout;
        var remove = new List<string>();

        foreach (var (key, entry) in _conversations)
        {
            if (entry.Users == 0 && entry.LastUsed <= cutoff)
                remove.Add(key);
        }

        if (ensureCapacity && _conversations.Count - remove.Count >= _maxEntries)
        {
            foreach (var (key, entry) in _conversations
                         .Where(pair => entryIsIdle(pair.Value) && !remove.Contains(pair.Key))
                         .OrderBy(pair => pair.Value.LastUsed))
            {
                remove.Add(key);
                if (_conversations.Count - remove.Count < _maxEntries)
                    break;
            }
        }

        foreach (var key in remove)
        {
            if (!_conversations.Remove(key, out var entry))
                continue;

            try
            {
                entry.Semaphore.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        static bool entryIsIdle(Entry entry) => entry.Users == 0;
    }

    private static void SafeRelease(SemaphoreSlim semaphore)
    {
        try
        {
            semaphore.Release();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private sealed class Lease(AiConversationGate owner, Entry entry) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            owner.LeaseReleased(entry);
        }
    }
}
