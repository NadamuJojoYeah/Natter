namespace Natter.Feature.Ai;

/// <summary>Provider-neutral error category used by the shared responder.</summary>
public enum AiErrorKind
{
    Authentication,
    InsufficientBalance,
    RateLimited,
    Timeout,
    Unavailable,
    InvalidRequest,
    ContentFilter,
    Unknown,
}

/// <summary>Implemented by provider adapters so the shared responder can classify errors.</summary>
public interface IAiProviderException
{
    AiErrorKind Kind { get; }

    int? StatusCode { get; }

    string? ErrorCode { get; }
}
