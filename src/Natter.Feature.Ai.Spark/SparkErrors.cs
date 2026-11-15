using Natter.Feature.Ai;

namespace Natter.Feature.Ai.Spark;

public sealed class SparkException : Exception, IAiProviderException
{
    public SparkException(
        AiErrorKind kind,
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

    public AiErrorKind Kind { get; }

    public int? StatusCode { get; }

    public string? ErrorCode { get; }

    public int? RetryAfterMs { get; }

    public bool Retryable => Kind is AiErrorKind.RateLimited or AiErrorKind.Timeout or AiErrorKind.Unavailable;

    public static SparkException Timeout(string message, Exception? inner = null)
        => new(AiErrorKind.Timeout, message, innerException: inner);

    public static SparkException FromHttp(int statusCode, string? body, int? retryAfterMs)
    {
        var (code, message, providerCode) = ParseError(body);
        var kind = statusCode switch
        {
            401 or 403 => AiErrorKind.Authentication,
            429 => AiErrorKind.RateLimited,
            408 => AiErrorKind.Timeout,
            >= 500 => AiErrorKind.Unavailable,
            400 => MapProviderCode(providerCode, message, AiErrorKind.InvalidRequest),
            _ => AiErrorKind.Unknown,
        };

        if (statusCode is 200 or 201 && providerCode is not null and not 0)
            kind = MapProviderCode(providerCode, message, kind);

        var safe = kind switch
        {
            AiErrorKind.Authentication => "Spark authentication failed.",
            AiErrorKind.InsufficientBalance => "Spark quota is insufficient.",
            AiErrorKind.RateLimited => "Spark rate limit exceeded.",
            AiErrorKind.Timeout => "Spark request timed out.",
            AiErrorKind.Unavailable => "Spark is temporarily unavailable.",
            AiErrorKind.ContentFilter => "Spark rejected the content.",
            AiErrorKind.InvalidRequest => message.Length > 0 ? message : "Spark rejected the request.",
            _ => "Spark request failed.",
        };

        return new SparkException(kind, safe, statusCode, code, retryAfterMs);
    }

    private static (string? Code, string Message, int? ProviderCode) ParseError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return (null, string.Empty, null);

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var code = error.TryGetProperty("code", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String
                    ? c.GetString()
                    : null;
                var message = error.TryGetProperty("message", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String
                    ? m.GetString() ?? string.Empty
                    : string.Empty;
                return (code, message, null);
            }

            var providerCode = root.TryGetProperty("code", out var topCode) && topCode.TryGetInt32(out var number)
                ? number
                : (int?)null;
            var topMessage = root.TryGetProperty("message", out var topMessageElement)
                             && topMessageElement.ValueKind == System.Text.Json.JsonValueKind.String
                ? topMessageElement.GetString() ?? string.Empty
                : string.Empty;
            return (providerCode?.ToString(System.Globalization.CultureInfo.InvariantCulture), topMessage, providerCode);
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, string.Empty, null);
        }
    }

    private static AiErrorKind MapProviderCode(int? code, string message, AiErrorKind fallback)
    {
        if (message.Contains("content", StringComparison.OrdinalIgnoreCase)
            && (message.Contains("audit", StringComparison.OrdinalIgnoreCase)
                || message.Contains("sensitive", StringComparison.OrdinalIgnoreCase)))
        {
            return AiErrorKind.ContentFilter;
        }

        return code switch
        {
            10007 => AiErrorKind.RateLimited,
            10013 or 10014 or 10019 => AiErrorKind.ContentFilter,
            10907 or 400 => AiErrorKind.InvalidRequest,
            11200 => AiErrorKind.Authentication,
            11201 or 11202 or 11203 => AiErrorKind.RateLimited,
            _ => fallback,
        };
    }
}
