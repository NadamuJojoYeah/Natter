using System.Net;
using Discord.Net;

namespace Natter.Chat.Discord;

/// <summary>Retry budget for Discord send calls (S2-A16).</summary>
public sealed record DiscordSendRetryOptions(
    int MaxAttempts = 3,
    int BaseDelayMs = 250,
    int MaxDelayMs = 5_000);

/// <summary>
/// Rate-limit classification and backoff for outbound Discord calls (S2-A16). Previously a 429
/// surfaced as a failure immediately and <c>retryAfterMs</c> was only forwarded to the caller, so
/// messages were dropped during traffic peaks. Retries are bounded and always honour the delay
/// the platform asked for.
/// </summary>
public static class DiscordSendRetryPolicy
{
    /// <summary>True for Discord rate-limit rejections (REST 429 or the typed exception).</summary>
    public static bool IsRateLimited(Exception? exception)
        => exception switch
        {
            RateLimitedException => true,
            HttpException http => http.HttpCode == HttpStatusCode.TooManyRequests,
            _ => false,
        };

    /// <summary>
    /// The platform-provided retry delay, when it sent one. Discord.Net surfaces it on
    /// <c>RateLimitedException.Request.TimeoutAt</c> (there is no public RetryAfter property on
    /// the exception itself).
    /// </summary>
    public static int? RetryAfterMs(Exception? exception)
    {
        if (exception is not RateLimitedException limited)
            return null;

        if (limited.Request?.TimeoutAt is not { } timeoutAt)
            return null;

        return (int)Math.Max(0, (timeoutAt - DateTimeOffset.UtcNow).TotalMilliseconds);
    }

    /// <summary>
    /// Delay before the next attempt: the platform's <c>retryAfterMs</c> wins (clamped to the
    /// configured ceiling), otherwise exponential backoff from <see cref="DiscordSendRetryOptions.BaseDelayMs"/>.
    /// </summary>
    public static TimeSpan DelayFor(Exception? exception, int attempt, DiscordSendRetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var ceiling = Math.Max(1, options.MaxDelayMs);
        if (RetryAfterMs(exception) is { } retryAfter)
            return TimeSpan.FromMilliseconds(Math.Clamp(retryAfter, 0, ceiling));

        var baseDelay = Math.Max(1, options.BaseDelayMs);
        var shift = Math.Clamp(attempt - 1, 0, 12);
        var scaled = (long)baseDelay << shift;
        return TimeSpan.FromMilliseconds(Math.Clamp(scaled, baseDelay, ceiling));
    }

    /// <summary>True when another attempt is allowed for this exception and attempt number.</summary>
    public static bool ShouldRetry(Exception? exception, int attempt, DiscordSendRetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return IsRateLimited(exception) && attempt < Math.Max(1, options.MaxAttempts);
    }

    /// <summary>Stable failure result once the retry budget is exhausted (never silent).</summary>
    public static DiscordSendResult Exhausted(Exception? exception, int attempts, DiscordSendRetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var budget = Math.Max(1, options.MaxAttempts);
        return DiscordSendResult.Failure(
            "rate_limited",
            $"Discord rate limit persisted after {Math.Max(attempts, budget)} attempt(s).",
            RetryAfterMs(exception));
    }
}
