using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Natter.Chat;

/// <summary>
/// Serializes timestamps as RFC 3339 UTC with a trailing <c>Z</c> and normalizes inbound
/// offsets to UTC. Unix seconds are converted at the adapter boundary, never stored.
/// </summary>
public sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Expected an RFC 3339 timestamp string.");

        var text = reader.GetString();
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
            throw new JsonException($"'{text}' is not a valid RFC 3339 timestamp.");

        return value.ToUniversalTime();
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture));
}

/// <summary>
/// Optional JSON projection of NLMP envelopes (lowerCamelCase, RFC 3339 UTC, string ids).
/// This is not a wire protocol: there is no handshake, routing or service discovery.
/// </summary>
public static class NlmpJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
            new UtcDateTimeOffsetConverter(),
        },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static string SerializeEvent(ChatEvent chatEvent)
    {
        ArgumentNullException.ThrowIfNull(chatEvent);
        return JsonSerializer.Serialize(chatEvent, chatEvent.GetType(), Options);
    }

    public static string SerializeCommand(ChatCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return JsonSerializer.Serialize(command, command.GetType(), Options);
    }

    public static string SerializeAck(ChatAck ack)
    {
        ArgumentNullException.ThrowIfNull(ack);
        return JsonSerializer.Serialize(ack, Options);
    }

    public static JsonElement SerializeEventElement(ChatEvent chatEvent)
    {
        ArgumentNullException.ThrowIfNull(chatEvent);
        return JsonSerializer.SerializeToElement(chatEvent, chatEvent.GetType(), Options);
    }

    /// <summary>
    /// Reads one event envelope. Unknown event types yield <c>null</c> (consumers ignore
    /// them); a missing or incompatible major version throws <see cref="NlmpVersionException"/>.
    /// Unknown JSON fields are ignored.
    /// </summary>
    public static ChatEvent? DeserializeEvent(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var version = ReadVersion(root);
        Nlmp.EnsureCompatible(version);

        var type = ReadString(root, "type");
        if (string.IsNullOrWhiteSpace(type))
            return null;

        return type switch
        {
            ChatEventTypes.MessageCreated => root.Deserialize<MessageCreatedEvent>(Options),
            ChatEventTypes.MessageUpdated => root.Deserialize<MessageUpdatedEvent>(Options),
            ChatEventTypes.MessageDeleted => root.Deserialize<MessageDeletedEvent>(Options),
            ChatEventTypes.InteractionCreated => root.Deserialize<InteractionCreatedEvent>(Options),
            ChatEventTypes.UpstreamStatus => root.Deserialize<UpstreamStatusEvent>(Options),
            _ => null,
        };
    }

    /// <summary>Reads one command envelope; unknown command types yield <c>null</c>.</summary>
    public static ChatCommand? DeserializeCommand(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Nlmp.EnsureCompatible(ReadVersion(root));

        var type = ReadString(root, "type");
        if (string.IsNullOrWhiteSpace(type))
            return null;

        return type switch
        {
            ChatCommandTypes.MessageSend => root.Deserialize<MessageSendCommand>(Options),
            ChatCommandTypes.MessageUpdate => root.Deserialize<MessageUpdateCommand>(Options),
            ChatCommandTypes.MessageDelete => root.Deserialize<MessageDeleteCommand>(Options),
            ChatCommandTypes.TypingStart => root.Deserialize<TypingStartCommand>(Options),
            ChatCommandTypes.InteractionRespond => root.Deserialize<InteractionRespondCommand>(Options),
            _ => null,
        };
    }

    public static ChatAck DeserializeAck(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var document = JsonDocument.Parse(json);
        Nlmp.EnsureCompatible(ReadVersion(document.RootElement));

        return document.RootElement.Deserialize<ChatAck>(Options)
               ?? throw new JsonException("The NLMP ack envelope was empty.");
    }

    private static string? ReadVersion(JsonElement root)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("version", out var version)
            && version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null;

    private static string? ReadString(JsonElement root, string propertyName)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
