using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Events;
using Natter.Cli.Abstractions;
using Natter.Cli.Core.Rendering;

namespace Natter.Cli.Core.Commands;

/// <summary>Prints buffered domain events from the in-memory journal.</summary>
public sealed class EventsListCommand : ICliCommandHandler
{
    private readonly IEventJournal? _journal;

    public EventsListCommand(IServiceProvider services)
        => _journal = services.GetService<IEventJournal>();

    public ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (_journal is null)
        {
            invocation.Console.Error.WriteLine("The domain event journal is disabled in this host.");
            return ValueTask.FromResult(CliExitCodes.Failure);
        }

        var limit = invocation.GetIntOption("limit", 20);
        var events = _journal.Snapshot(limit);

        if (invocation.HasFlag("json"))
        {
            invocation.Console.Out.WriteLine(JsonOutput.Serialize(events.Select(JsonOutput.Project).ToList()));
            return ValueTask.FromResult(CliExitCodes.Success);
        }

        ConsoleTable.Write(
            invocation.Console.Out,
            ["SEQ", "OCCURRED (UTC)", "EVENT", "PAYLOAD"],
            events.Select(envelope => (IReadOnlyList<string>)
            [
                envelope.Sequence.ToString(),
                envelope.OccurredAt.ToString("HH:mm:ss.fff"),
                envelope.EventName,
                JsonOutput.SerializePayloadCompact(envelope.Event),
            ]).ToList());

        return ValueTask.FromResult(CliExitCodes.Success);
    }
}

/// <summary>Streams live domain events for a fixed number of seconds (Rx subscription demo).</summary>
public sealed class EventsWatchCommand : ICliCommandHandler
{
    private readonly IDomainEventStream _stream;

    public EventsWatchCommand(IDomainEventStream stream) => _stream = stream;

    public async ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var seconds = Math.Clamp(invocation.GetIntOption("seconds", 3), 1, 3600);
        var count = 0;

        invocation.Console.Out.WriteLine($"Watching domain events for {seconds}s (Ctrl+C to stop)...");

        using var subscription = Natter.Infrastructure.Events.RxDomainEventExtensions.SubscribeSafe(
            _stream,
            envelope =>
            {
                Interlocked.Increment(ref count);
                invocation.Console.Out.WriteLine(CliEventPrinter.Format(envelope));
            });

        await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);

        invocation.Console.Out.WriteLine($"Watched {count} event(s).");
        return CliExitCodes.Success;
    }
}

/// <summary>Clears the in-memory event journal.</summary>
public sealed class EventsClearCommand : ICliCommandHandler
{
    private readonly IEventJournal? _journal;

    public EventsClearCommand(IServiceProvider services)
        => _journal = services.GetService<IEventJournal>();

    public ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (_journal is null)
        {
            invocation.Console.Error.WriteLine("The domain event journal is disabled in this host.");
            return ValueTask.FromResult(CliExitCodes.Failure);
        }

        var count = _journal.Count;
        _journal.Clear();
        invocation.Console.Out.WriteLine($"Cleared {count} buffered event(s).");
        return ValueTask.FromResult(CliExitCodes.Success);
    }
}
