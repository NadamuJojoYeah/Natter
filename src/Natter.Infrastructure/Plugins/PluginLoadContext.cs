using System.Reflection;
using System.Runtime.Loader;

namespace Natter.Infrastructure.Plugins;

/// <summary>
/// Collectible load context for one plugin directory. Contract assemblies are shared with the
/// default context for type identity; plugin adapters/providers and third-party libraries are
/// loaded from the plugin folder. Microsoft.Extensions packages that the host does not own are
/// private when the plugin ships a local copy, and otherwise resolved from the host if possible.
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly string[] FrameworkPrefixes =
    [
        "System.",
        "netstandard",
        "mscorlib",
        "Microsoft.CSharp",
    ];

    private readonly AssemblyDependencyResolver _resolver;
    private readonly PluginLoadContextOptions _options;
    private readonly List<string> _diagnostics = [];
    private readonly Lock _diagnosticsGate = new();
    private readonly string? _pluginDirectory;

    public PluginLoadContext(
        string pluginAssemblyPath,
        string pluginId,
        PluginLoadContextOptions? options = null)
        : base($"Natter.Plugin.{pluginId}", isCollectible: true)
    {
        PluginAssemblyPath = pluginAssemblyPath;
        _options = options ?? PluginLoadContextOptions.Default;
        _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
        _pluginDirectory = Path.GetDirectoryName(pluginAssemblyPath);
    }

    public string PluginAssemblyPath { get; }

    /// <summary>Structured resolution decisions for diagnostics; safe to include in load errors.</summary>
    public IReadOnlyList<string> Diagnostics
    {
        get
        {
            lock (_diagnosticsGate)
                return _diagnostics.ToArray();
        }
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (string.IsNullOrEmpty(name))
            return null;

        if (_options.ContractAssemblies.Contains(name))
        {
            Record($"{name}: shared host contract");
            return null;
        }

        if (IsFrameworkAssembly(name))
        {
            Record($"{name}: shared framework");
            return null;
        }

        var localPath = FindLocalPath(assemblyName);
        if (localPath is not null)
        {
            Record($"{name}: loaded from plugin directory '{localPath}'");
            return LoadFromAssemblyPath(localPath);
        }

        if (name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)
            && TryResolveDefault(name))
        {
            Record($"{name}: shared host Microsoft.Extensions package (no local copy)");
            return null;
        }

        Record($"{name}: not resolved (no contract, no local copy, no host fallback)");
        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return string.IsNullOrEmpty(path) ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }

    internal static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type is not null).Cast<Type>();
        }
    }

    private string? FindLocalPath(AssemblyName assemblyName)
    {
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path is not null)
            return path;

        if (!_options.EnableDirectoryFallback || string.IsNullOrEmpty(_pluginDirectory))
            return null;

        var candidate = Path.Combine(_pluginDirectory, assemblyName.Name + ".dll");
        if (!File.Exists(candidate))
            return null;

        Record($"{assemblyName.Name}: deps.json miss; directory fallback");
        return candidate;
    }

    private static bool IsFrameworkAssembly(string name)
        => FrameworkPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));

    private static bool TryResolveDefault(string name)
    {
        try
        {
            _ = Default.LoadFromAssemblyName(new AssemblyName(name));
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or FileLoadException)
        {
            return false;
        }
    }

    private void Record(string message)
    {
        lock (_diagnosticsGate)
            _diagnostics.Add(message);
    }
}
