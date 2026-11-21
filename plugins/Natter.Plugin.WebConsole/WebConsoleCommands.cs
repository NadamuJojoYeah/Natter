using Natter.Cli.Abstractions;

namespace Natter.Plugin.WebConsole;

/// <summary><c>natter web status [--json]</c>: local self-check without HTTP or token.</summary>
public sealed class WebStatusCommand : ICliCommandHandler
{
    private readonly WebConsoleRuntime _runtime;

    public WebStatusCommand(WebConsoleRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        invocation.Console.Out.WriteLine(_runtime.DescribeStatus(invocation.HasFlag("json")));
        return ValueTask.FromResult(CliExitCodes.Success);
    }
}
