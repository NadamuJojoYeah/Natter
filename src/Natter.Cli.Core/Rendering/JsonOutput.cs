using System.Text.Json;
using System.Text.Json.Serialization;
using Natter.Abstractions.Events;

namespace Natter.Cli.Core.Rendering;

/// <summary>Shared JSON formatting for machine-readable command output.</summary>
public static class JsonOutput
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Serializes single-line with the shared compact policy.</summary>
    public static string SerializeCompact(object value) => JsonSerializer.Serialize(value, CompactOptions);

    /// <summary>Serializes the concrete runtime event type, not the IDomainEvent interface.</summary>
    public static string SerializePayload(IDomainEvent domainEvent)
        => JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), Options);

    /// <summary>Single-line payload used inside tables and log lines.</summary>
    public static string SerializePayloadCompact(IDomainEvent domainEvent)
        => JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), CompactOptions);

    /// <summary>
    /// Single-line machine-readable policy shared by every `--json` command (S2-E8): field naming,
    /// enum casing and indentation must not differ between commands.
    /// </summary>
    public static JsonSerializerOptions CompactOptions { get; } = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static object Project(DomainEventEnvelope envelope) => new
    {
        sequence = envelope.Sequence,
        eventName = envelope.EventName,
        eventId = envelope.EventId,
        occurredAt = envelope.OccurredAt,
        origin = envelope.Origin,
        payload = JsonSerializer.SerializeToElement(envelope.Event, envelope.Event.GetType(), Options),
    };
}
