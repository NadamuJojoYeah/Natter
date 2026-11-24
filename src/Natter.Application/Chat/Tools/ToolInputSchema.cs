using System.Text.Json;
using System.Text.RegularExpressions;

namespace Natter.Application.Chat;

/// <summary>One input-schema issue with a JSON Pointer.</summary>
public sealed record ToolSchemaIssue(string Pointer, string Code, string Message);

/// <summary>
/// Small JSON Schema subset used to validate tool arguments (draft 2020-12 keywords:
/// <c>type</c>, <c>properties</c>, <c>required</c>, <c>additionalProperties</c>, <c>enum</c>,
/// <c>minimum</c>/<c>maximum</c>, <c>minLength</c>/<c>maxLength</c>, <c>pattern</c>,
/// <c>minItems</c>/<c>maxItems</c>, <c>items</c>). Unknown keywords are ignored on purpose: tool
/// schemas must not be able to break the host.
/// </summary>
public static class ToolInputSchema
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    public static bool TryParse(string? schemaJson, out JsonElement schema)
    {
        schema = default;
        if (string.IsNullOrWhiteSpace(schemaJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(schemaJson, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            schema = document.RootElement.Clone();
            return schema.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static IReadOnlyList<ToolSchemaIssue> Validate(JsonElement schema, JsonElement value)
    {
        var issues = new List<ToolSchemaIssue>();
        ValidateNode(schema, value, string.Empty, issues);
        return issues;
    }

    private static void ValidateNode(JsonElement schema, JsonElement value, string pointer, List<ToolSchemaIssue> issues)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return;

        if (schema.TryGetProperty("type", out var type))
            ValidateType(type, value, pointer, issues);

        if (schema.TryGetProperty("enum", out var enumValues) && enumValues.ValueKind == JsonValueKind.Array)
        {
            var matched = enumValues.EnumerateArray().Any(candidate => JsonEquals(candidate, value));
            if (!matched)
            {
                issues.Add(new ToolSchemaIssue(
                    pointer.Length == 0 ? "$" : pointer,
                    "schema.enum",
                    $"Value must be one of {enumValues.GetRawText()}."));
            }
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                ValidateObject(schema, value, pointer, issues);
                break;
            case JsonValueKind.Array:
                ValidateArray(schema, value, pointer, issues);
                break;
            case JsonValueKind.String:
                ValidateString(schema, value, pointer, issues);
                break;
            case JsonValueKind.Number:
                ValidateNumber(schema, value, pointer, issues);
                break;
        }
    }

    private static void ValidateType(JsonElement type, JsonElement value, string pointer, List<ToolSchemaIssue> issues)
    {
        var allowed = type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToArray()
            : type.ValueKind == JsonValueKind.String
                ? [type.GetString()!]
                : [];

        if (allowed.Length == 0)
            return;

        if (!allowed.Any(type => TypeMatches(type, value)))
        {
            issues.Add(new ToolSchemaIssue(
                pointer.Length == 0 ? "$" : pointer,
                "schema.type",
                $"Expected {string.Join(" or ", allowed)}, got {ValueTypeName(value)}."));
        }
    }

    private static void ValidateObject(JsonElement schema, JsonElement value, string pointer, List<ToolSchemaIssue> issues)
    {
        var properties = schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object
            ? props
            : default;

        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in required.EnumerateArray())
            {
                if (name.ValueKind != JsonValueKind.String)
                    continue;

                var propertyName = name.GetString()!;
                if (!value.TryGetProperty(propertyName, out _))
                {
                    issues.Add(new ToolSchemaIssue(
                        JoinPointer(pointer, propertyName),
                        "schema.required",
                        $"Required argument '{propertyName}' is missing."));
                }
            }
        }

        var allowsAdditional = !schema.TryGetProperty("additionalProperties", out var additional)
                               || additional.ValueKind != JsonValueKind.False;

        foreach (var property in value.EnumerateObject())
        {
            var propertyPointer = JoinPointer(pointer, property.Name);
            if (properties.ValueKind == JsonValueKind.Object
                && properties.TryGetProperty(property.Name, out var propertySchema))
            {
                ValidateNode(propertySchema, property.Value, propertyPointer, issues);
                continue;
            }

            if (!allowsAdditional)
            {
                issues.Add(new ToolSchemaIssue(
                    propertyPointer,
                    "schema.additional",
                    $"Argument '{property.Name}' is not declared by the tool schema."));
            }
        }
    }

    private static void ValidateArray(JsonElement schema, JsonElement value, string pointer, List<ToolSchemaIssue> issues)
    {
        var count = value.GetArrayLength();

        if (schema.TryGetProperty("minItems", out var minItems)
            && minItems.ValueKind == JsonValueKind.Number
            && minItems.TryGetInt32(out var minimum)
            && count < minimum)
        {
            issues.Add(new ToolSchemaIssue(pointer.Length == 0 ? "$" : pointer, "schema.minItems", $"At least {minimum} item(s) required."));
        }

        if (schema.TryGetProperty("maxItems", out var maxItems)
            && maxItems.ValueKind == JsonValueKind.Number
            && maxItems.TryGetInt32(out var maximum)
            && count > maximum)
        {
            issues.Add(new ToolSchemaIssue(pointer.Length == 0 ? "$" : pointer, "schema.maxItems", $"At most {maximum} item(s) allowed."));
        }

        if (!schema.TryGetProperty("items", out var items))
            return;

        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            ValidateNode(items, item, $"{pointer}[{index}]", issues);
            index++;
        }
    }

    private static void ValidateString(JsonElement schema, JsonElement value, string pointer, List<ToolSchemaIssue> issues)
    {
        var text = value.GetString() ?? string.Empty;

        if (schema.TryGetProperty("minLength", out var minLength)
            && minLength.ValueKind == JsonValueKind.Number
            && minLength.TryGetInt32(out var minimum)
            && text.Length < minimum)
        {
            issues.Add(new ToolSchemaIssue(pointer.Length == 0 ? "$" : pointer, "schema.minLength", $"Must be at least {minimum} character(s)."));
        }

        if (schema.TryGetProperty("maxLength", out var maxLength)
            && maxLength.ValueKind == JsonValueKind.Number
            && maxLength.TryGetInt32(out var maximum)
            && text.Length > maximum)
        {
            issues.Add(new ToolSchemaIssue(pointer.Length == 0 ? "$" : pointer, "schema.maxLength", $"Must be at most {maximum} character(s)."));
        }

        if (schema.TryGetProperty("pattern", out var pattern) && pattern.ValueKind == JsonValueKind.String)
        {
            try
            {
                var regex = new Regex(pattern.GetString()!, RegexOptions.CultureInvariant, RegexTimeout);
                if (!regex.IsMatch(text))
                {
                    issues.Add(new ToolSchemaIssue(
                        pointer.Length == 0 ? "$" : pointer,
                        "schema.pattern",
                        $"Value must match '{pattern.GetString()}'."));
                }
            }
            catch (ArgumentException)
            {
                // An invalid pattern in the tool schema is a tool-authoring bug, not a call failure.
            }
            catch (RegexMatchTimeoutException)
            {
                issues.Add(new ToolSchemaIssue(pointer.Length == 0 ? "$" : pointer, "schema.pattern", "Pattern evaluation timed out."));
            }
        }
    }

    private static void ValidateNumber(JsonElement schema, JsonElement value, string pointer, List<ToolSchemaIssue> issues)
    {
        if (!value.TryGetDouble(out var number))
            return;

        if (schema.TryGetProperty("minimum", out var minimum)
            && minimum.ValueKind == JsonValueKind.Number
            && minimum.TryGetDouble(out var min)
            && number < min)
        {
            issues.Add(new ToolSchemaIssue(pointer.Length == 0 ? "$" : pointer, "schema.minimum", $"Must be >= {min}."));
        }

        if (schema.TryGetProperty("maximum", out var maximum)
            && maximum.ValueKind == JsonValueKind.Number
            && maximum.TryGetDouble(out var max)
            && number > max)
        {
            issues.Add(new ToolSchemaIssue(pointer.Length == 0 ? "$" : pointer, "schema.maximum", $"Must be <= {max}."));
        }
    }

    private static bool TypeMatches(string type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number
                     && value.TryGetDouble(out var number)
                     && number == Math.Truncate(number),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => false,
    };

    private static string ValueTypeName(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => "unknown",
    };

    private static bool JsonEquals(JsonElement left, JsonElement right)
        => left.ValueKind == right.ValueKind
           && string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);

    private static string JoinPointer(string pointer, string property)
        => (pointer.Length == 0 ? string.Empty : pointer)
           + "/"
           + property.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
