using System.Reflection;
using System.Runtime.Loader;
using Natter.Infrastructure.Plugins;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// Guards the plugin load-context policy that live testing exposed as broken: only contract
/// assemblies are shared with the host, plugin adapters and third-party libraries stay private,
/// and a plain folder without a deps.json still resolves its own dependencies.
/// </summary>
public sealed class PluginLoadContextTests
{
    [Fact]
    public void Private_adapter_loads_from_the_plugin_folder_without_a_deps_json()
    {
        using var temp = CopyTestBinaries();

        var entry = Path.Combine(temp.Path, "Natter.Sample.Plugin.Discord.dll");
        Assert.False(File.Exists(entry + ".deps.json"), "The fixture must not contain a deps.json.");

        var context = new PluginLoadContext(entry, "discord-folder");
        var assembly = context.LoadFromAssemblyPath(entry);
        var types = ReadTypes(assembly);

        Assert.Contains(types, type => type.FullName == "Natter.Sample.Plugin.Discord.DiscordPlugin");

        // Private adapter + third-party libraries come from the plugin folder (no deps.json).
        var adapter = context.LoadFromAssemblyName(new AssemblyName("Natter.Chat.Discord"));
        var websocket = context.LoadFromAssemblyName(new AssemblyName("Discord.Net.WebSocket"));
        Assert.DoesNotContain(context.Assemblies, item => item == typeof(object).Assembly);
        Assert.Contains(context.Assemblies, item => item == adapter);
        Assert.Contains(context.Assemblies, item => item == websocket);

        // Contract assemblies must stay in the default context: one type identity per host.
        foreach (var sharedName in new[]
                 {
                     "Natter.Chat",
                     "Natter.Plugin.Abstractions",
                     "Natter.Abstractions",
                     "Microsoft.Extensions.DependencyInjection.Abstractions",
                 })
        {
            var shared = context.LoadFromAssemblyName(new AssemblyName(sharedName));
            Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(shared));
            Assert.DoesNotContain(context.Assemblies, item => item == shared);
        }
    }

    [Fact]
    public void Microsoft_extensions_ai_is_loaded_privately_because_the_host_does_not_reference_it()
    {
        using var temp = CopyTestBinaries();

        var entry = Path.Combine(temp.Path, "Natter.Feature.Ai.DeepSeek.dll");
        var context = new PluginLoadContext(entry, "deepseek-folder");
        var assembly = context.LoadFromAssemblyPath(entry);
        var types = ReadTypes(assembly);

        Assert.Contains(types, type => type.FullName == typeof(Natter.Feature.Ai.DeepSeek.DeepSeekChatClient).FullName);

        var ai = context.LoadFromAssemblyName(new AssemblyName("Microsoft.Extensions.AI.Abstractions"));
        Assert.Contains(context.Assemblies, item => item == ai);

        var di = context.LoadFromAssemblyName(new AssemblyName("Microsoft.Extensions.DependencyInjection.Abstractions"));
        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(di));
        Assert.DoesNotContain(context.Assemblies, item => item == di);
    }

    [Fact]
    public void Shared_contract_types_resolve_to_the_host_context()
    {
        using var temp = CopyTestBinaries();

        var entry = Path.Combine(temp.Path, "Natter.Sample.Plugin.Discord.dll");
        var context = new PluginLoadContext(entry, "identity-folder");
        var assembly = context.LoadFromAssemblyPath(entry);

        var pluginType = ReadTypes(assembly).Single(type => type.FullName == "Natter.Sample.Plugin.Discord.DiscordPlugin");
        var contract = pluginType.GetInterfaces()
            .Single(item => item.FullName == "Natter.Plugin.Abstractions.INatterPlugin");

        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(contract.Assembly));
        Assert.DoesNotContain(context.Assemblies, item => item == contract.Assembly);
    }

    private static IEnumerable<Type> ReadTypes(Assembly assembly)
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

    private static TempDirectory CopyTestBinaries()
    {
        var temp = new TempDirectory();
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory, "*.dll"))
            File.Copy(file, Path.Combine(temp.Path, Path.GetFileName(file)), overwrite: true);

        return temp;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "natter-load-context-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
