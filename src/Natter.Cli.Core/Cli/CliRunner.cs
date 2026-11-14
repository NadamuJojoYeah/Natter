using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Natter.Cli.Abstractions;

namespace Natter.Cli.Core;

/// <summary>Executes commands resolved from the registry and owns top-level error handling.</summary>
public sealed class CliRunner
{
    private readonly CliCommandRegistry _registry;
    private readonly IServiceProvider _services;
    private readonly ConsoleAccessor _consoleAccessor;
    private readonly NatterCliOptions _options;
    private readonly ILogger<CliRunner> _logger;

    public CliRunner(
        CliCommandRegistry registry,
        IServiceProvider services,
        ConsoleAccessor consoleAccessor,
        NatterCliOptions options,
        ILogger<CliRunner> logger)
    {
        _registry = registry;
        _services = services;
        _consoleAccessor = consoleAccessor;
        _options = options;
        _logger = logger;
    }

    public async Task<int> RunAsync(
        string[] args,
        IConsole console,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(console);

        _consoleAccessor.Set(console);

        if (args.Length == 0)
        {
            console.Out.WriteLine(HelpText.RenderGlobal(_registry));
            return CliExitCodes.Success;
        }

        if (args[0] is "--help" or "-h" or "-?")
        {
            console.Out.WriteLine(HelpText.RenderGlobal(_registry));
            return CliExitCodes.Success;
        }

        if (!_registry.TryResolve(args, out var command, out var consumed))
        {
            console.Error.WriteLine($"Unknown command: {args[0]}");
            console.Out.WriteLine();
            console.Out.WriteLine(HelpText.RenderGlobal(_registry));
            return CliExitCodes.Usage;
        }

        var parsed = CliParser.Parse(command, args.Skip(consumed).ToArray());

        if (parsed.ShowHelp)
        {
            console.Out.WriteLine(HelpText.RenderCommand(command));
            return CliExitCodes.Success;
        }

        if (parsed.Error is not null)
        {
            console.Error.WriteLine($"error: {parsed.Error}");
            console.Out.WriteLine();
            console.Out.WriteLine(HelpText.RenderCommand(command));
            return CliExitCodes.Usage;
        }

        var invocation = new CliInvocation(
            command.Name,
            parsed.Options,
            parsed.Positionals,
            _services,
            console);

        try
        {
            var handler = command.HandlerFactory(_services);
            return await handler.ExecuteAsync(invocation, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            console.Error.WriteLine("cancelled.");
            return CliExitCodes.Cancelled;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command '{Command}' failed.", command.Name);
            console.Error.WriteLine($"error: {ex.Message}");

            if (_options.Verbose)
                console.Error.WriteLine(ex.ToString());

            return CliExitCodes.Failure;
        }
    }
}
