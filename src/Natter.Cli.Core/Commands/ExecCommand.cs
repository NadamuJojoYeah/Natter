using Natter.Cli.Abstractions;

namespace Natter.Cli.Core.Commands;

/// <summary>
/// Runs several commands inside the same composed host. Useful to demonstrate that
/// plugin state (Rx subscriptions, counters) is host-scoped rather than process-scoped.
/// </summary>
public sealed class ExecCommand : ICliCommandHandler
{
    private const int MaxDepth = 4;
    private static readonly AsyncLocal<int> Depth = new();

    private readonly CliRunner _runner;

    public ExecCommand(CliRunner runner) => _runner = runner;

    public async ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.Positionals.Count == 0)
        {
            invocation.Console.Error.WriteLine("error: exec requires at least one command line.");
            return CliExitCodes.Usage;
        }

        if (Depth.Value >= MaxDepth)
        {
            invocation.Console.Error.WriteLine($"error: maximum exec nesting depth ({MaxDepth}) reached.");
            return CliExitCodes.Failure;
        }

        Depth.Value++;
        try
        {
            var exitCode = CliExitCodes.Success;

            foreach (var script in invocation.Positionals)
            {
                var tokens = CliCommandTokenizer.Split(script);
                if (tokens.Count == 0)
                    continue;

                invocation.Console.Out.WriteLine($"$ natter {string.Join(' ', tokens)}");
                var code = await _runner.RunAsync(tokens.ToArray(), invocation.Console, cancellationToken)
                    .ConfigureAwait(false);

                if (code != CliExitCodes.Success)
                {
                    exitCode = code;
                    if (!invocation.HasFlag("keep-going"))
                        break;
                }
            }

            return exitCode;
        }
        finally
        {
            Depth.Value--;
        }
    }
}
