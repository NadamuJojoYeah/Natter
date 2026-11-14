using Natter.Abstractions.Plugins;

namespace Natter.Infrastructure.Plugins;

/// <summary>
/// Discovers plugin directories, loads each one into its own collectible
/// <see cref="PluginLoadContext"/> and instantiates the <see cref="IPluginModule"/>.
/// Every failure is captured as a descriptor instead of aborting host startup.
/// </summary>
public sealed class PluginLoader
{
    public PluginLoadResult LoadDirectory(string directory)
    {
        var plugins = new List<LoadedPlugin>();
        var failures = new List<PluginDescriptor>();

        if (string.IsNullOrWhiteSpace(directory))
        {
            failures.Add(CreateFailure(directory ?? string.Empty, "Plugin directory must not be empty."));
            return new PluginLoadResult(plugins, failures);
        }

        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root))
        {
            failures.Add(CreateFailure(root, "Plugin directory does not exist."));
            return new PluginLoadResult(plugins, failures);
        }

        foreach (var pluginDirectory in DiscoverPluginDirectories(root))
        {
            var (plugin, failure) = LoadPlugin(pluginDirectory);

            if (plugin is not null)
                plugins.Add(plugin);

            if (failure is not null)
                failures.Add(failure);
        }

        return new PluginLoadResult(plugins, failures);
    }

    private static IReadOnlyList<string> DiscoverPluginDirectories(string root)
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

    private static (LoadedPlugin? Plugin, PluginDescriptor? Failure) LoadPlugin(string directory)
    {
        PluginManifest? manifest;
        try
        {
            manifest = PluginManifest.Read(directory);
        }
        catch (Exception ex)
        {
            return (null, CreateFailure(directory, ex.Message));
        }

        if (manifest?.Enabled == false)
        {
            return (null, CreateFailure(
                directory,
                "Disabled by plugin.json.",
                PluginState.Skipped,
                id: manifest.Id));
        }

        var assemblies = Directory.GetFiles(directory, "*.dll")
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToArray();

        var entryAssemblyName = manifest?.EntryAssembly
                                ?? PluginDiscovery.FindEntryAssembly(directory, assemblies);

        if (string.IsNullOrEmpty(entryAssemblyName))
            return (null, CreateFailure(directory, "No entry assembly could be resolved."));

        var entryPath = Path.Combine(directory, entryAssemblyName);
        if (!File.Exists(entryPath))
            return (null, CreateFailure(directory, $"Entry assembly '{entryAssemblyName}' was not found."));

        var pluginId = manifest?.Id ?? Path.GetFileNameWithoutExtension(entryAssemblyName);
        PluginLoadContext? loadContext = null;

        try
        {
            loadContext = new PluginLoadContext(entryPath, pluginId);
            var assembly = loadContext.LoadFromAssemblyPath(entryPath);

            var moduleType = PluginLoadContext.GetLoadableTypes(assembly)
                .Where(type =>
                    typeof(IPluginModule).IsAssignableFrom(type)
                    && type is { IsAbstract: false, IsInterface: false }
                    && type.GetConstructor(Type.EmptyTypes) is not null)
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .FirstOrDefault();

            if (moduleType is null)
            {
                loadContext.Unload();
                return (null, CreateFailure(
                    directory,
                    "No public IPluginModule implementation with a parameterless constructor was found.",
                    assemblyPath: entryPath,
                    id: pluginId));
            }

            var module = (IPluginModule)Activator.CreateInstance(moduleType)!;
            var metadata = module.Metadata;

            var descriptor = new PluginDescriptor(
                Id: manifest?.Id ?? metadata.Id,
                Name: manifest?.Name ?? metadata.Name,
                Version: manifest?.Version ?? metadata.Version,
                AssemblyPath: entryPath,
                Directory: directory,
                State: PluginState.Loaded,
                Capabilities: MergeCapabilities(manifest?.Capabilities, metadata.Capabilities),
                Description: manifest?.Description ?? metadata.Description);

            return (new LoadedPlugin(descriptor, module, loadContext), null);
        }
        catch (Exception ex)
        {
            loadContext?.Unload();
            return (null, CreateFailure(
                directory,
                $"{ex.GetType().Name}: {ex.Message}",
                assemblyPath: entryPath,
                id: pluginId));
        }
    }

    private static PluginDescriptor CreateFailure(
        string directory,
        string error,
        PluginState state = PluginState.Failed,
        string? assemblyPath = null,
        string? id = null)
    {
        var name = string.IsNullOrWhiteSpace(directory)
            ? "<unknown>"
            : Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));

        return new PluginDescriptor(
            Id: id ?? name,
            Name: name,
            Version: "0.0.0",
            AssemblyPath: assemblyPath ?? string.Empty,
            Directory: directory,
            State: state,
            Error: error);
    }

    private static IReadOnlyList<string>? MergeCapabilities(
        IReadOnlyList<string>? fromManifest,
        IReadOnlyList<string>? fromMetadata)
    {
        if (fromManifest is null && fromMetadata is null)
            return null;

        return (fromManifest ?? [])
            .Concat(fromMetadata ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
