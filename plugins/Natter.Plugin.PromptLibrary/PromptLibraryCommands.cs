using Natter.Cli.Abstractions;

namespace Natter.Plugin.PromptLibrary;

/// <summary>
/// <c>natter prompt explain [--json]</c>: shows preset id/version, fragment ids, disabled ids +
/// reasons, warnings and the plan hash. Never prints prompt text or variable values.
/// </summary>
public sealed class PromptExplainCommand(PromptLibraryRuntime runtime) : ICliCommandHandler
{
    private readonly PromptLibraryRuntime _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        invocation.Console.Out.WriteLine(_runtime.Describe(invocation.HasFlag("json")));
        return ValueTask.FromResult(CliExitCodes.Success);
    }
}
