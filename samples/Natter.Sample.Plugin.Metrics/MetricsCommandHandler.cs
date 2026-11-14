using System.Text.Json;
using Natter.Cli.Abstractions;

namespace Natter.Sample.Plugin.Metrics;

/// <summary>`metrics` command handler.</summary>
public sealed class MetricsCommandHandler : ICliCommandHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly EventMetricsCollector _collector;

    public MetricsCommandHandler(EventMetricsCollector collector) => _collector = collector;

    public ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var snapshot = _collector.Snapshot();

        if (invocation.HasFlag("json"))
        {
            invocation.Console.Out.WriteLine(JsonSerializer.Serialize(snapshot, JsonOptions));
            return ValueTask.FromResult(CliExitCodes.Success);
        }

        invocation.Console.Out.WriteLine($"Domain events observed by the metrics plugin: {snapshot.Total}");

        if (snapshot.Counts.Count == 0)
        {
            invocation.Console.Out.WriteLine("No events yet. Run 'natter demo' first.");
            return ValueTask.FromResult(CliExitCodes.Success);
        }

        foreach (var (eventName, count) in snapshot.Counts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            invocation.Console.Out.WriteLine($"  {eventName,-24} {count}");

        if (snapshot.LastEventName is not null)
        {
            invocation.Console.Out.WriteLine(
                $"last event: {snapshot.LastEventName} at {snapshot.LastObservedAt:HH:mm:ss.fff} UTC");
        }

        return ValueTask.FromResult(CliExitCodes.Success);
    }
}
