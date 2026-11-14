using Microsoft.Extensions.DependencyInjection;
using Natter.Cli.Abstractions;

namespace Natter.Sample.Plugin.Metrics;

/// <summary>Contributes the `metrics` command.</summary>
public sealed class MetricsCommandContributor : ICliCommandContributor
{
    public IEnumerable<CliCommandDescriptor> GetCommands()
    {
        yield return new CliCommandDescriptor
        {
            Name = "metrics",
            Description = "Show domain event counters collected by the metrics plugin.",
            Options = [new CliOptionDescriptor("json", "Emit machine-readable JSON.")],
            Origin = "plugin:metrics",
            HandlerFactory = services => services.GetRequiredService<MetricsCommandHandler>(),
        };
    }
}
