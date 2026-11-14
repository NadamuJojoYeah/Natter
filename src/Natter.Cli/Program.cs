using Natter.Cli;
using Natter.Cli.Core;

var console = new SystemConsole();
using var cancellation = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

return await NatterCli.RunAsync(args, console, cancellationToken: cancellation.Token);
