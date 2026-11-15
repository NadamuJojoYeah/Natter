namespace Natter.Infrastructure.Plugins.Nps;

/// <summary>Lightweight package hygiene checks from NPS §3 (secrets must not ship in plugin dirs).</summary>
internal static class PluginPackageInspector
{
    private static readonly string[] SensitiveExtensions =
    [
        ".pfx", ".p12", ".key", ".keystore", ".jks", ".pem",
    ];

    private static readonly string[] SensitiveNames =
    [
        ".env", "token", "tokens", "credentials", "credentials.json", "secrets.json",
    ];

    public static IReadOnlyList<string> FindSensitiveFiles(string pluginDirectory)
    {
        if (!Directory.Exists(pluginDirectory))
            return [];

        var matches = new List<string>();

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(pluginDirectory, "*", SearchOption.AllDirectories);
        }
        catch
        {
            return matches;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var extension = Path.GetExtension(file);

            if (SensitiveExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
                || SensitiveNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                matches.Add(Path.GetRelativePath(pluginDirectory, file));
            }
        }

        return matches;
    }
}
