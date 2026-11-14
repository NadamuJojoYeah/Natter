using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
using Natter.Cli.Abstractions;

namespace Natter.Sample.Plugin.Metrics;

/// <summary>
/// Sample plugin that only consumes the Rx event stream. It shows that several plugins
/// can independently observe the same hot stream without knowing about each other.
/// </summary>
public sealed class MetricsPlugin : IPluginModule
{
    public PluginMetadata Metadata { get; } = new(
        Id: "metrics",
        Name: "Event Metrics Plugin",
        Version: "0.1.0",
        Description: "Counts domain events and exposes them via the metrics command.",
        Author: "Natter samples",
        Capabilities: ["events:subscriber", "cli:metrics"]);

    public void Configure(IPluginRegistrationContext context)
    {
        context.Services.AddSingleton<EventMetricsCollector>();
        context.Services.AddSingleton<IPluginLifecycle, MetricsLifecycle>();
        context.Services.AddSingleton<ICliCommandContributor, MetricsCommandContributor>();
        context.Services.AddTransient<MetricsCommandHandler>();
        context.AddCapability("lifecycle");
        context.AddCapability("metrics:counters");
    }
}
