using System.Text.Json;
using Natter.Abstractions.Ports;
using Natter.Chat.Safety;

namespace Natter.Chat.Satori;

/// <summary>Adapter policy that cannot be discovered from Satori events.</summary>
public sealed record SatoriMappingOptions
{
    public string ProtocolId { get; init; } = "satori";

    public ChatAttachmentPolicy Attachments { get; init; } = new();

    public bool EmitLoginStatusEvents { get; init; } = true;
}

/// <summary>
/// Maps Satori EVENT frames to NLMP events. Messages are lossy text + attachments +
/// mentions; incomplete updates are merged from cache or skipped, never emitted partial.
/// </summary>
public sealed class SatoriEventMapper
{
    private readonly IClock _clock;
    private readonly SatoriMappingOptions _options;
    private readonly SatoriMessageCache _cache;

    public SatoriEventMapper(IClock clock, SatoriMappingOptions? options = null, SatoriMessageCache? cache = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? new SatoriMappingOptions();
        _cache = cache ?? new SatoriMessageCache();
    }

    public SatoriMessageCache SnapshotCache => _cache;

    public ChatEvent? Map(SatoriEvent? satoriEvent)
    {
        if (satoriEvent is null)
            return null;

        var type = satoriEvent.Type?.Trim();
        if (string.IsNullOrEmpty(type))
            return null;

        var normalizedType = type.ToLowerInvariant();

        if (normalizedType is "login-added" or "login-removed")
            return MapLoginStatus(satoriEvent, added: normalizedType == "login-added");

        var source = BuildSource(satoriEvent.Login);
        if (source is null)
            return null;

        var receivedAt = _clock.UtcNow;
        var occurredAt = FromUnixSeconds(satoriEvent.Timestamp) ?? receivedAt;

        if (normalizedType is "message-created" or "message-updated")
        {
            return MapMessageEvent(
                satoriEvent,
                source,
                occurredAt,
                receivedAt,
                updated: normalizedType == "message-updated");
        }

        if (normalizedType == "message-deleted")
            return MapDeletedEvent(satoriEvent, source, occurredAt, receivedAt);

        if (normalizedType.StartsWith("interaction", StringComparison.Ordinal))
            return MapInteractionEvent(satoriEvent, source, occurredAt, receivedAt);

        return null;
    }

    private Source? BuildSource(SatoriLogin? login)
    {
        if (login is null
            || string.IsNullOrWhiteSpace(login.Platform)
            || string.IsNullOrWhiteSpace(login.User?.Id))
        {
            return null;
        }

        return new Source(_options.ProtocolId, login.Platform, login.User.Id);
    }

    private ChatEvent? MapMessageEvent(
        SatoriEvent satoriEvent,
        Source source,
        DateTimeOffset occurredAt,
        DateTimeOffset receivedAt,
        bool updated)
    {
        var dto = satoriEvent.Message;
        if (string.IsNullOrWhiteSpace(dto?.Id))
            return null;

        var messageId = dto.Id;
        _cache.TryGet(source, messageId, out var cached);

        var channel = dto.Channel ?? satoriEvent.Channel;
        if (cached is not null && channel?.Id is { } channelId && cached.Conversation.Id != channelId)
            cached = null;
        var guildId = (dto.Guild ?? satoriEvent.Guild)?.Id;
        var sender = MapUser(dto.User ?? satoriEvent.User) ?? cached?.Sender;

        SatoriContentMapping? mapped = null;
        if (dto.Content is not null)
            mapped = SatoriContentMapper.Map(dto.Content, _options.Attachments);

        var content = mapped?.Content ?? cached?.Content;
        var conversation = BuildConversation(channel, guildId, cached?.Conversation);

        if (conversation is null || sender is null || content is null)
            return null;

        var createdAt = FromUnixSeconds(dto.CreatedAt)
                        ?? FromUnixSeconds(satoriEvent.Timestamp)
                        ?? cached?.CreatedAt
                        ?? receivedAt;

        var editedAt = FromUnixSeconds(dto.UpdatedAt) ?? (updated ? occurredAt : cached?.EditedAt);
        var replyTo = mapped?.ReplyTo ?? cached?.ReplyTo;

        var message = new Message(messageId, conversation, sender, content, createdAt, replyTo, editedAt);
        if (message.Validate().Count > 0)
            return null;

        _cache.Store(source, message);

        var extensions = BuildExtensions(satoriEvent);
        var eventId = ChatEventId.Derive(source, updated ? ChatEventTypes.MessageUpdated : ChatEventTypes.MessageCreated, satoriEvent.Sn);
        var payload = new MessagePayload(message);

        return updated
            ? new MessageUpdatedEvent
            {
                Id = eventId,
                Source = source,
                Sequence = satoriEvent.Sn,
                OccurredAt = occurredAt,
                ReceivedAt = receivedAt,
                Extensions = extensions,
                Payload = payload,
            }
            : new MessageCreatedEvent
            {
                Id = eventId,
                Source = source,
                Sequence = satoriEvent.Sn,
                OccurredAt = occurredAt,
                ReceivedAt = receivedAt,
                Extensions = extensions,
                Payload = payload,
            };
    }

    private ChatEvent? MapDeletedEvent(
        SatoriEvent satoriEvent,
        Source source,
        DateTimeOffset occurredAt,
        DateTimeOffset receivedAt)
    {
        var dto = satoriEvent.Message;
        var messageId = dto?.Id ?? satoriEvent.MessageId;
        if (string.IsNullOrWhiteSpace(messageId))
            return null;

        _cache.TryGet(source, messageId, out var cached);
        var channel = dto?.Channel ?? satoriEvent.Channel;
        if (cached is not null && channel?.Id is { } channelId && cached.Conversation.Id != channelId)
            cached = null;
        var guildId = (dto?.Guild ?? satoriEvent.Guild)?.Id;
        var conversation = BuildConversation(channel, guildId, cached?.Conversation);
        if (conversation is null)
            return null;

        _cache.Remove(source, messageId);

        return new MessageDeletedEvent
        {
            Id = ChatEventId.Derive(source, ChatEventTypes.MessageDeleted, satoriEvent.Sn),
            Source = source,
            Sequence = satoriEvent.Sn,
            OccurredAt = occurredAt,
            ReceivedAt = receivedAt,
            Extensions = BuildExtensions(satoriEvent),
            Payload = new MessageDeletedPayload(conversation, messageId, occurredAt),
        };
    }

    private ChatEvent? MapInteractionEvent(
        SatoriEvent satoriEvent,
        Source source,
        DateTimeOffset occurredAt,
        DateTimeOffset receivedAt)
    {
        var dto = satoriEvent.Interaction;
        if (dto is null || string.IsNullOrWhiteSpace(dto.Id))
            return null;

        var user = MapUser(dto.User ?? satoriEvent.User);
        var channel = dto.Channel ?? satoriEvent.Channel;
        var conversation = BuildConversation(channel, satoriEvent.Guild?.Id, null);
        if (user is null || conversation is null)
            return null;

        var interaction = new Interaction(
            dto.Id,
            MapInteractionKind(dto.Type, satoriEvent.Type),
            user,
            conversation,
            occurredAt,
            NormalizeCommand(dto.Command),
            dto.Options,
            dto.Values,
            dto.ReplyToken);

        return new InteractionCreatedEvent
        {
            Id = ChatEventId.Derive(source, ChatEventTypes.InteractionCreated, satoriEvent.Sn),
            Source = source,
            Sequence = satoriEvent.Sn,
            OccurredAt = occurredAt,
            ReceivedAt = receivedAt,
            Extensions = BuildExtensions(satoriEvent),
            Payload = new InteractionPayload(interaction),
        };
    }

    private ChatEvent? MapLoginStatus(SatoriEvent satoriEvent, bool added)
    {
        if (!_options.EmitLoginStatusEvents)
            return null;

        var source = BuildSource(satoriEvent.Login);
        if (source is null)
            return null;

        var receivedAt = _clock.UtcNow;
        var occurredAt = FromUnixSeconds(satoriEvent.Timestamp) ?? receivedAt;

        return new UpstreamStatusEvent
        {
            Id = ChatEventId.Derive(source, ChatEventTypes.UpstreamStatus, satoriEvent.Sn),
            Source = source,
            Sequence = satoriEvent.Sn,
            OccurredAt = occurredAt,
            ReceivedAt = receivedAt,
            Extensions = BuildExtensions(satoriEvent),
            Payload = new UpstreamStatusPayload(added ? UpstreamState.Ready : UpstreamState.Disconnected),
        };
    }

    private static Conversation? BuildConversation(SatoriChannel? channel, string? guildId, Conversation? cached)
    {
        var id = channel?.Id ?? cached?.Id;
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var kind = channel is not null
            ? MapConversationKind(channel, guildId)
            : cached?.Kind ?? ConversationKind.Unknown;

        return new Conversation(
            id,
            kind,
            channel?.ParentId ?? cached?.ParentId,
            guildId ?? cached?.GuildId,
            channel?.Name ?? cached?.Title);
    }

    private static ConversationKind MapConversationKind(SatoriChannel channel, string? guildId)
    {
        var inGuild = !string.IsNullOrWhiteSpace(guildId);

        return channel.Type switch
        {
            null => inGuild ? ConversationKind.Guild : ConversationKind.Unknown,
            SatoriChannelTypes.Direct => ConversationKind.Direct,
            SatoriChannelTypes.Category => ConversationKind.Unknown,
            SatoriChannelTypes.Text => inGuild ? ConversationKind.Guild : ConversationKind.Group,
            SatoriChannelTypes.Voice or SatoriChannelTypes.Live => inGuild ? ConversationKind.Guild : ConversationKind.Unknown,
            _ => ConversationKind.Unknown,
        };
    }

    private static User? MapUser(SatoriUser? dto)
    {
        if (dto is null || string.IsNullOrWhiteSpace(dto.Id))
            return null;

        var name = string.IsNullOrWhiteSpace(dto.Name) ? dto.Id : dto.Name;
        var kind = dto.IsBot switch
        {
            true => UserKind.Bot,
            false => UserKind.Human,
            null => UserKind.Unknown,
        };

        return new User(dto.Id, name, kind, dto.Nick, dto.Avatar);
    }

    private static InteractionKind MapInteractionKind(string? interactionType, string? eventType)
    {
        var value = (interactionType ?? eventType ?? string.Empty).ToLowerInvariant();

        if (value.Contains("button", StringComparison.Ordinal))
            return InteractionKind.Button;
        if (value.Contains("select", StringComparison.Ordinal))
            return InteractionKind.Select;
        if (value.Contains("modal", StringComparison.Ordinal))
            return InteractionKind.Modal;
        if (value.Contains("command", StringComparison.Ordinal) || value.Contains("slash", StringComparison.Ordinal))
            return InteractionKind.Slash;

        return InteractionKind.Unknown;
    }

    private static string? NormalizeCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var normalized = command.Trim().TrimStart('/').Trim().ToLowerInvariant();
        return normalized.Length == 0 ? null : normalized;
    }

    private static IReadOnlyDictionary<string, JsonElement>? BuildExtensions(SatoriEvent satoriEvent)
    {
        if (satoriEvent.Sn is not { } sn)
            return null;

        return new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["satori"] = JsonSerializer.SerializeToElement(new SatoriSnExtension(sn), SatoriJson.Options),
        };
    }

    private static DateTimeOffset? FromUnixSeconds(long? seconds)
    {
        if (seconds is not { } value || value < -62135596800L || value > 253402300799L)
            return null;

        return DateTimeOffset.FromUnixTimeSeconds(value).ToUniversalTime();
    }

    private sealed record SatoriSnExtension(long Sn);
}
