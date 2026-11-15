using Microsoft.Extensions.DependencyInjection;
using Natter.Cli.Abstractions;
using Natter.Plugin.Abstractions;

namespace Natter.Sample.Plugin.Metrics;

/// <summary>NPS v1 sample: contributes a CLI command and starts an event counter on activation.</summary>
public sealed class MetricsNpsPlugin : INatterPlugin
{
    public static PluginManifest Manifest { get; } = PluginManifestReader.ReadEmbedded(
        typeof(MetricsNpsPlugin).Assembly,
        "Natter.Sample.Plugin.Metrics.plugin.json");

    public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        var services = context.Services.Collection;

        services.AddSingleton<EventMetricsCollector>();
        services.AddSingleton<MetricsLifecycle>();
        services.AddTransient<MetricsCommandHandler>();

        context.Contributions.AddCliCommand(new CliCommandDescriptor
        {
            Name = "metrics",
            Description = "Show domain event counters collected by the metrics plugin.",
            Options = [new CliOptionDescriptor("json", "Emit machine-readable JSON.")],
            HandlerFactory = services => services.GetRequiredService<MetricsCommandHandler>(),
        });

        return ValueTask.CompletedTask;
    }

    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        var provider = context.Services.Provider
                       ?? throw new InvalidOperationException("The plugin provider is not available.");
        var lifecycle = provider.GetRequiredService<MetricsLifecycle>();
        await lifecycle.StartAsync(provider, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        if (context.Services.Provider?.GetService<MetricsLifecycle>() is { } lifecycle)
            await lifecycle.StopAsync(context.Services.Provider, cancellationToken).ConfigureAwait(false);
    }
}
