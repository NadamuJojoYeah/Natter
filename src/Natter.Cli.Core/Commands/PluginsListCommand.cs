using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
using Natter.Cli.Abstractions;
using Natter.Cli.Core.Rendering;

namespace Natter.Cli.Core.Commands;

/// <summary>Lists file-based and embedded plugins with their state.</summary>
public sealed class PluginsListCommand : ICliCommandHandler
{
    private readonly IPluginCatalog _catalog;

    public PluginsListCommand(IPluginCatalog catalog) => _catalog = catalog;

    public ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var failedOnly = invocation.HasFlag("failed");
        var plugins = _catalog.Plugins
            .Where(plugin => !failedOnly || plugin.State != PluginState.Loaded)
            .OrderBy(plugin => plugin.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (invocation.HasFlag("json"))
        {
            invocation.Console.Out.WriteLine(JsonOutput.Serialize(plugins));
            return ValueTask.FromResult(CliExitCodes.Success);
        }

        ConsoleTable.Write(
            invocation.Console.Out,
            ["ID", "NAME", "VERSION", "STATE", "ORIGIN", "CAPABILITIES"],
            plugins.Select(plugin => (IReadOnlyList<string>)
            [
                plugin.Id,
                plugin.Name,
                plugin.Version,
                plugin.State.ToString(),
                plugin.IsDynamic ? "embedded" : plugin.Directory,
                plugin.Capabilities is null ? "-" : string.Join(", ", plugin.Capabilities),
            ]).ToList());

        foreach (var plugin in plugins.Where(plugin => !string.IsNullOrWhiteSpace(plugin.Error)))
            invocation.Console.Out.WriteLine($"  [{plugin.Id}] {plugin.Error}");

        return ValueTask.FromResult(CliExitCodes.Success);
    }
}
