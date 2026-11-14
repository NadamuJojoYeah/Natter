using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Application;
using Natter.Abstractions.Events;
using Natter.Abstractions.Ports;
using Natter.Application.Demo;
using Natter.Cli.Abstractions;
using Natter.Cli.Core.Rendering;
using Natter.Infrastructure.Events;

namespace Natter.Cli.Core.Commands;

/// <summary>
/// End-to-end demo of the framework pipeline: command → aggregate → repository port →
/// domain event dispatcher → Rx stream, with a plugin-provided greeting adapter.
/// </summary>
public sealed class DemoCommand : ICliCommandHandler
{
    private readonly IApplicationDispatcher _dispatcher;
    private readonly IDomainEventStream _stream;
    private readonly IGreetingPort _greetingPort;
    private readonly IEventJournal? _journal;

    public DemoCommand(
        IApplicationDispatcher dispatcher,
        IDomainEventStream stream,
        IGreetingPort greetingPort,
        IServiceProvider services)
    {
        _dispatcher = dispatcher;
        _stream = stream;
        _greetingPort = greetingPort;
        _journal = services.GetService<IEventJournal>();
    }

    public async ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var name = invocation.GetOption("name", "demo");
        var amount = Math.Max(1, invocation.GetIntOption("amount", 1));
        var times = Math.Clamp(invocation.GetIntOption("times", 3), 1, 1000);

        var collected = new List<DomainEventEnvelope>();
        using var subscription = _stream.SubscribeSafe(envelope =>
        {
            lock (collected) collected.Add(envelope);
        });

        var snapshot = await _dispatcher
            .SendAsync(new IncrementCounterCommand(name, amount, times), cancellationToken)
            .ConfigureAwait(false);

        var greeting = await _greetingPort
            .GreetAsync(name, cancellationToken)
            .ConfigureAwait(false);

        if (invocation.HasFlag("json"))
        {
            var payload = new
            {
                counter = snapshot,
                greeting,
                events = collected.Select(JsonOutput.Project).ToList(),
                journalCount = _journal?.Count ?? 0,
            };

            invocation.Console.Out.WriteLine(JsonOutput.Serialize(payload));
            return CliExitCodes.Success;
        }

        var output = invocation.Console.Out;
        output.WriteLine(greeting);
        output.WriteLine($"counter '{snapshot.Name}' ({snapshot.Id}) = {snapshot.Value} after {times} increment(s) of {amount}.");
        output.WriteLine($"{snapshot.EventsRaised} domain event(s) dispatched into the Rx stream:");
        output.WriteLine();

        ConsoleTable.Write(
            output,
            ["SEQ", "EVENT", "PAYLOAD"],
            collected.Select(envelope => (IReadOnlyList<string>)
            [
                envelope.Sequence.ToString(),
                envelope.EventName,
                JsonOutput.SerializePayloadCompact(envelope.Event),
            ]).ToList());

        return CliExitCodes.Success;
    }
}
