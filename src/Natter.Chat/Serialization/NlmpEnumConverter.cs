using System.Text.Json;
using System.Text.Json.Serialization;

namespace Natter.Chat;

/// <summary>
/// Tolerant enum binding: consumers MUST ignore unknown enum values instead of failing an
/// otherwise valid envelope. Unknown values fall back to the enum's "unknown-ish" member.
/// </summary>
public sealed class NlmpEnumConverterFactory : JsonConverterFactory
{
    public static NlmpEnumConverterFactory Instance { get; } = new();

    public override bool CanConvert(Type typeToConvert)
        => typeToConvert == typeof(ConversationKind)
           || typeToConvert == typeof(UserKind)
           || typeToConvert == typeof(AttachmentKind)
           || typeToConvert == typeof(MentionKind)
           || typeToConvert == typeof(InteractionKind)
           || typeToConvert == typeof(ContentFormat)
           || typeToConvert == typeof(UpstreamState);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var fallback = FallbackFor(typeToConvert);
        var converterType = typeof(FallbackEnumConverter<>).MakeGenericType(typeToConvert);
        return (JsonConverter)Activator.CreateInstance(converterType, fallback)!;
    }

    private static object FallbackFor(Type type)
        => type switch
        {
            _ when type == typeof(ConversationKind) => ConversationKind.Unknown,
            _ when type == typeof(UserKind) => UserKind.Unknown,
            _ when type == typeof(AttachmentKind) => AttachmentKind.Unknown,
            _ when type == typeof(MentionKind) => MentionKind.User,
            _ when type == typeof(InteractionKind) => InteractionKind.Unknown,
            _ when type == typeof(ContentFormat) => ContentFormat.Plain,
            _ when type == typeof(UpstreamState) => UpstreamState.Error,
            _ => throw new NotSupportedException($"No NLMP enum fallback for '{type.FullName}'."),
        };

    private sealed class FallbackEnumConverter<T>(T fallback) : JsonConverter<T>
        where T : struct, Enum
    {
        private readonly T _fallback = fallback;

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                var text = reader.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    foreach (var value in Enum.GetValues<T>())
                    {
                        if (string.Equals(value.ToString(), text, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(
                                JsonNamingPolicy.CamelCase.ConvertName(value.ToString()),
                                text,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return value;
                        }
                    }
                }

                return _fallback;
            }

            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number))
            {
                var value = (T)Enum.ToObject(typeof(T), number);
                return Enum.IsDefined(value) ? value : _fallback;
            }

            throw new JsonException($"Expected an enum string or number for '{typeof(T).Name}'.");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            => writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
    }
}
