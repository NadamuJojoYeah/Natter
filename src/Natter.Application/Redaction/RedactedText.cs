using System.Text.Json;
using System.Text.Json.Serialization;

namespace Natter.Application.Redaction;

/// <summary>
/// Type boundary for diagnostic text that may leave the process (health DTOs, event details).
/// It keeps the raw value for internal diagnosis but every ordinary rendering — ToString,
/// implicit string conversion and JSON — goes through the shared redaction core. A new output
/// path therefore cannot print the raw value by accident.
/// </summary>
[JsonConverter(typeof(RedactedTextJsonConverter))]
public sealed class RedactedText
{
    public RedactedText(string? raw) => Raw = raw ?? string.Empty;

    /// <summary>Internal diagnostic value. Never write this to a console, JSON or event payload.</summary>
    public string Raw { get; }

    public string Redacted => SecretRedaction.Apply(Raw, SecretRedactionOptions.Admin);

    public override string ToString() => Redacted;

    public static implicit operator RedactedText(string? raw) => new(raw);

    public static implicit operator string(RedactedText value) => value.Redacted;

    public static RedactedText? FromNullable(string? raw)
        => raw is null ? null : new RedactedText(raw);
}

/// <summary>Serializes <see cref="RedactedText"/> as the redacted string, never the raw value.</summary>
public sealed class RedactedTextJsonConverter : JsonConverter<RedactedText>
{
    public override RedactedText Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString());

    public override void Write(Utf8JsonWriter writer, RedactedText value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Redacted);
}
