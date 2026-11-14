using System.Text.Json;

namespace Natter.Chat.Safety;

/// <summary>
/// Outbound mention policy. High-risk mentions are denied unless a host explicitly opts in.
/// </summary>
public sealed record ChatOutboundPolicy
{
    public bool AllowEveryoneMention { get; init; }

    public bool AllowHereMention { get; init; }

    public static ChatOutboundPolicy Default { get; } = new();
}

/// <summary>Validates outbound mentions before a command reaches an upstream.</summary>
public static class ChatMentionGuard
{
    public static ChatError? Validate(Content? content, ChatOutboundPolicy? policy = null)
    {
        if (content?.Mentions is not { Count: > 0 } mentions)
            return null;

        policy ??= ChatOutboundPolicy.Default;

        var blocked = mentions
            .Where(mention => mention is not null)
            .Where(mention => mention.Kind switch
            {
                MentionKind.Everyone => !policy.AllowEveryoneMention,
                MentionKind.Here => !policy.AllowHereMention,
                _ => false,
            })
            .Select(mention => mention.Kind.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (blocked.Length == 0)
            return null;

        var details = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["blockedMentionKinds"] = JsonSerializer.SerializeToElement(blocked, NlmpJson.Options),
        };

        return ChatError.Create(
            ChatErrorCodes.InvalidRequest,
            "Outbound everyone/here mentions are disabled by the default policy.",
            details: details);
    }
}
