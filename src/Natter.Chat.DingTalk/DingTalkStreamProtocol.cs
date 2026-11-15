using System.Text.Json;

namespace Natter.Chat.DingTalk;

/// <summary>Pure protocol helpers for DingTalk Stream mode; testable without WebSocket.</summary>
public static class DingTalkStreamProtocol
{
    public const string BotTopic = "/v1.0/im/bot/messages/get";

    public static string BuildOpenConnectionRequest(
        string clientId,
        string clientSecret,
        string topic,
        string userAgent = "natter/1.0")
        => JsonSerializer.Serialize(new
        {
            clientId,
            clientSecret,
            subscriptions = new[] { new { type = "CALLBACK", topic } },
            ua = userAgent,
            localIp = string.Empty,
        });

    public static DingTalkStreamConnection ParseOpenConnection(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var endpoint = root.TryGetProperty("endpoint", out var e) ? e.GetString() : null;
        var ticket = root.TryGetProperty("ticket", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(ticket))
            throw new InvalidOperationException("DingTalk Stream open-connection response is missing endpoint/ticket.");

        return new DingTalkStreamConnection(endpoint, ticket);
    }

    public static DingTalkStreamFrame ParseFrame(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        var headers = root.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object
            ? h
            : default;
        var messageId = headers.ValueKind == JsonValueKind.Object && headers.TryGetProperty("messageId", out var id)
            ? id.GetString()
            : null;
        var topic = headers.ValueKind == JsonValueKind.Object && headers.TryGetProperty("topic", out var topicElement)
            ? topicElement.GetString()
            : null;
        var data = root.TryGetProperty("data", out var d)
            ? d.ValueKind == JsonValueKind.String ? d.GetString() : d.GetRawText()
            : null;

        return new DingTalkStreamFrame(type, messageId, topic, data);
    }

    public static string BuildAck(string? messageId, int code = 200, string message = "OK")
        => JsonSerializer.Serialize(new
        {
            code,
            headers = new
            {
                messageId,
                contentType = "application/json",
            },
            message,
            data = "{}",
        });

    public static string BuildWebSocketUri(string endpoint, string ticket)
        => endpoint + (endpoint.Contains('?', StringComparison.Ordinal) ? "&" : "?")
           + "ticket=" + Uri.EscapeDataString(ticket);
}

public sealed record DingTalkStreamConnection(string Endpoint, string Ticket);

public sealed record DingTalkStreamFrame(string Type, string? MessageId, string? Topic, string? Data)
{
    public bool IsDisconnect => string.Equals(Type, "SYSTEM", StringComparison.OrdinalIgnoreCase)
                                && string.Equals(Topic, "disconnect", StringComparison.OrdinalIgnoreCase);
}
