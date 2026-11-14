using Natter.Abstractions.Ports;
using Natter.Cli.Abstractions;

namespace Natter.Sample.Plugin.Greetings;

/// <summary>`greet &lt;name&gt;` command handler.</summary>
public sealed class GreetCommandHandler : ICliCommandHandler
{
    private readonly IGreetingPort _greetingPort;
    private readonly RecentEventBuffer _buffer;

    public GreetCommandHandler(IGreetingPort greetingPort, RecentEventBuffer buffer)
    {
        _greetingPort = greetingPort;
        _buffer = buffer;
    }

    public async ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var name = invocation.GetPositional(0, "world");
        var message = await _greetingPort.GreetAsync(name, cancellationToken).ConfigureAwait(false);

        invocation.Console.Out.WriteLine(message);

        var recent = _buffer.Snapshot();
        invocation.Console.Out.WriteLine(
            recent.Count == 0
                ? "The plugin's event subscription has not observed any domain event yet."
                : $"The plugin observed {recent.Count} recent event(s); run 'natter greetings recent' to list them.");

        return CliExitCodes.Success;
    }
}
