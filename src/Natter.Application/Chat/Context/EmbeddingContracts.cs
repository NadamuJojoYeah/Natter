using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Natter.Application.Chat;

/// <summary>Thread-safe in-memory provider catalog used by the NPS host and tests.</summary>
public sealed class EmbeddingProviderCatalog : IEmbeddingProviderCatalog
{
    private readonly Dictionary<string, IEmbeddingProvider> _providers = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public IReadOnlyList<(string Reference, IEmbeddingProvider Provider)> Providers
    {
        get { lock (_gate) return _providers.Select(pair => (pair.Key, pair.Value)).ToArray(); }
    }

    public void Register(string reference, IEmbeddingProvider provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(provider);
        lock (_gate)
            _providers[reference] = provider;
    }

    public void Unregister(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return;

        lock (_gate)
            _providers.Remove(reference);
    }

    public bool TryGet(string reference, out IEmbeddingProvider? provider)
    {
        lock (_gate)
            return _providers.TryGetValue(reference, out provider);
    }

    public void Clear()
    {
        lock (_gate)
            _providers.Clear();
    }
}

/// <summary>Thread-safe in-memory rerank catalog used by the NPS host and tests.</summary>
public sealed class RerankProviderCatalog : IRerankProviderCatalog
{
    private readonly Dictionary<string, IRerankProvider> _providers = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public IReadOnlyList<(string Reference, IRerankProvider Provider)> Providers
    {
        get { lock (_gate) return _providers.Select(pair => (pair.Key, pair.Value)).ToArray(); }
    }

    public void Register(string reference, IRerankProvider provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(provider);
        lock (_gate)
            _providers[reference] = provider;
    }

    public void Unregister(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return;

        lock (_gate)
            _providers.Remove(reference);
    }

    public bool TryGet(string reference, out IRerankProvider? provider)
    {
        lock (_gate)
            return _providers.TryGetValue(reference, out provider);
    }

    public void Clear()
    {
        lock (_gate)
            _providers.Clear();
    }
}

/// <summary>Embedding cache port. The key is (model, dimensions, SHA-256(text)).</summary>
public interface IEmbeddingCache
{
    ValueTask<ReadOnlyMemory<float>?> TryGetAsync(
        string model,
        int dimensions,
        string text,
        CancellationToken cancellationToken = default);

    ValueTask SetAsync(
        string model,
        int dimensions,
        string text,
        ReadOnlyMemory<float> vector,
        CancellationToken cancellationToken = default);
}

/// <summary>Process-local cache; the RAG plugin can supply its own SQLite-backed implementation.</summary>
public sealed class InMemoryEmbeddingCache : IEmbeddingCache
{
    private readonly ConcurrentDictionary<string, float[]> _entries = new(StringComparer.Ordinal);

    public int Count => _entries.Count;

    public ValueTask<ReadOnlyMemory<float>?> TryGetAsync(
        string model,
        int dimensions,
        string text,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            _entries.TryGetValue(EmbeddingCacheKey.Create(model, dimensions, text), out var vector)
                ? (ReadOnlyMemory<float>?)new ReadOnlyMemory<float>(vector)
                : null);
    }

    public ValueTask SetAsync(
        string model,
        int dimensions,
        string text,
        ReadOnlyMemory<float> vector,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _entries[EmbeddingCacheKey.Create(model, dimensions, text)] = vector.ToArray();
        return ValueTask.CompletedTask;
    }

    public void Clear() => _entries.Clear();
}

/// <summary>Creates the canonical (model, dimensions, sha256(text)) cache key.</summary>
public static class EmbeddingCacheKey
{
    public static string Create(string model, int dimensions, string text)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(text);

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        return $"{model}\u001F{dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture)}\u001F{hash}";
    }
}

/// <summary>One input after planning; Index is the original request index.</summary>
public sealed record EmbeddingExecutionResult(
    EmbeddingResult Result,
    IReadOnlyList<int> TruncatedInputIndexes,
    IReadOnlyList<int> ProviderBatchSizes);

/// <summary>
/// Applies batching/truncation/cache around a provider. Any batch failure aborts the whole call;
/// cache writes are deferred until every batch succeeds, so callers never observe a half result.
/// </summary>
public sealed class EmbeddingService
{
    private readonly IEmbeddingProvider _provider;
    private readonly IEmbeddingCache? _cache;

    public EmbeddingService(IEmbeddingProvider provider, IEmbeddingCache? cache = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _cache = cache;
    }

    public async ValueTask<EmbeddingExecutionResult> EmbedAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Inputs);

        var plan = EmbeddingPlanner.CreatePlan(request.Inputs, _provider.MaxBatchSize, _provider.MaxInputTokens);
        var vectors = new ReadOnlyMemory<float>?[request.Inputs.Count];
        var cacheModel = request.Model ?? _provider.Id;
        var cacheDimensions = request.Dimensions ?? 0;
        var pending = new List<EmbeddingPlannedInput>();

        foreach (var batch in plan.Batches)
        {
            foreach (var input in batch)
            {
                if (_cache is not null)
                {
                    var cached = await _cache.TryGetAsync(
                        cacheModel,
                        cacheDimensions,
                        input.Text,
                        cancellationToken).ConfigureAwait(false);
                    if (cached is { } vector)
                    {
                        vectors[input.Index] = vector;
                        continue;
                    }
                }

                pending.Add(input);
            }
        }

        var batchSizes = new List<int>();
        string? providerModel = null;
        var dimensions = request.Dimensions ?? 0;
        var promptTokens = 0;
        var pendingResults = new List<(EmbeddingPlannedInput Input, ReadOnlyMemory<float> Vector)>();

        for (var offset = 0; offset < pending.Count; offset += _provider.MaxBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = pending.GetRange(offset, Math.Min(_provider.MaxBatchSize, pending.Count - offset));
            var result = await _provider.EmbedAsync(
                new EmbeddingRequest(
                    chunk.Select(item => item.Text).ToArray(),
                    request.Model,
                    request.Dimensions),
                cancellationToken).ConfigureAwait(false);

            if (result.Vectors.Count != chunk.Count)
            {
                throw new EmbeddingProviderException(
                    EmbeddingErrorKind.InvalidResponse,
                    "embedding.invalid_response",
                    $"Provider returned {result.Vectors.Count} vectors for {chunk.Count} inputs.");
            }

            foreach (var vector in result.Vectors)
            {
                if (dimensions == 0)
                    dimensions = vector.Length;
                else if (vector.Length != dimensions)
                {
                    throw new EmbeddingProviderException(
                        EmbeddingErrorKind.InvalidResponse,
                        "embedding.dimension_mismatch",
                        $"Provider returned inconsistent vector dimensions ({vector.Length} vs {dimensions}).");
                }
            }

            providerModel ??= result.Model;
            promptTokens += result.PromptTokens ?? 0;
            batchSizes.Add(chunk.Count);

            for (var index = 0; index < chunk.Count; index++)
            {
                vectors[chunk[index].Index] = result.Vectors[index];
                pendingResults.Add((chunk[index], result.Vectors[index]));
            }
        }

        if (_cache is not null)
        {
            foreach (var (input, vector) in pendingResults)
            {
                await _cache.SetAsync(
                    cacheModel,
                    cacheDimensions,
                    input.Text,
                    vector,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        var ordered = new ReadOnlyMemory<float>[request.Inputs.Count];
        for (var index = 0; index < vectors.Length; index++)
        {
            if (vectors[index] is not { } vector)
            {
                throw new EmbeddingProviderException(
                    EmbeddingErrorKind.InvalidResponse,
                    "embedding.missing_vector",
                    $"No vector was produced for input index {index}.");
            }

            if (dimensions == 0)
                dimensions = vector.Length;
            ordered[index] = vector;
        }

        return new EmbeddingExecutionResult(
            new EmbeddingResult(
                ordered,
                request.Model ?? providerModel ?? _provider.Id,
                dimensions,
                promptTokens == 0 ? null : promptTokens),
            plan.TruncatedIndexes,
            batchSizes);
    }
}

/// <summary>Rerank host guard: enforces MaxCandidates and validates provider output IDs.</summary>
public sealed class RerankService
{
    public async ValueTask<RerankResult> RerankAsync(
        IRerankProvider provider,
        string query,
        IReadOnlyList<RerankCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count > provider.MaxCandidates)
        {
            throw new RerankProviderException(
                "rerank.too_many_candidates",
                $"Rerank provider '{provider.Id}' accepts at most {provider.MaxCandidates} candidates, got {candidates.Count}.");
        }

        var result = await provider.RerankAsync(query, candidates, cancellationToken).ConfigureAwait(false);
        var known = candidates.Select(candidate => candidate.Id).ToHashSet(StringComparer.Ordinal);
        if (result.Ranked.Count > candidates.Count
            || result.Ranked.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != result.Ranked.Count
            || result.Ranked.Any(item => !known.Contains(item.Id)))
        {
            throw new RerankProviderException(
                "rerank.invalid_response",
                $"Rerank provider '{provider.Id}' returned an invalid candidate set.");
        }

        return result;
    }
}
