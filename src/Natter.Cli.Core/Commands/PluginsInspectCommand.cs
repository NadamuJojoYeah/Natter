using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
using Natter.Cli.Abstractions;
using Natter.Cli.Core.Rendering;

namespace Natter.Cli.Core.Commands;

/// <summary>Inspects a plugin directory (manifest, entry assembly, capabilities) without loading it.</summary>
public sealed class PluginsInspectCommand : ICliCommandHandler
{
    private readonly IPluginProbe _probe;

    public PluginsInspectCommand(IPluginProbe probe) => _probe = probe;

    public ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var directory = invocation.GetPositional(0, ".").Trim();
        var result = _probe.Probe(directory);

        if (invocation.HasFlag("json"))
        {
            invocation.Console.Out.WriteLine(JsonOutput.Serialize(result));
        }
        else
        {
            var output = invocation.Console.Out;
            output.WriteLine($"plugin probe: {result.Directory}");
            output.WriteLine(new string('-', 52));
            output.WriteLine($"valid          : {result.IsValid}");
            output.WriteLine($"id             : {result.Id ?? "-"}");
            output.WriteLine($"name           : {result.Name ?? "-"}");
            output.WriteLine($"version        : {result.Version ?? "-"}");
            output.WriteLine($"schema version : {result.SchemaVersion?.ToString() ?? "legacy"}");
            output.WriteLine($"kind           : {result.Kind ?? "-"}");
            output.WriteLine($"host API       : {result.HostApi ?? "-"}");
            output.WriteLine($"entry assembly : {result.EntryAssembly ?? "-"}");
            output.WriteLine($"entry type     : {result.EntryType ?? "-"}");
            output.WriteLine($"assemblies     : {(result.Assemblies is null ? "-" : string.Join(", ", result.Assemblies))}");
            output.WriteLine($"dependencies   : {(result.Dependencies is null ? "-" : string.Join(", ", result.Dependencies))}");
            output.WriteLine($"permissions    : {(result.Permissions is null ? "-" : string.Join(", ", result.Permissions))}");
            output.WriteLine($"contributions  : {(result.Contributions is null ? "-" : string.Join(", ", result.Contributions.Select(contribution => $"{contribution.Type}/{contribution.Id}")))}");
            output.WriteLine($"capabilities   : {(result.Capabilities is null ? "-" : string.Join(", ", result.Capabilities))}");
            output.WriteLine($"warnings       : {(result.Warnings is null ? "-" : string.Join("; ", result.Warnings))}");

            if (!string.IsNullOrWhiteSpace(result.Error))
                output.WriteLine($"error          : {result.Error}");
        }

        return ValueTask.FromResult(result.IsValid ? CliExitCodes.Success : CliExitCodes.Failure);
    }
}
