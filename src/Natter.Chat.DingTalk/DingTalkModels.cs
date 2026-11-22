using System.Text.Json;

namespace Natter.Chat.DingTalk;

/// <summary>One DingTalk robot callback message (text subset of the documented protocol).</summary>
public sealed record DingTalkCallbackMessage(
    string MsgId,
    string ConversationId,
    string ConversationType,
    string? ConversationTitle,
    string SenderId,
    string? SenderStaffId,
    string SenderNick,
    string? ChatbotUserId,
    string? RobotCode,
    string Content,
    IReadOnlyList<string> AtUserIds,
    bool IsInAtList,
    string MsgType,
    string? SessionWebhook,
    DateTimeOffset? SessionWebhookExpiresAt,
    DateTimeOffset CreatedAt)
{
    public bool IsDirect => ConversationType == "1";
    public bool IsGroup => ConversationType == "2";
    public string EffectiveUserId => string.IsNullOrWhiteSpace(SenderStaffId) ? SenderId : SenderStaffId;
}

public sealed class DingTalkCallbackParseResult
{
    public DingTalkCallbackMessage? Message { get; init; }

    public string? Error { get; init; }

    public bool Ok => Message is not null;
}

/// <summary>Parses the documented DingTalk robot callback body.</summary>
public static class DingTalkCallbackParser
{
    public static DingTalkCallbackParseResult Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new DingTalkCallbackParseResult { Error = "Callback body must be a JSON object." };

            var msgType = String(root, "msgtype") ?? string.Empty;
            if (!string.Equals(msgType, "text", StringComparison.OrdinalIgnoreCase))
                return new DingTalkCallbackParseResult { Error = $"Unsupported DingTalk message type '{msgType}'." };

            var text = root.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.Object
                ? String(textElement, "content") ?? string.Empty
                : string.Empty;

            var atUsers = new List<string>();
            if (root.TryGetProperty("atUsers", out var atUsersElement) && atUsersElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in atUsersElement.EnumerateArray())
                {
                    var id = item.ValueKind == JsonValueKind.Object ? String(item, "dingtalkId") : null;
                    if (!string.IsNullOrWhiteSpace(id))
                        atUsers.Add(id);
                }
            }

            var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(Int64(root, "createAt") ?? 0);
            var expires = Int64(root, "sessionWebhookExpiredTime") is { } expiration
                ? DateTimeOffset.FromUnixTimeMilliseconds(expiration)
                : (DateTimeOffset?)null;

            var message = new DingTalkCallbackMessage(
                String(root, "msgId") ?? string.Empty,
                String(root, "conversationId") ?? string.Empty,
                String(root, "conversationType") ?? "2",
                String(root, "conversationTitle"),
                String(root, "senderId") ?? String(root, "senderStaffId") ?? string.Empty,
                String(root, "senderStaffId"),
                String(root, "senderNick") ?? "dingtalk-user",
                String(root, "chatbotUserId"),
                String(root, "robotCode"),
                text,
                atUsers,
                Bool(root, "isInAtList"),
                msgType,
                String(root, "sessionWebhook"),
                expires,
                createdAt);

            if (string.IsNullOrWhiteSpace(message.MsgId) || string.IsNullOrWhiteSpace(message.ConversationId))
                return new DingTalkCallbackParseResult { Error = "Callback is missing msgId or conversationId." };

            return new DingTalkCallbackParseResult { Message = message };
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException)
        {
            return new DingTalkCallbackParseResult { Error = "Malformed DingTalk callback: " + ex.Message };
        }
    }

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? Int64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;
        if (value.TryGetInt64(out var number))
            return number;
        return value.ValueKind == JsonValueKind.String
               && long.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
    }

    private static bool Bool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}

public enum DingTalkTransportEventKind
{
    Message,
    Error,
    Disconnected,

    /// <summary>
    /// S2-A6: the transport (re)connected. Without this event the upstream kept
    /// <c>connected=false</c> after an internal reconnect and /health showed an outage forever.
    /// </summary>
    Connected,
}

public sealed record DingTalkTransportEvent
{
    public required DingTalkTransportEventKind Kind { get; init; }

    public DingTalkCallbackMessage? Message { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }
}

public sealed record DingTalkReply(string ConversationId, string SessionWebhook, string Content);

public sealed record DingTalkSendResult(bool Ok, string? MessageId = null, string? ErrorCode = null, string? ErrorMessage = null)
{
    public static DingTalkSendResult Success(string? messageId = null) => new(true, messageId);

    public static DingTalkSendResult Failure(string code, string message) => new(false, ErrorCode: code, ErrorMessage: message);
}

public interface IDingTalkTransport
{
    IObservable<DingTalkTransportEvent> Events { get; }

    ValueTask ConnectAsync(CancellationToken cancellationToken = default);

    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);

    ValueTask<DingTalkSendResult> SendAsync(DingTalkReply reply, CancellationToken cancellationToken = default);
}

/// <summary>HMAC-SHA256 callback signature helper (timestamp + "\n" + appSecret).</summary>
public static class DingTalkSignature
{
    public static string Sign(string timestamp, string appSecret)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(appSecret));
        var data = System.Text.Encoding.UTF8.GetBytes(timestamp + "\n" + appSecret);
        return Convert.ToBase64String(hmac.ComputeHash(data));
    }

    public static bool Verify(
        string? timestamp,
        string? sign,
        string appSecret,
        DateTimeOffset now,
        TimeSpan tolerance)
    {
        if (string.IsNullOrWhiteSpace(timestamp)
            || string.IsNullOrWhiteSpace(sign)
            || string.IsNullOrWhiteSpace(appSecret)
            || !long.TryParse(timestamp, out var milliseconds))
        {
            return false;
        }

        var sentAt = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        if ((now - sentAt).Duration() > tolerance)
            return false;

        var expected = Sign(timestamp, appSecret);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(expected),
            System.Text.Encoding.UTF8.GetBytes(sign));
    }
}
