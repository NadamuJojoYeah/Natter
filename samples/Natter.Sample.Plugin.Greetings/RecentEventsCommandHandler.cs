using System.Text.Json;
using Natter.Cli.Abstractions;

namespace Natter.Sample.Plugin.Greetings;

/// <summary>`greetings recent` command handler.</summary>
public sealed class RecentEventsCommandHandler : ICliCommandHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly RecentEventBuffer _buffer;

    public RecentEventsCommandHandler(RecentEventBuffer buffer) => _buffer = buffer;

    public ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var events = _buffer.Snapshot();

        if (invocation.HasFlag("json"))
        {
            var payload = events.Select(envelope => new
            {
                sequence = envelope.Sequence,
                eventName = envelope.EventName,
                eventId = envelope.EventId,
                occurredAt = envelope.OccurredAt,
                origin = envelope.Origin,
                payload = envelope.Event,
            });

            invocation.Console.Out.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
            return ValueTask.FromResult(CliExitCodes.Success);
        }

        if (events.Count == 0)
        {
            invocation.Console.Out.WriteLine("No events observed yet. Run 'natter demo' first.");
            return ValueTask.FromResult(CliExitCodes.Success);
        }

        foreach (var envelope in events)
        {
            invocation.Console.Out.WriteLine(
                $"#{envelope.Sequence,-3} {envelope.OccurredAt:HH:mm:ss.fff} {envelope.EventName} ({envelope.Origin})");
        }

        return ValueTask.FromResult(CliExitCodes.Success);
    }
}
