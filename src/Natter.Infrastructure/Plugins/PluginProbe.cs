using Natter.Abstractions.Plugins;
using Natter.Plugin.Abstractions;

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
            var nps = PluginManifestReader.Read(fullPath);
            if (nps.IsNps)
                return ProbeNps(fullPath, nps);

            return ProbeLegacy(fullPath);
        }
        catch (Exception ex)
        {
            return new PluginProbeResult(fullPath, false, ex.Message);
        }
    }

    private static PluginProbeResult ProbeNps(string directory, PluginManifestReadResult read)
    {
        if (!read.IsValid || read.Manifest is null)
        {
            var error = string.Join(
                "; ",
                read.Errors.Select(issue => $"{issue.Path}: {issue.Message}"));

            return new PluginProbeResult(
                Directory: directory,
                IsValid: false,
                Error: string.IsNullOrWhiteSpace(error) ? "Manifest is invalid." : error,
                Id: read.Manifest?.Id,
                Name: read.Manifest?.Name,
                Version: read.Manifest?.Version,
                EntryAssembly: read.Manifest?.Entry?.Assembly,
                EntryType: read.Manifest?.Entry?.Type,
                SchemaVersion: read.Manifest?.SchemaVersion,
                Kind: read.Manifest?.Kind.ToString().ToLowerInvariant(),
                HostApi: read.Manifest?.HostApi,
                Dependencies: (read.Manifest?.DependsOn ?? [])
                    .Select(dependency => dependency.Id)
                    .ToArray(),
                Permissions: read.Manifest?.Permissions?.ToArray(),
                Contributions: (read.Manifest?.Contributions ?? [])
                    .Select(contribution => new PluginContributionDescriptor(
                        contribution.Type,
                        contribution.Id,
                        contribution.Priority))
                    .ToArray(),
                Warnings: read.Warnings.Select(issue => $"{issue.Path}: {issue.Message}").ToArray());
        }

        var manifest = read.Manifest!;
        var entry = manifest.Entry?.Assembly;
        var valid = !string.IsNullOrWhiteSpace(entry)
                    && File.Exists(Path.Combine(directory, entry))
                    && !string.IsNullOrWhiteSpace(manifest.Entry?.Type);

        return new PluginProbeResult(
            Directory: directory,
            IsValid: valid,
            Error: valid ? null : "Entry assembly or entry type could not be resolved.",
            Id: manifest.Id,
            Name: manifest.Name,
            Version: manifest.Version,
            EntryAssembly: entry,
            EntryType: manifest.Entry?.Type,
            SchemaVersion: manifest.SchemaVersion,
            Kind: manifest.Kind.ToString().ToLowerInvariant(),
            HostApi: manifest.HostApi,
            Dependencies: (manifest.DependsOn ?? []).Select(dependency => dependency.Id).ToArray(),
            Permissions: manifest.Permissions?.ToArray(),
            Contributions: (manifest.Contributions ?? [])
                .Select(contribution => new PluginContributionDescriptor(
                    contribution.Type,
                    contribution.Id,
                    contribution.Priority))
                .ToArray(),
            Warnings: read.Warnings.Select(issue => $"{issue.Path}: {issue.Message}").ToArray());
    }

    private static PluginProbeResult ProbeLegacy(string directory)
    {
        var manifest = LegacyPluginManifest.Read(directory);
        var assemblies = Directory.GetFiles(directory, "*.dll")
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var entry = manifest?.EntryAssembly ?? PluginDiscovery.FindEntryAssembly(directory, assemblies);
        var valid = !string.IsNullOrEmpty(entry) && File.Exists(Path.Combine(directory, entry));

        return new PluginProbeResult(
            Directory: directory,
            IsValid: valid,
            Error: valid ? null : "No plugin entry assembly could be resolved.",
            Id: manifest?.Id,
            Name: manifest?.Name,
            Version: manifest?.Version,
            EntryAssembly: entry,
            Assemblies: assemblies,
            Capabilities: manifest?.Capabilities);
    }
}
