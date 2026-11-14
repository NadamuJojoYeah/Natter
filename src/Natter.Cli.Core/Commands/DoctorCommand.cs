using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Composition;
using Natter.Abstractions.Events;
using Natter.Abstractions.Plugins;
using Natter.Abstractions.Ports;
using Natter.Chat;
using Natter.Cli.Abstractions;
using Natter.Cli.Core.Rendering;

namespace Natter.Cli.Core.Commands;

/// <summary>Shows the composed host: runtime, plugins, adapters, event bus and command counts.</summary>
public sealed class DoctorCommand : ICliCommandHandler
{
    private readonly IServiceProvider _services;

    public DoctorCommand(IServiceProvider services) => _services = services;

    public ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var plugins = _services.GetRequiredService<IPluginCatalog>();
        var adapters = _services.GetRequiredService<IPortAdapterCatalog>();
        var commandRegistry = _services.GetRequiredService<CliCommandRegistry>();
        var composition = _services.GetRequiredService<CompositionOptions>();
        var bus = _services.GetRequiredService<IDomainEventBusDiagnostics>();
        var journal = _services.GetService<IEventJournal>();
        var chatHub = _services.GetService<IChatEventDiagnostics>();
        var chatUpstreams = _services.GetService<IChatUpstreamRegistry>();

        var loaded = plugins.Plugins.Count(plugin => plugin.State == PluginState.Loaded);
        var failed = plugins.Plugins.Count(plugin => plugin.State == PluginState.Failed);

        if (invocation.HasFlag("json"))
        {
            var payload = new
            {
                framework = NatterInfo.Version,
                runtime = NatterInfo.Runtime,
                rid = NatterInfo.Rid,
                os = NatterInfo.Os,
                architecture = NatterInfo.Architecture,
                environment = composition.EnvironmentName,
                pluginLoading = composition.EnablePluginLoading,
                plugins = new { total = plugins.Plugins.Count, loaded, failed },
                commands = commandRegistry.CommandCount,
                portAdapters = adapters.Adapters.Count,
                eventBus = new { published = bus.PublishedCount, subscribers = bus.SubscriberCount },
                journal = new { enabled = journal is not null, count = journal?.Count ?? 0 },
                chat = new
                {
                    upstreams = chatUpstreams?.Upstreams.Count ?? 0,
                    eventsPublished = chatHub?.PublishedCount ?? 0,
                    subscribers = chatHub?.SubscriberCount ?? 0,
                },
                pluginDetails = plugins.Plugins,
            };

            invocation.Console.Out.WriteLine(JsonOutput.Serialize(payload));
            return ValueTask.FromResult(CliExitCodes.Success);
        }

        var output = invocation.Console.Out;
        output.WriteLine($"Natter doctor — framework {NatterInfo.Version}");
        output.WriteLine(new string('-', 52));
        output.WriteLine($"runtime        : {NatterInfo.Runtime}");
        output.WriteLine($"rid            : {NatterInfo.Rid}");
        output.WriteLine($"os             : {NatterInfo.Os}");
        output.WriteLine($"architecture   : {NatterInfo.Architecture}");
        output.WriteLine($"environment    : {composition.EnvironmentName}");
        output.WriteLine($"plugins        : {plugins.Plugins.Count} total, {loaded} loaded, {failed} failed");
        output.WriteLine($"commands       : {commandRegistry.CommandCount} registered");
        output.WriteLine($"port adapters  : {adapters.Adapters.Count} binding(s)");
        output.WriteLine($"event bus      : {bus.PublishedCount} published, {bus.SubscriberCount} subscriber(s)");
        output.WriteLine($"event journal  : {(journal is null ? "disabled" : $"{journal.Count} event(s) buffered")}");
        if (chatHub is not null || chatUpstreams is not null)
        {
            output.WriteLine(
                $"NLMP chat      : {chatUpstreams?.Upstreams.Count ?? 0} upstream(s), "
                + $"{chatHub?.PublishedCount ?? 0} event(s) published, "
                + $"{chatHub?.SubscriberCount ?? 0} subscriber(s)");
        }
        output.WriteLine();

        ConsoleTable.Write(
            output,
            ["ID", "NAME", "VERSION", "STATE", "ORIGIN", "CAPABILITIES"],
            plugins.Plugins
                .OrderBy(plugin => plugin.Id, StringComparer.OrdinalIgnoreCase)
                .Select(plugin => (IReadOnlyList<string>)
                [
                    plugin.Id,
                    plugin.Name,
                    plugin.Version,
                    plugin.State.ToString(),
                    plugin.IsDynamic ? "embedded" : "file",
                    plugin.Capabilities is null ? "-" : string.Join(", ", plugin.Capabilities),
                ])
                .ToList());

        var failures = plugins.Plugins
            .Where(plugin => !string.IsNullOrWhiteSpace(plugin.Error))
            .ToList();

        if (failures.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Plugin errors:");
            foreach (var failure in failures)
                output.WriteLine($"  [{failure.Id}] {failure.Error}");
        }

        return ValueTask.FromResult(CliExitCodes.Success);
    }
}
