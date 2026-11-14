using Microsoft.Extensions.Logging;
using Natter.Cli.Abstractions;

namespace Natter.Cli.Core.Logging;

/// <summary>
/// Minimal ILoggerProvider that writes to the host console's error writer.
/// Keeps the framework free of a console logging package dependency.
/// </summary>
public sealed class CliLoggerProvider : ILoggerProvider
{
    private readonly IConsole _console;

    public CliLoggerProvider(IConsole console) => _console = console;

    public ILogger CreateLogger(string categoryName) => new CliLogger(_console, categoryName);

    public void Dispose()
    {
    }

    private sealed class CliLogger : ILogger
    {
        private readonly IConsole _console;
        private readonly string _shortCategory;

        public CliLogger(IConsole console, string categoryName)
        {
            _console = console;
            var lastDot = categoryName.LastIndexOf('.');
            _shortCategory = lastDot >= 0 ? categoryName[(lastDot + 1)..] : categoryName;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            _console.Error.WriteLine($"[{logLevel.ToString().ToLowerInvariant()}] {_shortCategory}: {message}");

            if (exception is not null)
                _console.Error.WriteLine(exception.ToString());
        }
    }
}
