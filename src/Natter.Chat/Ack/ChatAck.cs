using System.Text.Json;

namespace Natter.Chat;

/// <summary>Well-known NLMP v0.1 error codes.</summary>
public static class ChatErrorCodes
{
    public const string InvalidRequest = "invalid_request";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string Unsupported = "unsupported";
    public const string RateLimited = "rate_limited";
    public const string Unavailable = "unavailable";
    public const string Timeout = "timeout";
    public const string Conflict = "conflict";
    public const string Internal = "internal";

    public static bool IsRetryable(string? code)
        => code is RateLimited or Unavailable or Timeout;
}

/// <summary>Structured command failure. <see cref="RetryAfterMs"/> only applies to rate limits.</summary>
public sealed record ChatError(
    string Code,
    string Message,
    bool Retryable = false,
    int? RetryAfterMs = null,
    IReadOnlyDictionary<string, JsonElement>? Details = null)
{
    public static ChatError Create(
        string code,
        string message,
        int? retryAfterMs = null,
        IReadOnlyDictionary<string, JsonElement>? details = null)
        => new(code, message, ChatErrorCodes.IsRetryable(code), retryAfterMs, details);
}

/// <summary>Result of a command. Empty for update/delete/typing/interaction unless the adapter reports more.</summary>
public sealed record ChatAckResult(IReadOnlyList<string>? MessageIds = null, DateTimeOffset? DeliveredAt = null);

/// <summary>Adapter acknowledgement for one command.</summary>
public sealed record ChatAck
{
    public string Version { get; init; } = Nlmp.Version;

    public required string Id { get; init; }

    public required string ReplyTo { get; init; }

    public required bool Ok { get; init; }

    public ChatAckResult? Result { get; init; }

    public ChatError? Error { get; init; }

    public static ChatAck Success(
        ChatCommand command,
        IReadOnlyList<string>? messageIds = null,
        DateTimeOffset? deliveredAt = null)
        => new()
        {
            Id = $"ack:{command.Id}",
            ReplyTo = command.Id,
            Ok = true,
            Result = new ChatAckResult(messageIds, deliveredAt),
        };

    public static ChatAck Failure(ChatCommand command, ChatError error)
        => new()
        {
            Id = $"ack:{command.Id}",
            ReplyTo = command.Id,
            Ok = false,
            Error = error,
        };
}
