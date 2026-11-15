using System.Text.Json;
using Natter.Plugin.Abstractions;

namespace Natter.Infrastructure.Plugins.Nps;

/// <summary>Effective config plus validation result for one plugin directory.</summary>
public sealed record NpsConfigLoadResult(
    JsonElement Raw,
    JsonElement Effective,
    bool Valid,
    IReadOnlyList<PluginConfigIssue> Issues,
    string? ConfigPath,
    string? SchemaPath);

/// <summary>Reads config.json/config.schema.json and validates the documented schema subset.</summary>
public static class NpsPluginConfigLoader
{
    public static NpsConfigLoadResult Load(PluginManifest manifest, string pluginDirectory, string? configDirectory = null)
    {
        var issues = new List<PluginConfigIssue>();
        var embeddedConfigPath = Path.Combine(pluginDirectory, "config.json");
        var externalConfigPath = string.IsNullOrWhiteSpace(configDirectory)
            ? null
            : Path.Combine(configDirectory, manifest.Id + ".json");

        var configPath = externalConfigPath is not null && File.Exists(externalConfigPath)
            ? externalConfigPath
            : embeddedConfigPath;
        var configExists = File.Exists(configPath);
        var schemaPath = manifest.ConfigSchema is null
            ? null
            : Path.GetFullPath(Path.Combine(pluginDirectory, manifest.ConfigSchema));

        JsonElement raw = JsonSerializer.SerializeToElement(new { });

        if (configExists)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(configPath), new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
                raw = document.RootElement.Clone();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                issues.Add(new PluginConfigIssue("$", "config.json could not be read.", "valid JSON", ex.Message));
            }
        }

        if (schemaPath is null)
            return new NpsConfigLoadResult(raw, raw, issues.Count == 0, issues, configExists ? configPath : null, null);

        if (!File.Exists(schemaPath))
        {
            issues.Add(new PluginConfigIssue("$", "Declared config schema was not found.", "existing file", manifest.ConfigSchema));
            return new NpsConfigLoadResult(raw, raw, false, issues, configExists ? configPath : null, schemaPath);
        }

        JsonElement schema;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(schemaPath), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            schema = document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            issues.Add(new PluginConfigIssue("$", "config schema could not be read.", "valid JSON schema", ex.Message));
            return new NpsConfigLoadResult(raw, raw, false, issues, configExists ? configPath : null, schemaPath);
        }

        var effective = PluginConfigSchema.ApplyDefaults(schema, raw);
        issues.AddRange(PluginConfigSchema.Validate(schema, effective));

        return new NpsConfigLoadResult(raw, effective, issues.Count == 0, issues, configExists ? configPath : null, schemaPath);
    }
}
