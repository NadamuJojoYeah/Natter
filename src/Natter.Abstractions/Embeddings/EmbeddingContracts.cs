using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Natter.Application.Chat;

/// <summary>One embedding request. Callers should supply the effective model/dimensions they use as cache keys.</summary>
public sealed record EmbeddingRequest(
    IReadOnlyList<string> Inputs,
    string? Model = null,
    int? Dimensions = null);

/// <summary>Provider embedding output. Vector order corresponds to request input order.</summary>
public sealed record EmbeddingResult(
    IReadOnlyList<ReadOnlyMemory<float>> Vectors,
    string Model,
    int Dimensions,
    int? PromptTokens);

/// <summary>Text embedding port (P6.2). Implementations may be backed by a remote provider.</summary>
public interface IEmbeddingProvider
{
    string Id { get; }

    /// <summary>Maximum inputs per provider call. Callers MUST batch below this limit.</summary>
    int MaxBatchSize { get; }

    /// <summary>Maximum estimated tokens per input. Callers MUST truncate before calling.</summary>
    int MaxInputTokens { get; }

    ValueTask<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken ct = default);
}

/// <summary>One rerank candidate supplied by the caller.</summary>
public sealed record RerankCandidate(string Id, string Text);

/// <summary>Provider rerank output. Ranked order is the provider's relevance order.</summary>
public sealed record RerankResult(IReadOnlyList<(string Id, double Score)> Ranked);

/// <summary>Rerank port (P6.2).</summary>
public interface IRerankProvider
{
    string Id { get; }

    /// <summary>Maximum candidates per provider call. Callers MUST not exceed this limit.</summary>
    int MaxCandidates { get; }

    ValueTask<RerankResult> RerankAsync(
        string query,
        IReadOnlyList<RerankCandidate> candidates,
        CancellationToken ct = default);
}

/// <summary>Stable embedding failure categories; providers expose a machine-readable Code too.</summary>
public enum EmbeddingErrorKind
{
    Authentication,
    RateLimited,
    Timeout,
    InvalidRequest,
    Unavailable,
    InvalidResponse,
    Unknown,
}

/// <summary>
/// Provider failure with a stable code. Host-side batching rethrows it unchanged, and never retries
/// on its own (provider/SDK retry policy belongs to the implementation).
/// </summary>
public sealed class EmbeddingProviderException : Exception
{
    public EmbeddingProviderException(
        EmbeddingErrorKind kind,
        string code,
        string message,
        int? statusCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        Code = code;
        StatusCode = statusCode;
    }

    public EmbeddingErrorKind Kind { get; }

    public string Code { get; }

    public int? StatusCode { get; }
}

/// <summary>Rerank failure with a stable code (limits/validation/provider errors).</summary>
public sealed class RerankProviderException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}

/// <summary>Host-held embedding providers keyed by <c>owner#id</c>.</summary>
public interface IEmbeddingProviderCatalog
{
    void Register(string reference, IEmbeddingProvider provider);

    void Unregister(string reference);

    bool TryGet(string reference, out IEmbeddingProvider? provider);

    IReadOnlyList<(string Reference, IEmbeddingProvider Provider)> Providers { get; }

    void Clear();
}

/// <summary>Host-held rerank providers keyed by <c>owner#id</c>.</summary>
public interface IRerankProviderCatalog
{
    void Register(string reference, IRerankProvider provider);

    void Unregister(string reference);

    bool TryGet(string reference, out IRerankProvider? provider);

    IReadOnlyList<(string Reference, IRerankProvider Provider)> Providers { get; }

    void Clear();
}

