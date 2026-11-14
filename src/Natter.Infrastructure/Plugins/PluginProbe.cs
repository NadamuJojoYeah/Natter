using Natter.Abstractions.Plugins;

namespace Natter.Infrastructure.Plugins;

/// <summary>Inspects a plugin directory without loading it into a collectible context.</summary>
public sealed class PluginProbe : IPluginProbe
{
    public PluginProbeResult Probe(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return new PluginProbeResult(directory ?? string.Empty, false, "Directory must not be empty.");

        var fullPath = Path.GetFullPath(directory);
        if (File.Exists(fullPath))
            fullPath = Path.GetDirectoryName(fullPath)!;

        if (!Directory.Exists(fullPath))
            return new PluginProbeResult(fullPath, false, "Directory does not exist.");

        try
        {
            var manifest = PluginManifest.Read(fullPath);
            var assemblies = Directory.GetFiles(fullPath, "*.dll")
                .Select(Path.GetFileName)
                .OfType<string>()
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var entry = manifest?.EntryAssembly ?? PluginDiscovery.FindEntryAssembly(fullPath, assemblies);
            var valid = !string.IsNullOrEmpty(entry) && File.Exists(Path.Combine(fullPath, entry));

            return new PluginProbeResult(
                Directory: fullPath,
                IsValid: valid,
                Error: valid ? null : "No plugin entry assembly could be resolved.",
                Id: manifest?.Id,
                Name: manifest?.Name,
                Version: manifest?.Version,
                EntryAssembly: entry,
                Assemblies: assemblies,
                Capabilities: manifest?.Capabilities);
        }
        catch (Exception ex)
        {
            return new PluginProbeResult(fullPath, false, ex.Message);
        }
    }
}
