using System.Reflection;
using System.Runtime.Loader;

namespace Natter.Infrastructure.Plugins;

/// <summary>
/// Collectible load context for a single plugin directory. Framework assemblies are
/// resolved from the default context so type identity (ports, contracts, Rx types)
/// is shared between the host and every plugin.
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly string[] SharedPrefixes =
    [
        "Natter.",
        "Microsoft.Extensions.",
        "System.Reactive",
        "System.",
        "netstandard",
        "Microsoft.CSharp",
        "mscorlib",
    ];

    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginAssemblyPath, string pluginId)
        : base($"Natter.Plugin.{pluginId}", isCollectible: true)
    {
        PluginAssemblyPath = pluginAssemblyPath;
        _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
    }

    public string PluginAssemblyPath { get; }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (string.IsNullOrEmpty(name))
            return null;

        if (IsShared(name))
            return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
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

    private static bool IsShared(string name)
        => SharedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));
}
