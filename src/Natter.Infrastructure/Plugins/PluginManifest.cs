using System.Text.Json;

namespace Natter.Infrastructure.Plugins;

/// <summary>Optional `plugin.json` manifest that lives next to the plugin assembly.</summary>
internal sealed class PluginManifest
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string? Id { get; set; }

    public string? Name { get; set; }

    public string? Version { get; set; }

    public string? Description { get; set; }

    public string? Author { get; set; }

    public string? EntryAssembly { get; set; }

    public bool? Enabled { get; set; } = true;

    public List<string>? Capabilities { get; set; }

    public static PluginManifest? Read(string directory)
    {
        var path = Path.Combine(directory, "plugin.json");
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<PluginManifest>(json, SerializerOptions)
                   ?? new PluginManifest();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            throw new InvalidOperationException($"Cannot read plugin manifest '{path}': {ex.Message}", ex);
        }
    }
}
