using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Natter.Plugin.Abstractions;

/// <summary>
/// JSON Schema draft 2020-12 subset used by NPS v1: type, properties, required,
/// additionalProperties, enum, default, minimum, maximum, pattern, items and oneOf.
/// </summary>
public static class PluginConfigSchema
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    public static IReadOnlyList<PluginConfigIssue> Validate(JsonElement schema, JsonElement config)
    {
        var issues = new List<PluginConfigIssue>();
        ValidateNode(schema, config, string.Empty, issues);
        return issues;
    }

    /// <summary>Returns a deep copy of the config with schema defaults filled in (never writes the user file).</summary>
    public static JsonElement ApplyDefaults(JsonElement schema, JsonElement config)
    {
        JsonNode node;
        try
        {
            node = JsonNode.Parse(config.GetRawText()) ?? new JsonObject();
        }
        catch (JsonException)
        {
            return config;
        }

        ApplyDefaultsNode(schema, node);
        return JsonSerializer.SerializeToElement(node);
    }

    public static JsonElement? ResolvePointer(JsonElement root, string? pointer)
    {
        if (string.IsNullOrEmpty(pointer))
            return root;

        if (!pointer.StartsWith('/'))
            return null;

        var current = root;
        foreach (var rawSegment in pointer[1..].Split('/'))
        {
            var segment = rawSegment.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);

            if (current.ValueKind == JsonValueKind.Object)
            {
                if (!current.TryGetProperty(segment, out current))
                    return null;
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                if (!int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    || index < 0
                    || index >= current.GetArrayLength())
                {
                    return null;
                }

                current = current[index];
            }
            else
            {
                return null;
            }
        }

        return current;
    }

    private static void ValidateNode(
        JsonElement schema,
        JsonElement value,
        string pointer,
        List<PluginConfigIssue> issues)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new PluginConfigIssue(pointer, "Schema node must be an object.", "object", Describe(schema)));
            return;
        }

        if (schema.TryGetProperty("oneOf", out var oneOf) && oneOf.ValueKind == JsonValueKind.Array)
        {
            var matches = 0;
            foreach (var candidate in oneOf.EnumerateArray())
            {
                var candidateIssues = new List<PluginConfigIssue>();
                ValidateNode(candidate, value, pointer, candidateIssues);
                if (candidateIssues.Count == 0)
                    matches++;
            }

            if (matches != 1)
            {
                issues.Add(new PluginConfigIssue(
                    pointer,
                    $"Value must match exactly one oneOf schema (matched {matches}).",
                    "exactly one",
                    matches.ToString(CultureInfo.InvariantCulture)));
            }
        }

        if (schema.TryGetProperty("type", out var type))
        {
            var allowed = type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
                : type.ValueKind == JsonValueKind.String ? [type.GetString()!] : [];

            if (allowed.Length > 0 && !allowed.Any(candidate => MatchesType(candidate, value)))
            {
                issues.Add(new PluginConfigIssue(
                    pointer,
                    $"Expected type {string.Join(" or ", allowed)}.",
                    string.Join(" or ", allowed),
                    Describe(value)));
                return;
            }
        }

        if (schema.TryGetProperty("enum", out var enumValues) && enumValues.ValueKind == JsonValueKind.Array)
        {
            var matched = enumValues.EnumerateArray().Any(candidate => JsonEquals(candidate, value));
            if (!matched)
            {
                issues.Add(new PluginConfigIssue(
                    pointer,
                    "Value must be one of the schema enum values.",
                    enumValues.GetRawText(),
                    value.GetRawText()));
            }
        }

        if (value.ValueKind == JsonValueKind.Object)
            ValidateObject(schema, value, pointer, issues);

        if (value.ValueKind == JsonValueKind.Array
            && schema.TryGetProperty("items", out var items)
            && items.ValueKind == JsonValueKind.Object)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                ValidateNode(items, item, $"{pointer}/{index}", issues);
                index++;
            }
        }

        if (schema.TryGetProperty("pattern", out var pattern) && pattern.ValueKind == JsonValueKind.String
            && value.ValueKind == JsonValueKind.String)
        {
            var regex = pattern.GetString()!;
            try
            {
                if (!Regex.IsMatch(value.GetString()!, regex, RegexOptions.CultureInvariant, RegexTimeout))
                {
                    issues.Add(new PluginConfigIssue(
                        pointer,
                        $"String does not match pattern '{regex}'.",
                        regex,
                        value.GetString()));
                }
            }
            catch (ArgumentException)
            {
                issues.Add(new PluginConfigIssue(pointer, $"Schema pattern '{regex}' is invalid.", "valid regex", regex));
            }
        }

        if ((value.ValueKind == JsonValueKind.Number) && value.TryGetDecimal(out var number))
        {
            if (schema.TryGetProperty("minimum", out var minimum) && minimum.ValueKind == JsonValueKind.Number
                && minimum.TryGetDecimal(out var minimumValue) && number < minimumValue)
            {
                issues.Add(new PluginConfigIssue(
                    pointer,
                    $"Number must be >= {minimumValue.ToString(CultureInfo.InvariantCulture)}.",
                    minimumValue.ToString(CultureInfo.InvariantCulture),
                    number.ToString(CultureInfo.InvariantCulture)));
            }

            if (schema.TryGetProperty("maximum", out var maximum) && maximum.ValueKind == JsonValueKind.Number
                && maximum.TryGetDecimal(out var maximumValue) && number > maximumValue)
            {
                issues.Add(new PluginConfigIssue(
                    pointer,
                    $"Number must be <= {maximumValue.ToString(CultureInfo.InvariantCulture)}.",
                    maximumValue.ToString(CultureInfo.InvariantCulture),
                    number.ToString(CultureInfo.InvariantCulture)));
            }
        }
    }

    private static void ValidateObject(
        JsonElement schema,
        JsonElement value,
        string pointer,
        List<PluginConfigIssue> issues)
    {
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var requiredProperty in required.EnumerateArray())
            {
                if (requiredProperty.ValueKind != JsonValueKind.String)
                    continue;

                var name = requiredProperty.GetString()!;
                if (!value.TryGetProperty(name, out _))
                {
                    issues.Add(new PluginConfigIssue(
                        $"{pointer}/{EscapePointer(name)}",
                        "Required property is missing.",
                        "present",
                        "missing"));
                }
            }
        }

        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            properties = default;

        if (properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                if (value.TryGetProperty(property.Name, out var propertyValue))
                    ValidateNode(property.Value, propertyValue, $"{pointer}/{EscapePointer(property.Name)}", issues);
            }
        }

        if (schema.TryGetProperty("additionalProperties", out var additional))
        {
            if (additional.ValueKind == JsonValueKind.False)
            {
                foreach (var property in value.EnumerateObject())
                {
                    if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out _))
                        continue;

                    issues.Add(new PluginConfigIssue(
                        $"{pointer}/{EscapePointer(property.Name)}",
                        "Additional properties are not allowed.",
                        "known property",
                        $"'{property.Name}'"));
                }
            }
            else if (additional.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in value.EnumerateObject())
                {
                    if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out _))
                        continue;

                    ValidateNode(additional, property.Value, $"{pointer}/{EscapePointer(property.Name)}", issues);
                }
            }
        }
    }

    private static void ApplyDefaultsNode(JsonElement schema, JsonNode? node)
    {
        if (schema.ValueKind != JsonValueKind.Object || node is null)
            return;

        if (node is JsonObject obj && schema.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                if (!obj.TryGetPropertyValue(property.Name, out var child))
                {
                    if (property.Value.TryGetProperty("default", out var defaultValue))
                        obj[property.Name] = JsonNode.Parse(defaultValue.GetRawText())?.DeepClone();

                    continue;
                }

                ApplyDefaultsNode(property.Value, child);
            }
        }

        if (node is JsonArray array && schema.TryGetProperty("items", out var items))
        {
            foreach (var child in array)
                ApplyDefaultsNode(items, child);
        }
    }

    private static bool MatchesType(string type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => false,
    };

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => value.ValueKind.ToString(),
    };

    private static string EscapePointer(string value) => value.Replace("~", "~0").Replace("/", "~1");

    private static bool JsonEquals(JsonElement left, JsonElement right)
    {
        try
        {
            return JsonNode.DeepEquals(
                JsonNode.Parse(left.GetRawText()),
                JsonNode.Parse(right.GetRawText()));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
