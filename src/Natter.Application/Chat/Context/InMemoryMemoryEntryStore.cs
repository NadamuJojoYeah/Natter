using Natter.Chat;

namespace Natter.Application.Chat;

/// <summary>
/// In-memory <see cref="IMemoryEntryStore"/> for hosts without a state directory and for tests.
/// Behaviour mirrors <c>SqliteMemoryEntryStore</c>: repeat detection by content hash, power-law
/// retention, archive instead of delete. Only the index differs (linear scan instead of FTS5).
/// </summary>
public sealed class InMemoryMemoryEntryStore : IMemoryEntryStore
{
    private readonly Dictionary<string, MemoryEntry> _items = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly Func<DateTimeOffset> _clock;

    public InMemoryMemoryEntryStore()
        : this(null)
    {
    }

    /// <summary>Test seam: pins "now" so decay assertions are deterministic.</summary>
    public InMemoryMemoryEntryStore(Func<DateTimeOffset>? clock) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public ValueTask<MemoryEntry> WriteAsync(
        MemoryEntryWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Content);

        var now = _clock();
        var scopeType = request.Scope ?? MemoryScope.DefaultWriteScope();
        var scopeId = MemoryScope.Id(request.Source, request.Conversation, request.User, scopeType);
        var content = request.Content.Trim();
        var hash = MemoryEntryMapping.Hash(request.Type, scopeId, content);

        lock (_gate)
        {
            var existing = _items.Values.FirstOrDefault(item =>
                string.Equals(item.ScopeId, scopeId, StringComparison.Ordinal)
                && item.Type == request.Type
                && string.Equals(item.ContentHash, hash, StringComparison.Ordinal));

            if (existing is not null)
            {
                var reinforced = existing with
                {
                    StabilityDays = MemoryStability.Reinforce(existing.StabilityDays),
                    ReinforceCount = existing.ReinforceCount + 1,
                    Confidence = Math.Clamp(Math.Max(existing.Confidence, request.Confidence), 0, 1),
                    Archived = false,
                    UpdatedAt = now,
                };
                _items[reinforced.Id] = reinforced;
                return ValueTask.FromResult(reinforced);
            }

            var entry = new MemoryEntry(
                "me_" + Guid.NewGuid().ToString("N")[..16],
                request.Type,
                scopeType,
                scopeId,
                content,
                hash,
                MemoryDecay.InitialStabilityDays(request.Type),
                ReinforceCount: 0,
                Math.Clamp(request.Confidence, 0, 1),
                Archived: false,
                SupersedesId: null,
                request.SourceMessageIds,
                string.IsNullOrWhiteSpace(request.Sensitivity) ? "normal" : request.Sensitivity,
                now,
                now,
                LastRecalledAt: null)
            {
                OwnerId = request.User.Id,
            };

            _items[entry.Id] = entry;
            return ValueTask.FromResult(entry);
        }
    }

    public ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(
        MemoryEntryQuery query,
        CancellationToken cancellationToken = default)
    {
        var now = _clock();
        var limit = Math.Max(0, query.Limit);

        lock (_gate)
        {
            IEnumerable<MemoryEntry> items = _items.Values
                .Where(item => query.IncludeArchived || !item.Archived)
                .Where(item => query.IncludePrivate
                               || !string.Equals(item.Sensitivity, "private", StringComparison.OrdinalIgnoreCase))
                .Where(item => ScopeVisible(item, query))
                .Where(item => query.Types is null || query.Types.Count == 0 || query.Types.Contains(item.Type));

            // Relevance × power-law retention, matching the SQLite path (§9.1). No category weight:
            // the per-type decay exponent already carries the prior (§9.1.1).
            if (!string.IsNullOrWhiteSpace(query.Query))
            {
                var tokens = MemoryScoring.Tokenize(query.Query);
                if (tokens.Count > 0)
                {
                    var scored = items
                        .Select(item => (Item: item, Score: (double)Score(item.Content, tokens) * item.WeightAt(now)))
                        .Where(pair => pair.Score > 0)
                        .OrderByDescending(pair => pair.Score)
                        .ThenByDescending(pair => pair.Item.UpdatedAt)
                        .Take(limit)
                        .Select(pair => pair.Item)
                        .ToArray();
                    if (scored.Length > 0)
                        return ValueTask.FromResult<IReadOnlyList<MemoryEntry>>(scored);

                    // Same fallback as the SQLite store: no lexical hit falls back to recency so a
                    // turn still sees recent memory (P6 §8.1 enhancement, never a hard dependency).
                }
            }

            var recent = items
                .OrderByDescending(item => item.UpdatedAt)
                .Take(limit)
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<MemoryEntry>>(recent);
        }
    }

    public ValueTask<MemoryEntry?> GetAsync(
        Source source,
        Conversation conversation,
        User user,
        string id,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(id, out var entry))
                return ValueTask.FromResult<MemoryEntry?>(null);

            var visible = new MemoryEntryQuery(source, conversation, user) { IncludeArchived = true };
            return ValueTask.FromResult(ScopeVisible(entry, visible) ? entry : null);
        }
    }

    public ValueTask<MemoryArchiveResult> ArchiveAsync(
        Source source,
        Conversation conversation,
        User user,
        string id,
        CancellationToken cancellationToken = default)
    {
        var now = _clock();
        lock (_gate)
        {
            if (!_items.TryGetValue(id, out var entry))
                return ValueTask.FromResult(MemoryArchiveResult.NotFound);

            var visible = new MemoryEntryQuery(source, conversation, user) { IncludeArchived = true };
            if (!ScopeVisible(entry, visible))
                return ValueTask.FromResult(MemoryArchiveResult.NotFound);

            // NULL owner = no recorded attribution; archiving is reversible so it stays allowed.
            if (entry.OwnerId is not null && !string.Equals(entry.OwnerId, user.Id, StringComparison.Ordinal))
                return ValueTask.FromResult(MemoryArchiveResult.NotOwner);

            _items[id] = entry with { Archived = true, UpdatedAt = now };
            return ValueTask.FromResult(MemoryArchiveResult.Archived);
        }
    }

    public ValueTask ReinforceRecallAsync(
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
    {
        var now = _clock();
        lock (_gate)
        {
            foreach (var id in ids)
            {
                if (!_items.TryGetValue(id, out var entry))
                    continue;

                _items[id] = entry with
                {
                    StabilityDays = entry.StabilityDays * (1 + MemoryStability.RecallGrowth),
                    LastRecalledAt = now,
                };
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<MemoryEntryExport> ExportAsync(
        Source source,
        Conversation conversation,
        User user,
        bool includePrivate,
        CancellationToken cancellationToken = default)
    {
        var query = new MemoryEntryQuery(source, conversation, user)
        {
            IncludeArchived = true,
            IncludePrivate = includePrivate,
            Limit = int.MaxValue,
        };

        var items = SearchAsync(query, cancellationToken).AsTask().GetAwaiter().GetResult();
        return ValueTask.FromResult(MemoryEntryExport.Create(items, _clock()));
    }

    public ValueTask<int> ImportAsync(
        Source source,
        Conversation conversation,
        User user,
        MemoryEntryExport export,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(export);
        export.EnsureReadable();

        var visible = new MemoryEntryQuery(source, conversation, user) { IncludeArchived = true };
        var imported = 0;
        lock (_gate)
        {
            foreach (var item in export.Items)
            {
                if (!ScopeVisible(item, visible))
                    continue;
                _items[item.Id] = item;
                imported++;
            }
        }

        return ValueTask.FromResult(imported);
    }

    /// <summary>All entries regardless of scope. For persistence and diagnostics, not for recall.</summary>
    public IReadOnlyList<MemoryEntry> Snapshot()
    {
        lock (_gate)
            return _items.Values.OrderBy(entry => entry.CreatedAt).ToArray();
    }

    /// <summary>Replaces the contents (used when loading a persisted snapshot).</summary>
    public void Restore(IEnumerable<MemoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        lock (_gate)
        {
            _items.Clear();
            foreach (var entry in entries)
                _items[entry.Id] = entry;
        }
    }

    private static int Score(string content, IReadOnlyList<string> tokens)
    {
        var haystack = content.ToLowerInvariant();
        var matches = tokens.Count(token => haystack.Contains(token, StringComparison.Ordinal));
        return matches;
    }

    private static bool ScopeVisible(MemoryEntry entry, MemoryEntryQuery query)
    {
        if (query.Scope is { } scope)
        {
            return entry.ScopeType == scope
                   && string.Equals(
                       entry.ScopeId,
                       MemoryScope.Id(query.Source, query.Conversation, query.User, scope),
                       StringComparison.Ordinal);
        }

        return entry.ScopeType switch
        {
            MemoryScopeType.User => entry.ScopeId == MemoryScope.Id(
                query.Source, query.Conversation, query.User, MemoryScopeType.User),
            MemoryScopeType.Conversation => entry.ScopeId == MemoryScope.Id(
                query.Source, query.Conversation, query.User, MemoryScopeType.Conversation),
            MemoryScopeType.Guild => entry.ScopeId == MemoryScope.Id(
                query.Source, query.Conversation, query.User, MemoryScopeType.Guild),
            MemoryScopeType.Global => entry.ScopeId == MemoryScope.Id(
                query.Source, query.Conversation, query.User, MemoryScopeType.Global),
            _ => false,
        };
    }
}
