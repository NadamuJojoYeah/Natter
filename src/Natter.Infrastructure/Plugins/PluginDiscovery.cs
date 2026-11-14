namespace Natter.Infrastructure.Plugins;

internal static class PluginDiscovery
{
    private static readonly string[] NonEntryPrefixes =
    [
        "Natter.Abstractions",
        "Natter.Cli.Abstractions",
        "Natter.Infrastructure",
        "Natter.Application",
        "Natter.Domain",
        "Microsoft.",
        "System.",
        "netstandard",
    ];

    public static string? FindEntryAssembly(string directory, IReadOnlyList<string> assemblies)
    {
        var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));

        return assemblies.FirstOrDefault(assembly =>
                   string.Equals(Path.GetFileNameWithoutExtension(assembly), directoryName, StringComparison.OrdinalIgnoreCase))
               ?? assemblies.FirstOrDefault(assembly => IsEntryCandidate(assembly));
    }

    private static bool IsEntryCandidate(string assemblyFileName)
    {
        var simpleName = Path.GetFileNameWithoutExtension(assemblyFileName);
        return !NonEntryPrefixes.Any(prefix =>
            simpleName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
