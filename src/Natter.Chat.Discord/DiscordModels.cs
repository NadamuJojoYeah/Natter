using System.Text.Json;

namespace Natter.Chat.Discord;

/// <summary>Strongly typed bot token wrapper. It never prints the secret.</summary>
public sealed class DiscordBotToken
{
    private DiscordBotToken(string value) => Value = value;

    internal string Value { get; }

    public static DiscordBotToken From(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Discord bot token must not be empty.", nameof(token));

        return new DiscordBotToken(token.Trim());
    }

    public override string ToString() => "***";
}

/// <summary>Discord transport configuration; secrets stay outside this object.</summary>
public sealed class DiscordOptions
{
    public string? Token { get; set; }

    /// <summary>Bot user id. Required because the upstream Source is fixed before login.</summary>
    public string SelfId { get; set; } = string.Empty;

    public List<string> Intents { get; set; } = ["Guilds", "GuildMessages", "DirectMessages", "MessageContent"];

    public List<string> Guilds { get; set; } = [];

    public List<string> Channels { get; set; } = [];

    public List<string> Users { get; set; } = [];

    public bool RequireMention { get; set; } = true;

    public bool MessageCommands { get; set; } = true;

    public bool SlashCommands { get; set; }

    public bool IgnoreBots { get; set; } = true;

    public bool IgnoreSelf { get; set; } = true;

    public bool Typing { get; set; } = true;

    public int MaxMessageLength { get; set; } = 1900;

    public long MaxAttachmentBytes { get; set; } = 25L * 1024 * 1024;

    /// <summary>Application command names that may be registered/handled.</summary>
    public Dictionary<string, bool> Commands { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ask"] = true,
        ["remember"] = true,
        ["forget"] = true,
        ["memory"] = true,
        ["reset"] = true,
        ["task"] = true,
        ["status"] = true,
        ["tasks"] = true,
        ["cancel"] = true,
    };

    public int InteractionTtlSeconds { get; set; } = 900;

    /// <summary>S2-A11: hard cap for the interaction token cache (bounded, TTL pruned).</summary>
    public int InteractionCacheCapacity { get; set; } = 500;

    /// <summary>S2-A16: send attempts for a rate-limited call (1 disables retries).</summary>
    public int SendRetryMaxAttempts { get; set; } = 3;

    /// <summary>S2-A16: exponential backoff base for rate-limited sends.</summary>
    public int SendRetryBaseDelayMs { get; set; } = 250;

    /// <summary>S2-A16: ceiling for both exponential backoff and platform retryAfter delays.</summary>
    public int SendRetryMaxDelayMs { get; set; } = 5_000;

    public int ReadyTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// P0 (2026-11-22 incident): rebuild the gateway transport when the upstream stays
    /// disconnected past <see cref="ReconnectWatchdogThresholdMs"/>. Discord.Net's internal
    /// reconnect loop can terminate permanently, and nothing else in the host would recover.
    /// </summary>
    public bool ReconnectWatchdog { get; set; } = true;

    /// <summary>How long <c>connected=false</c> is tolerated before the watchdog rebuilds the transport.</summary>
    public int ReconnectWatchdogThresholdMs { get; set; } = 60_000;

    /// <summary>Watchdog poll interval in milliseconds; 0 derives one from the threshold.</summary>
    public int ReconnectWatchdogTickMs { get; set; }

    /// <summary>First backoff delay between rebuild attempts.</summary>
    public int ReconnectBackoffInitialMs { get; set; } = 1_000;

    /// <summary>Upper bound for the exponential rebuild backoff.</summary>
    public int ReconnectBackoffMaxMs { get; set; } = 60_000;

    /// <summary>
    /// Consecutive failed rebuilds tolerated before the upstream flags "manual intervention
    /// required" and stops retrying instead of hammering the platform forever.
    /// </summary>
    public int ReconnectMaxAttempts { get; set; } = 10;

    public string? DevGuildId { get; set; }

    public bool IsGuildAllowed(string? guildId)
        => Guilds.Count == 0 || (guildId is not null && Guilds.Contains(guildId, StringComparer.Ordinal));

    public bool IsChannelAllowed(string? channelId)
        => Channels.Count == 0 || (channelId is not null && Channels.Contains(channelId, StringComparer.Ordinal));

    public bool IsUserAllowed(string? userId)
        => Users.Count == 0 || (userId is not null && Users.Contains(userId, StringComparer.Ordinal));
}

/// <summary>Discord.Net-facing transport abstraction; CI uses a fake implementation.</summary>
public interface IDiscordTransport
{
    IObservable<DiscordTransportEvent> Events { get; }

    ValueTask ConnectAsync(CancellationToken cancellationToken = default);

    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);

    ValueTask<DiscordSendResult> SendMessageAsync(DiscordSendMessage request, CancellationToken cancellationToken = default);

    ValueTask<DiscordSendResult> UpdateMessageAsync(DiscordUpdateMessage request, CancellationToken cancellationToken = default);

    ValueTask<DiscordSendResult> DeleteMessageAsync(DiscordDeleteMessage request, CancellationToken cancellationToken = default);

    ValueTask TriggerTypingAsync(DiscordConversationRef conversation, CancellationToken cancellationToken = default);

    ValueTask<DiscordSendResult> RespondInteractionAsync(DiscordInteractionResponse response, CancellationToken cancellationToken = default);
}

public enum DiscordTransportEventKind
{
    Ready,
    MessageCreated,
    MessageUpdated,
    MessageDeleted,
    InteractionCreated,
    Error,
    Disconnected,
}

public sealed record DiscordTransportEvent
{
    public required DiscordTransportEventKind Kind { get; init; }

    public string? SelfId { get; init; }

    public string? SelfName { get; init; }

    public DiscordMessage? Message { get; init; }

    public string? MessageId { get; init; }

    public DiscordChannel? Channel { get; init; }

    public DiscordGuild? Guild { get; init; }

    public DiscordInteraction? Interaction { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public int? GuildCount { get; init; }

    public int? ChannelCount { get; init; }

    public int? LatencyMs { get; init; }

    /// <summary>False for recoverable diagnostics such as command-registration failures.</summary>
    public bool Fatal { get; init; } = true;
}

public sealed record DiscordUser(string Id, string Username, string? GlobalName = null, bool IsBot = false)
{
    public string DisplayName => string.IsNullOrWhiteSpace(GlobalName) ? Username : GlobalName;
}

public sealed record DiscordChannel(
    string Id,
    string? GuildId = null,
    string? Name = null,
    int? Type = null,
    string? ParentId = null,
    bool IsDirect = false,
    bool IsGroup = false,
    bool IsThread = false);

public sealed record DiscordGuild(string Id, string? Name = null);

public sealed record DiscordAttachment(
    string Id,
    string Url,
    string FileName,
    string? ContentType = null,
    long? Size = null,
    int? Width = null,
    int? Height = null);

public sealed record DiscordMessage(
    string Id,
    DiscordChannel Channel,
    DiscordGuild? Guild,
    DiscordUser Author,
    string Content,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EditedAt = null,
    string? ReplyToMessageId = null,
    IReadOnlyList<DiscordAttachment>? Attachments = null,
    IReadOnlyList<DiscordUser>? MentionedUsers = null,
    bool MentionEveryone = false);

public sealed record DiscordInteraction(
    string Id,
    string Kind,
    DiscordUser User,
    DiscordChannel Channel,
    DiscordGuild? Guild,
    string? Command,
    IReadOnlyDictionary<string, JsonElement>? Options,
    IReadOnlyList<string>? Values,
    string? ReplyToken,
    DateTimeOffset CreatedAt);

public sealed record DiscordSendMessage(
    string ChannelId,
    string Content,
    string? ReplyToMessageId = null,
    IReadOnlyList<string>? MentionUserIds = null);

public sealed record DiscordUpdateMessage(
    string ChannelId,
    string MessageId,
    string Content,
    IReadOnlyList<string>? MentionUserIds = null);

public sealed record DiscordDeleteMessage(string ChannelId, string MessageId);

public sealed record DiscordConversationRef(string ChannelId, string? GuildId = null);

public sealed record DiscordInteractionResponse(
    string InteractionId,
    string Content,
    bool Ephemeral = false,
    bool UpdateOriginal = false);

public sealed record DiscordSendResult(
    bool Ok,
    string? MessageId = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    int? RetryAfterMs = null)
{
    public static DiscordSendResult Success(string messageId) => new(true, messageId);

    public static DiscordSendResult Failure(string code, string message, int? retryAfterMs = null)
        => new(false, ErrorCode: code, ErrorMessage: message, RetryAfterMs: retryAfterMs);
}
