namespace Natter.Chat.DingTalk;

public sealed class DingTalkOptions
{
    public string? AppSecret { get; set; }

    public string BotId { get; set; } = string.Empty;

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
