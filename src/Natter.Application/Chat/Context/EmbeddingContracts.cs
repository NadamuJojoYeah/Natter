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
public sealed record EmbeddingPlannedInput(int Index, string Text, bool Truncated);

/// <summary>Batch plan preserving original order and recording truncated input indexes.</summary>
public sealed record EmbeddingPlan(
    IReadOnlyList<IReadOnlyList<EmbeddingPlannedInput>> Batches,
    IReadOnlyList<int> TruncatedIndexes);

/// <summary>
/// Host-side batching/truncation. Token estimation is deliberately conservative: CJK characters
/// count as 1.5 chars/token and all other characters as 4 chars/token, with a 10% budget reserve.
/// </summary>
public static class EmbeddingPlanner
{
    public static EmbeddingPlan CreatePlan(
        IReadOnlyList<string> inputs,
        int maxBatchSize,
        int maxInputTokens)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (maxBatchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBatchSize), "MaxBatchSize must be positive.");

        var tokenBudget = Math.Max(1, maxInputTokens);
        var truncated = new List<int>();
        var planned = new List<EmbeddingPlannedInput>(inputs.Count);

        for (var index = 0; index < inputs.Count; index++)
        {
            var original = inputs[index] ?? string.Empty;
            var text = Truncate(original, tokenBudget, out var wasTruncated);
            if (wasTruncated)
                truncated.Add(index);
            planned.Add(new EmbeddingPlannedInput(index, text, wasTruncated));
        }

        var batches = new List<IReadOnlyList<EmbeddingPlannedInput>>();
        for (var offset = 0; offset < planned.Count; offset += maxBatchSize)
            batches.Add(planned.GetRange(offset, Math.Min(maxBatchSize, planned.Count - offset)));

        return new EmbeddingPlan(batches, truncated);
    }

    /// <summary>Truncates to the conservative token budget without splitting surrogate pairs.</summary>
    public static string Truncate(string text, int maxInputTokens, out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(text);
        truncated = false;
        if (text.Length == 0 || maxInputTokens <= 0)
            return text;

        // Keep a little headroom for provider-side tokenizer differences.
        var budget = Math.Max(1d, maxInputTokens * 0.9);
        var builder = new StringBuilder();
        var used = 0d;

        foreach (var rune in text.EnumerateRunes())
        {
            var cost = IsCjk(rune.Value) ? 1d / 1.5d : 1d / 4d;
            if (used + cost > budget)
            {
                truncated = true;
                break;
            }

            builder.Append(rune.ToString());
            used += cost;
        }

        return builder.ToString();
    }

    public static int EstimateTokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var cjk = 0;
        var other = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsCjk(rune.Value))
                cjk++;
            else
                other++;
        }

        return (int)Math.Ceiling(cjk / 1.5d + other / 4d);
    }

    private static bool IsCjk(int codePoint)
        => codePoint is >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF
            or >= 0x3040 and <= 0x30FF
            or >= 0xAC00 and <= 0xD7AF
            or >= 0xF900 and <= 0xFAFF
            or >= 0x20000 and <= 0x2FA1F;
}

/// <summary>Result of a host-planned embedding call, including truncation and provider-call evidence.</summary>
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
