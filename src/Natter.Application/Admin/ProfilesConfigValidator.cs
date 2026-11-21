using System.Globalization;
using System.Text.Json;

namespace Natter.Application.Admin;

/// <summary>
/// Structural validation for <c>profiles.json</c>. Mirrors the rules enforced by
/// <c>RuntimeProfileStore</c> when the host loads profiles, but runs before anything is written so
/// the admin surface can reject a document that would break the next start.
/// </summary>
public static class ProfilesConfigValidator
{
    public static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static IReadOnlyList<AdminConfigIssue> Validate(string content)
    {
        var issues = new List<AdminConfigIssue>();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content ?? string.Empty, ParseOptions);
        }
        catch (JsonException exception)
        {
            issues.Add(new AdminConfigIssue(
                "$",
                AdminCodes.ConfigInvalidJson,
                $"profiles.json is not valid JSON: {exception.Message}"));
            return issues;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new AdminConfigIssue("$", AdminCodes.ConfigSchemaInvalid, "profiles.json root must be an object.", "object", root.ValueKind.ToString()));
                return issues;
            }

            if (root.TryGetProperty("schemaVersion", out var schemaVersion))
            {
                if (schemaVersion.ValueKind != JsonValueKind.Number
                    || !schemaVersion.TryGetInt32(out var version)
                    || version != 1)
                {
                    issues.Add(new AdminConfigIssue(
                        "/schemaVersion",
                        AdminCodes.ConfigSchemaInvalid,
                        "schemaVersion must be 1.",
                        "1",
                        schemaVersion.ToString()));
                }
            }
            else
            {
                issues.Add(new AdminConfigIssue("/schemaVersion", AdminCodes.ConfigSchemaInvalid, "schemaVersion is required.", "1", null));
            }

            if (!root.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Array)
            {
                issues.Add(new AdminConfigIssue("/profiles", AdminCodes.ConfigSchemaInvalid, "profiles must be an array.", "array", null));
                return issues;
            }

            if (profiles.GetArrayLength() == 0)
            {
                issues.Add(new AdminConfigIssue("/profiles", AdminCodes.ConfigSchemaInvalid, "profiles must not be empty.", "non-empty array", "0"));
                return issues;
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var enabledById = new Dictionary<string, bool>(StringComparer.Ordinal);
            var index = 0;

            foreach (var profile in profiles.EnumerateArray())
            {
                var path = $"/profiles/{index}";
                index++;

                if (profile.ValueKind != JsonValueKind.Object)
                {
                    issues.Add(new AdminConfigIssue(path, AdminCodes.ConfigSchemaInvalid, "profile must be an object.", "object", profile.ValueKind.ToString()));
                    continue;
                }

                var id = ReadString(profile, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    issues.Add(new AdminConfigIssue(path + "/id", AdminCodes.ConfigSchemaInvalid, "profile id is required.", "non-empty string", null));
                    continue;
                }

                if (!ids.Add(id))
                {
                    issues.Add(new AdminConfigIssue(path + "/id", AdminCodes.ConfigSchemaInvalid, $"duplicate profile id '{id}'.", "unique id", id));
                    continue;
                }

                if (profile.TryGetProperty("supportTier", out var tier))
                {
                    if (tier.ValueKind != JsonValueKind.Number
                        || !tier.TryGetInt32(out var tierValue)
                        || tierValue is not (1 or 2))
                    {
                        issues.Add(new AdminConfigIssue(path + "/supportTier", AdminCodes.ConfigSchemaInvalid, "supportTier must be 1 or 2.", "1 or 2", tier.ToString()));
                    }
                }

                ValidateRef(profile, "prompt.provider", path + "/prompt", "provider", "host#static", issues);
                ValidateRef(profile, "context.assembler", path + "/context", "assembler", "host#recent-window", issues);
                ValidateRef(profile, "memory.store", path + "/memory", "store", "host#inline", issues);

                if (profile.TryGetProperty("ai", out var ai))
                {
                    if (ai.ValueKind != JsonValueKind.Object)
                    {
                        issues.Add(new AdminConfigIssue(path + "/ai", AdminCodes.ConfigSchemaInvalid, "ai must be an object.", "object", ai.ValueKind.ToString()));
                    }
                    else
                    {
                        var provider = ReadString(ai, "provider");
                        if (string.IsNullOrWhiteSpace(provider) || provider.IndexOf('#', StringComparison.Ordinal) <= 0)
                        {
                            issues.Add(new AdminConfigIssue(path + "/ai/provider", AdminCodes.ConfigSchemaInvalid, "ai.provider must be 'pluginId#responderId'.", "owner#id", provider));
                        }
                    }
                }
                else
                {
                    issues.Add(new AdminConfigIssue(path + "/ai", AdminCodes.ConfigSchemaInvalid, "ai is required.", "object", null));
                }

                if (profile.TryGetProperty("context", out var context))
                {
                    if (context.ValueKind != JsonValueKind.Object)
                    {
                        issues.Add(new AdminConfigIssue(path + "/context", AdminCodes.ConfigSchemaInvalid, "context must be an object.", "object", context.ValueKind.ToString()));
                    }
                    else
                    {
                        ValidateBudget(context, "maxInputCharacters", path + "/context", strictPositive: true, 12000, issues);
                        ValidateBudget(context, "maxMemoryItems", path + "/context", strictPositive: false, 8, issues);
                        ValidateBudget(context, "maxSummaryCharacters", path + "/context", strictPositive: false, 1200, issues);
                        ValidateBudget(context, "maxRecentMessages", path + "/context", strictPositive: false, 20, issues);
                        ValidateBudget(context, "maxRecentCharacters", path + "/context", strictPositive: false, 24000, issues);
                    }
                }

                if (profile.TryGetProperty("history", out var history) && history.ValueKind == JsonValueKind.Object)
                {
                    var scope = ReadString(history, "scope");
                    if (scope is not null && scope is not ("conversation" or "user"))
                    {
                        issues.Add(new AdminConfigIssue(path + "/history/scope", AdminCodes.ConfigSchemaInvalid, "history.scope must be 'conversation' or 'user'.", "conversation|user", scope));
                    }
                }

                if (profile.TryGetProperty("match", out var match) && match.ValueKind == JsonValueKind.Object)
                {
                    foreach (var listName in new[] { "protocols", "platforms", "guilds", "conversations", "actors" })
                    {
                        if (match.TryGetProperty(listName, out var list) && list.ValueKind != JsonValueKind.Array)
                        {
                            issues.Add(new AdminConfigIssue(path + "/match/" + listName, AdminCodes.ConfigSchemaInvalid, "match list must be an array.", "array", list.ValueKind.ToString()));
                        }
                    }
                }

                enabledById[id] = !(profile.TryGetProperty("policy", out var policy)
                                    && policy.ValueKind == JsonValueKind.Object
                                    && policy.TryGetProperty("enabled", out var enabled)
                                    && enabled.ValueKind == JsonValueKind.False);
            }

            var defaultProfile = ReadString(root, "defaultProfile");
            if (string.IsNullOrWhiteSpace(defaultProfile))
            {
                issues.Add(new AdminConfigIssue("/defaultProfile", AdminCodes.ConfigSchemaInvalid, "defaultProfile is required.", "existing profile id", null));
            }
            else if (!ids.Contains(defaultProfile))
            {
                issues.Add(new AdminConfigIssue("/defaultProfile", AdminCodes.ConfigSchemaInvalid, $"defaultProfile '{defaultProfile}' does not exist.", "existing profile id", defaultProfile));
            }
            else if (!enabledById.GetValueOrDefault(defaultProfile, true))
            {
                issues.Add(new AdminConfigIssue("/profiles", AdminCodes.ConfigSchemaInvalid, $"defaultProfile '{defaultProfile}' must be enabled.", "policy.enabled = true", "false"));
            }
        }

        return issues;
    }

    private static void ValidateRef(
        JsonElement profile,
        string member,
        string parentPath,
        string property,
        string example,
        List<AdminConfigIssue> issues)
    {
        var parts = member.Split('.');
        if (!profile.TryGetProperty(parts[0], out var parent) || parent.ValueKind != JsonValueKind.Object)
            return;

        var value = ReadString(parent, parts[1]);
        if (value is null)
            return;

        var hash = value.IndexOf('#', StringComparison.Ordinal);
        if (hash <= 0 || hash >= value.Length - 1)
        {
            issues.Add(new AdminConfigIssue(
                $"{parentPath}/{property}",
                AdminCodes.ConfigSchemaInvalid,
                $"{member} must use 'owner#id'.",
                example,
                value));
        }
    }

    private static void ValidateBudget(
        JsonElement context,
        string name,
        string parentPath,
        bool strictPositive,
        int fallback,
        List<AdminConfigIssue> issues)
    {
        if (!context.TryGetProperty(name, out var value))
            return;

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            issues.Add(new AdminConfigIssue($"{parentPath}/{name}", AdminCodes.ConfigSchemaInvalid, $"{name} must be an integer.", strictPositive ? "> 0" : ">= 0", value.ToString()));
            return;
        }

        if (strictPositive ? number <= 0 : number < 0)
        {
            issues.Add(new AdminConfigIssue(
                $"{parentPath}/{name}",
                AdminCodes.ConfigSchemaInvalid,
                $"{name} must be {(strictPositive ? "greater than 0" : "0 or greater")}.",
                (strictPositive ? "> 0" : ">= 0") + $" (default {fallback.ToString(CultureInfo.InvariantCulture)})",
                number.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static string? ReadString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
