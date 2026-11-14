using Natter.Abstractions.Events;
using Natter.Abstractions.Plugins;
using Natter.Cli.Abstractions;
using Natter.Infrastructure.Events;
using Microsoft.Extensions.Logging;

namespace Natter.Cli.Core;

/// <summary>
/// Optional lifecycle hook that prints every domain event when --trace-events is used.
/// Demonstrates that a host can consume the same Rx stream as plugins.
/// </summary>
public sealed class CliEventPrinter : IPluginLifecycle, IDisposable
{
    private readonly IDomainEventStream _stream;
    private readonly IConsoleAccessor _consoleAccessor;
    private readonly NatterCliOptions _options;
    private readonly ILogger<CliEventPrinter> _logger;
    private IDisposable? _subscription;

    public CliEventPrinter(
        IDomainEventStream stream,
        IConsoleAccessor consoleAccessor,
        NatterCliOptions options,
        ILogger<CliEventPrinter> logger)
    {
        _stream = stream;
        _consoleAccessor = consoleAccessor;
        _options = options;
        _logger = logger;
    }

    public ValueTask StartAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        if (!_options.TraceEvents)
            return ValueTask.CompletedTask;

        _subscription = _stream.SubscribeSafe(
            envelope => _consoleAccessor.Current.Out.WriteLine(Format(envelope)),
            _logger);

        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        _subscription?.Dispose();
        _subscription = null;
        return ValueTask.CompletedTask;
    }

    public static string Format(DomainEventEnvelope envelope)
        => $"[event #{envelope.Sequence}] {envelope.EventName} {Rendering.JsonOutput.SerializePayloadCompact(envelope.Event)}";

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }
}
