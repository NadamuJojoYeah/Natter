using Microsoft.Extensions.Logging;
using Natter.Application.Redaction;

namespace Natter.Cli.Core.Logging;

/// <summary>
/// Wraps any <see cref="ILoggerProvider"/> so formatted messages and exception text pass through
/// the shared redaction core before reaching the inner sink. Installed by the CLI composition
/// root; adapters keep logging normally and cannot leak a raw LastError through a log line.
/// </summary>
public sealed class RedactingLoggerProvider : ILoggerProvider
{
    private readonly ILoggerProvider _inner;

    public RedactingLoggerProvider(ILoggerProvider inner)
        => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public ILogger CreateLogger(string categoryName) => new RedactingLogger(_inner.CreateLogger(categoryName));

    public void Dispose() => _inner.Dispose();

    private sealed class RedactingLogger(ILogger inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

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
            var safe = SecretRedaction.Apply(message, SecretRedactionOptions.Admin);
            if (exception is not null)
                safe += " | " + SecretRedaction.Apply(exception.ToString(), SecretRedactionOptions.Admin);

            // Pass the redacted text as the message and drop the exception object so no inner sink
            // can re-render the original exception after us.
            inner.Log(logLevel, eventId, safe, null, static (value, _) => value);
        }
    }
}
