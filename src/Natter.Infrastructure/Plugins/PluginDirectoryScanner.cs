namespace Natter.Infrastructure.Plugins;

/// <summary>Discovers plugin directories below a plugin root.</summary>
internal static class PluginDirectoryScanner
{
    public static IReadOnlyList<string> Discover(string root)
    {
        if (File.Exists(Path.Combine(root, "plugin.json")))
            return [root];

        var subdirectories = Directory.GetDirectories(root)
            .Where(directory =>
                File.Exists(Path.Combine(directory, "plugin.json"))
                || Directory.GetFiles(directory, "*.dll").Length > 0)
            .OrderBy(directory => directory, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (subdirectories.Count > 0)
            return subdirectories;

        return Directory.GetFiles(root, "*.dll").Length > 0 ? [root] : [];
    }
}
