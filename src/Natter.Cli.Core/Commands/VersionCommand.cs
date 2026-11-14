using Natter.Cli.Abstractions;
using Natter.Cli.Core.Rendering;

namespace Natter.Cli.Core.Commands;

/// <summary>Prints framework and runtime information.</summary>
public sealed class VersionCommand : ICliCommandHandler
{
    public ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            framework = NatterInfo.Version,
            runtime = NatterInfo.Runtime,
            rid = NatterInfo.Rid,
            os = NatterInfo.Os,
            architecture = NatterInfo.Architecture,
            processPath = Environment.ProcessPath,
        };

        if (invocation.HasFlag("json"))
        {
            invocation.Console.Out.WriteLine(JsonOutput.Serialize(payload));
        }
        else
        {
            invocation.Console.Out.WriteLine($"Natter framework {NatterInfo.Version}");
            invocation.Console.Out.WriteLine($"  runtime : {NatterInfo.Runtime}");
            invocation.Console.Out.WriteLine($"  rid     : {NatterInfo.Rid}");
            invocation.Console.Out.WriteLine($"  os      : {NatterInfo.Os}");
            invocation.Console.Out.WriteLine($"  arch    : {NatterInfo.Architecture}");
        }

        return ValueTask.FromResult(CliExitCodes.Success);
    }
}
