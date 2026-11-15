using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
using Natter.Cli.Abstractions;
using Natter.Cli.Core.Rendering;
using Natter.Plugin.Abstractions;

namespace Natter.Cli.Core.Commands;

/// <summary>Shows the NPS dependency graph and ordering edges.</summary>
public sealed class PluginsGraphCommand : ICliCommandHandler
{
    private readonly IServiceProvider _services;

    public PluginsGraphCommand(IServiceProvider services) => _services = services;

    public ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var runtime = _services.GetService<IPluginRuntimeDiagnostics>();
        if (runtime is null)
        {
            invocation.Console.Error.WriteLine("The NPS runtime is not composed in this host.");
            return ValueTask.FromResult(CliExitCodes.Failure);
        }

        if (invocation.HasFlag("json"))
        {
            invocation.Console.Out.WriteLine(JsonOutput.Serialize(new
            {
                nodes = runtime.Graph,
                edges = runtime.GraphEdges,
            }));
            return ValueTask.FromResult(CliExitCodes.Success);
        }

        ConsoleTable.Write(
            invocation.Console.Out,
            ["ID", "VERSION", "KIND", "STATE", "DEPENDS ON", "BLOCKED BY"],
            runtime.Graph.Select(node => (IReadOnlyList<string>)
            [
                node.Id,
                node.Version,
                node.Kind,
                node.State.ToString(),
                node.DependsOn.Count == 0 ? "-" : string.Join(", ", node.DependsOn),
                node.BlockedBy.Count == 0 ? "-" : string.Join(", ", node.BlockedBy),
            ]).ToList());

        if (runtime.GraphEdges.Count == 0)
            return ValueTask.FromResult(CliExitCodes.Success);

        invocation.Console.Out.WriteLine();
        ConsoleTable.Write(
            invocation.Console.Out,
            ["FROM", "TO", "KIND"],
            runtime.GraphEdges.Select(edge => (IReadOnlyList<string>)
            [
                edge.From,
                edge.To,
                edge.Kind,
            ]).ToList());

        return ValueTask.FromResult(CliExitCodes.Success);
    }
}

/// <summary>Detailed diagnosis for one plugin.</summary>
public sealed class PluginsDoctorCommand : ICliCommandHandler
{
    private readonly IServiceProvider _services;

    public PluginsDoctorCommand(IServiceProvider services) => _services = services;

    public ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var pluginId = invocation.GetPositional(0);
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            invocation.Console.Error.WriteLine("usage: natter plugins doctor <plugin-id> [--json]");
            return ValueTask.FromResult(CliExitCodes.Usage);
        }

        var runtime = _services.GetService<IPluginRuntimeDiagnostics>();
        if (runtime is null)
        {
            invocation.Console.Error.WriteLine("The NPS runtime is not composed in this host.");
            return ValueTask.FromResult(CliExitCodes.Failure);
        }

        PluginDoctorReport report;
        try
        {
            report = runtime.GetDoctor(pluginId);
        }
        catch (KeyNotFoundException ex)
        {
            invocation.Console.Error.WriteLine(ex.Message);
            return ValueTask.FromResult(CliExitCodes.Failure);
        }

        if (invocation.HasFlag("json"))
        {
            invocation.Console.Out.WriteLine(JsonOutput.Serialize(report));
            return ValueTask.FromResult(CliExitCodes.Success);
        }

        var output = invocation.Console.Out;
        output.WriteLine($"plugin doctor: {report.PluginId}");
        output.WriteLine(new string('-', 52));
        output.WriteLine($"state          : {report.State}");
        output.WriteLine($"id / version   : {report.Manifest?.Id ?? report.PluginId} / {report.Manifest?.Version ?? "-"}");
        output.WriteLine($"kind           : {report.Manifest?.Kind.ToString().ToLowerInvariant() ?? "-"}");
        output.WriteLine($"host API       : {report.HostApi ?? "-"} ({(report.HostApiCompatible ? "compatible" : "incompatible")})");
        output.WriteLine($"config         : {(report.ConfigValid ? "valid" : "invalid")} ({report.ConfigPath ?? "none"})");
        output.WriteLine($"dependencies   : {(report.Dependencies.Count == 0 ? "-" : string.Join(", ", report.Dependencies))}");
        output.WriteLine($"missing secrets: {(report.MissingSecrets.Count == 0 ? "-" : string.Join(", ", report.MissingSecrets))}");

        if (report.Warnings.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("warnings:");
            foreach (var warning in report.Warnings)
                output.WriteLine($"  {warning}");
        }

        if (report.ConfigIssues.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("config issues:");
            foreach (var issue in report.ConfigIssues)
                output.WriteLine($"  {issue.Pointer}: {issue.Message}");
        }

        if (report.Error is not null)
        {
            output.WriteLine();
            output.WriteLine($"error          : {report.Error.Phase}/{report.Error.Code}");
            output.WriteLine($"message        : {report.Error.Message}");
        }

        return ValueTask.FromResult(CliExitCodes.Success);
    }
}

/// <summary>Lists declared/active NPS contributions.</summary>
public sealed class PluginsContributionsCommand : ICliCommandHandler
{
    private readonly IServiceProvider _services;

    public PluginsContributionsCommand(IServiceProvider services) => _services = services;

    public ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var runtime = _services.GetService<IPluginRuntimeDiagnostics>();
        if (runtime is null)
        {
            invocation.Console.Error.WriteLine("The NPS runtime is not composed in this host.");
            return ValueTask.FromResult(CliExitCodes.Failure);
        }

        var components = _services.GetService<Natter.Application.Chat.IContextComponentCatalog>()?.Components
                         ?? [];
        var hostRows = components.Select(component => new
            {
                owner = component.Owner,
                source = component.Source,
                type = component.Type,
                id = component.Id,
                pluginId = component.Owner,
                priority = 0,
                status = "active",
            })
            .ToArray();
        var pluginRows = runtime.Contributions.Select(contribution => new
            {
                owner = contribution.PluginId,
                source = "plugin",
                type = contribution.Type,
                id = contribution.Id,
                pluginId = contribution.PluginId,
                priority = contribution.Priority,
                status = contribution.Status,
            })
            .ToArray();
        var rows = pluginRows.Concat(hostRows).ToArray();

        if (invocation.HasFlag("json"))
        {
            invocation.Console.Out.WriteLine(JsonOutput.Serialize(rows));
            return ValueTask.FromResult(CliExitCodes.Success);
        }

        ConsoleTable.Write(
            invocation.Console.Out,
            ["TYPE", "ID", "OWNER", "SOURCE", "PRIORITY", "STATUS"],
            rows.Select(contribution => (IReadOnlyList<string>)
            [
                contribution.type,
                contribution.id,
                contribution.owner,
                contribution.source,
                contribution.priority.ToString(),
                contribution.status,
            ]).ToList());

        return ValueTask.FromResult(CliExitCodes.Success);
    }
}

/// <summary>Lists requested/granted/denied plugin permissions.</summary>
public sealed class PluginsPermissionsCommand : ICliCommandHandler
{
    private readonly IServiceProvider _services;

    public PluginsPermissionsCommand(IServiceProvider services) => _services = services;

    public ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var runtime = _services.GetService<IPluginRuntimeDiagnostics>();
        if (runtime is null)
        {
            invocation.Console.Error.WriteLine("The NPS runtime is not composed in this host.");
            return ValueTask.FromResult(CliExitCodes.Failure);
        }

        if (invocation.HasFlag("json"))
        {
            invocation.Console.Out.WriteLine(JsonOutput.Serialize(runtime.Permissions));
            return ValueTask.FromResult(CliExitCodes.Success);
        }

        ConsoleTable.Write(
            invocation.Console.Out,
            ["PLUGIN", "REQUESTED", "GRANTED", "DENIED"],
            runtime.Permissions.Select(permission => (IReadOnlyList<string>)
            [
                permission.PluginId,
                permission.Requested.Count == 0 ? "-" : string.Join(", ", permission.Requested),
                permission.Granted.Count == 0 ? "-" : string.Join(", ", permission.Granted),
                permission.Denied.Count == 0 ? "-" : string.Join(", ", permission.Denied),
            ]).ToList());

        return ValueTask.FromResult(CliExitCodes.Success);
    }
}

/// <summary>Validates one plugin's config.json against its declared schema.</summary>
public sealed class PluginsConfigValidateCommand : ICliCommandHandler
{
    private readonly IServiceProvider _services;

    public PluginsConfigValidateCommand(IServiceProvider services) => _services = services;

    public ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var pluginId = invocation.GetPositional(0);
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            invocation.Console.Error.WriteLine("usage: natter plugins config validate <plugin-id> [--json]");
            return ValueTask.FromResult(CliExitCodes.Usage);
        }

        var runtime = _services.GetService<IPluginRuntimeDiagnostics>();
        if (runtime is null)
        {
            invocation.Console.Error.WriteLine("The NPS runtime is not composed in this host.");
            return ValueTask.FromResult(CliExitCodes.Failure);
        }

        var result = runtime.ValidateConfig(pluginId);

        if (invocation.HasFlag("json"))
        {
            invocation.Console.Out.WriteLine(JsonOutput.Serialize(result));
        }
        else if (result.Valid)
        {
            invocation.Console.Out.WriteLine($"config valid: {result.PluginId}");
        }
        else
        {
            invocation.Console.Out.WriteLine($"config invalid: {result.PluginId}");
            foreach (var issue in result.Issues)
                invocation.Console.Out.WriteLine($"  {issue.Pointer}: {issue.Message}");
        }

        return ValueTask.FromResult(result.Valid ? CliExitCodes.Success : CliExitCodes.Failure);
    }
}
