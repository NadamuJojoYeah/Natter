using System.Text.Json;

namespace Natter.Infrastructure.Plugins;

/// <summary>Optional `plugin.json` manifest that lives next to the plugin assembly.</summary>
internal sealed class LegacyPluginManifest
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public int? SchemaVersion { get; set; }

    public string? Id { get; set; }

    public string? Name { get; set; }

    public string? Version { get; set; }

    public string? Description { get; set; }

    public string? Author { get; set; }

    public string? EntryAssembly { get; set; }

    public bool? Enabled { get; set; } = true;

    public List<string>? Capabilities { get; set; }

    public static LegacyPluginManifest? Read(string directory)
    {
        var path = Path.Combine(directory, "plugin.json");
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<LegacyPluginManifest>(json, SerializerOptions)
                   ?? new LegacyPluginManifest();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            throw new InvalidOperationException($"Cannot read plugin manifest '{path}': {ex.Message}", ex);
        }
    }
}
