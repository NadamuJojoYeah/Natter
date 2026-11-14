using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Ports;
using Natter.Cli.Abstractions;
using Natter.Cli.Core.Rendering;

namespace Natter.Cli.Core.Commands;

/// <summary>Lists every port/adapter binding, including plugin overrides.</summary>
public sealed class PortsCommand : ICliCommandHandler
{
    private readonly IPortAdapterCatalog _catalog;

    public PortsCommand(IPortAdapterCatalog catalog) => _catalog = catalog;

    public ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var filter = invocation.GetOption("port");

        var adapters = _catalog.Adapters
            .Where(adapter => string.IsNullOrWhiteSpace(filter)
                              || adapter.Port.Contains(filter, StringComparison.OrdinalIgnoreCase)
                              || adapter.Adapter.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(adapter => adapter.Port, StringComparer.OrdinalIgnoreCase)
            .ThenBy(adapter => adapter.Origin, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (invocation.HasFlag("json"))
        {
            invocation.Console.Out.WriteLine(JsonOutput.Serialize(adapters));
            return ValueTask.FromResult(CliExitCodes.Success);
        }

        ConsoleTable.Write(
            invocation.Console.Out,
            ["PORT", "ADAPTER", "LIFETIME", "ORIGIN", "DESCRIPTION"],
            adapters.Select(adapter => (IReadOnlyList<string>)
            [
                adapter.Port,
                adapter.Adapter,
                adapter.Lifetime.ToString(),
                adapter.Origin,
                adapter.Description ?? "-",
            ]).ToList());

        return ValueTask.FromResult(CliExitCodes.Success);
    }
}
