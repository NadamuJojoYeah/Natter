using Natter.Cli.Abstractions;

namespace Natter.Cli.Core.Commands;

/// <summary>Global or per-command help.</summary>
public sealed class HelpCommand : ICliCommandHandler
{
    private readonly CliCommandRegistry _registry;

    public HelpCommand(CliCommandRegistry registry) => _registry = registry;

    public ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var target = invocation.GetPositional(0);

        if (string.IsNullOrWhiteSpace(target))
        {
            invocation.Console.Out.WriteLine(HelpText.RenderGlobal(_registry));
            return ValueTask.FromResult(CliExitCodes.Success);
        }

        if (_registry.TryGet(target, out var command))
        {
            invocation.Console.Out.WriteLine(HelpText.RenderCommand(command));
            return ValueTask.FromResult(CliExitCodes.Success);
        }

        invocation.Console.Error.WriteLine($"Unknown command '{target}'.");
        invocation.Console.Out.WriteLine(HelpText.RenderGlobal(_registry));
        return ValueTask.FromResult(CliExitCodes.Usage);
    }
}
