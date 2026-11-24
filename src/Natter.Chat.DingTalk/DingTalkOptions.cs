namespace Natter.Chat.DingTalk;

public sealed class DingTalkOptions
{
    public string? AppSecret { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public string BotId { get; set; } = string.Empty;

    public string? RobotCode { get; set; }

    public string StreamGatewayUrl { get; set; } = "https://api.dingtalk.com/v1.0/gateway/connections/open";

    public string StreamTopic { get; set; } = "/v1.0/im/bot/messages/get";

    public int ReconnectBaseDelayMs { get; set; } = 500;

    public int ReconnectMaxDelayMs { get; set; } = 30000;

    /// <summary>
    /// S2-A4: no inbound frame for this long means the Stream socket is half-open; the transport
    /// closes it and reconnects instead of reporting <c>IsConnected=true</c> forever.
    /// </summary>
    public int StreamIdleTimeoutMs { get; set; } = 90_000;

    /// <summary>S2-A4: an ack that goes unanswered for this long is treated as a half-open peer.</summary>
    public int StreamPongTimeoutMs { get; set; } = 30_000;

    /// <summary>S2-A3: bounded dedup of callback frames the platform re-delivers.</summary>
    public int StreamCallbackDedupCapacity { get; set; } = 2048;

    /// <summary>S2-A3: how long a callback id is remembered for redelivery suppression.</summary>
    public int StreamCallbackDedupTtlMs { get; set; } = 600_000;

    /// <summary>Legacy HTTP callback listener; Stream mode does not use this.</summary>
    public string ListenPrefix { get; set; } = "http://127.0.0.1:5088/dingtalk/";

    public int MaxClockSkewMinutes { get; set; } = 60;

    public bool RequireMention { get; set; } = true;

    public List<string> Conversations { get; set; } = [];

    public List<string> Users { get; set; } = [];

    public int MaxMessageLength { get; set; } = 2000;

    public bool IsConversationAllowed(string conversationId)
        => Conversations.Count == 0 || Conversations.Contains(conversationId, StringComparer.Ordinal);

    public bool IsUserAllowed(string userId)
        => Users.Count == 0 || Users.Contains(userId, StringComparer.Ordinal);
}
