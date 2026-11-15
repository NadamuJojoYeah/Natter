namespace Natter.Feature.Ai.DeepSeek;

public enum DeepSeekErrorKind
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

/// <summary>Structured DeepSeek failure. Never includes the API key or full prompt/response.</summary>
public sealed class DeepSeekException : Exception
{
    public DeepSeekException(
        DeepSeekErrorKind kind,
        string message,
        int? statusCode = null,
        string? errorCode = null,
        int? retryAfterMs = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        StatusCode = statusCode;
        ErrorCode = errorCode;
        RetryAfterMs = retryAfterMs;
    }

    public DeepSeekErrorKind Kind { get; }

    public int? StatusCode { get; }

    public string? ErrorCode { get; }

    public int? RetryAfterMs { get; }

    public bool Retryable => Kind is DeepSeekErrorKind.RateLimited or DeepSeekErrorKind.Timeout or DeepSeekErrorKind.Unavailable;

    public static DeepSeekException Timeout(string message, Exception? inner = null)
        => new(DeepSeekErrorKind.Timeout, message, innerException: inner);

    public static DeepSeekException FromHttp(int statusCode, string? body, int? retryAfterMs)
    {
        var (code, message) = ParseError(body);
        var kind = statusCode switch
        {
            401 or 403 => DeepSeekErrorKind.Authentication,
            402 => DeepSeekErrorKind.InsufficientBalance,
            408 => DeepSeekErrorKind.Timeout,
            429 => DeepSeekErrorKind.RateLimited,
            >= 500 => DeepSeekErrorKind.Unavailable,
            400 when message.Contains("content_filter", StringComparison.OrdinalIgnoreCase)
                   || code?.Contains("content_filter", StringComparison.OrdinalIgnoreCase) == true
                => DeepSeekErrorKind.ContentFilter,
            400 => DeepSeekErrorKind.InvalidRequest,
            _ => DeepSeekErrorKind.Unknown,
        };

        var safeMessage = kind switch
        {
            DeepSeekErrorKind.Authentication => "DeepSeek authentication failed.",
            DeepSeekErrorKind.InsufficientBalance => "DeepSeek account balance is insufficient.",
            DeepSeekErrorKind.RateLimited => "DeepSeek rate limit exceeded.",
            DeepSeekErrorKind.Timeout => "DeepSeek request timed out.",
            DeepSeekErrorKind.Unavailable => "DeepSeek is temporarily unavailable.",
            DeepSeekErrorKind.ContentFilter => "DeepSeek rejected the content.",
            DeepSeekErrorKind.InvalidRequest => message.Length > 0 ? message : "DeepSeek rejected the request.",
            _ => "DeepSeek request failed.",
        };

        return new DeepSeekException(kind, safeMessage, statusCode, code, retryAfterMs);
    }

    private static (string? Code, string Message) ParseError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return (null, string.Empty);

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var code = error.TryGetProperty("code", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String
                    ? c.GetString()
                    : null;
                var message = error.TryGetProperty("message", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String
                    ? m.GetString() ?? string.Empty
                    : string.Empty;
                return (code, message);
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return (null, string.Empty);
    }
}
