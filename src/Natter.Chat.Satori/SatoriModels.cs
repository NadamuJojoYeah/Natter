using System.Text.Json;
using System.Text.Json.Serialization;

namespace Natter.Chat.Satori;

// Minimal Satori wire DTOs. These types never leave the adapter: NLMP core does not
// reference them, and a native adapter does not need them either.

/// <summary>Satori user resource subset.</summary>
public sealed record SatoriUser
{
    public string? Id { get; init; }

    public string? Name { get; init; }

    public string? Nick { get; init; }

    public string? Avatar { get; init; }

    [JsonPropertyName("is_bot")]
    public bool? IsBot { get; init; }
}

/// <summary>Satori channel resource subset.</summary>
public sealed record SatoriChannel
{
    public string? Id { get; init; }

    public int? Type { get; init; }

    public string? Name { get; init; }

    [JsonPropertyName("parent_id")]
    public string? ParentId { get; init; }
}

/// <summary>Satori guild resource subset.</summary>
public sealed record SatoriGuild
{
    public string? Id { get; init; }

    public string? Name { get; init; }
}

/// <summary>Satori login resource subset (bot identity + feature discovery).</summary>
public sealed record SatoriLogin
{
    public string? Platform { get; init; }

    public SatoriUser? User { get; init; }

    public IReadOnlyList<string>? Features { get; init; }
}

/// <summary>Satori message resource subset.</summary>
public sealed record SatoriMessage
{
    public string? Id { get; init; }

    public string? Content { get; init; }

    public SatoriChannel? Channel { get; init; }

    public SatoriGuild? Guild { get; init; }

    public SatoriUser? User { get; init; }

    [JsonPropertyName("created_at")]
    public long? CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public long? UpdatedAt { get; init; }
}

/// <summary>Satori interaction resource subset.</summary>
public sealed record SatoriInteraction
{
    public string? Id { get; init; }

    public string? Type { get; init; }

    public string? Command { get; init; }

    public IReadOnlyDictionary<string, JsonElement>? Options { get; init; }

    public IReadOnlyList<string>? Values { get; init; }

    public SatoriUser? User { get; init; }

    public SatoriChannel? Channel { get; init; }

    [JsonPropertyName("reply_token")]
    public string? ReplyToken { get; init; }
}

/// <summary>Satori EVENT frame subset.</summary>
public sealed record SatoriEvent
{
    public long? Sn { get; init; }

    public string? Type { get; init; }

    public long? Timestamp { get; init; }

    public SatoriLogin? Login { get; init; }

    public SatoriMessage? Message { get; init; }

    public SatoriChannel? Channel { get; init; }

    public SatoriGuild? Guild { get; init; }

    public SatoriUser? User { get; init; }

    public SatoriInteraction? Interaction { get; init; }

    [JsonPropertyName("message_id")]
    public string? MessageId { get; init; }
}

/// <summary>Satori channel type constants used by the mapping.</summary>
public static class SatoriChannelTypes
{
    public const int Text = 0;
    public const int Direct = 1;
    public const int Category = 2;
    public const int Voice = 3;
    public const int Live = 4;
}

/// <summary>Serialization settings for Satori wire DTOs (snake_case attributes preserved).</summary>
public static class SatoriJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static SatoriEvent? DeserializeEvent(string json)
        => JsonSerializer.Deserialize<SatoriEvent>(json, Options);
}
