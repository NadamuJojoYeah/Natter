using Microsoft.Extensions.Logging;
using Natter.Application.Admin;

namespace Natter.Infrastructure.Admin;

/// <summary>
/// Logger provider that fills the admin log ring buffer. It only observes: it never writes to a
/// console/file and never throws into the logging pipeline.
/// </summary>
public sealed class AdminLogBufferLoggerProvider : ILoggerProvider
{
    private readonly AdminLogBuffer _buffer;
    private readonly IAdminRedactor _redactor;

    public AdminLogBufferLoggerProvider(AdminLogBuffer buffer, IAdminRedactor redactor)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
    }

    public ILogger CreateLogger(string categoryName)
        => new AdminLogBufferLogger(_buffer, _redactor, categoryName);

    public void Dispose()
    {
    }

    private sealed class AdminLogBufferLogger : ILogger
    {
        private readonly AdminLogBuffer _buffer;
        private readonly IAdminRedactor _redactor;
        private readonly string _category;

        public AdminLogBufferLogger(AdminLogBuffer buffer, IAdminRedactor redactor, string category)
        {
            _buffer = buffer;
            _redactor = redactor;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel)
            => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.None)
                return;

            try
            {
                var message = formatter(state, exception);
                if (exception is not null)
                    message = message + " | " + exception.GetType().Name + ": " + exception.Message;

                _buffer.Add(
                    DateTimeOffset.UtcNow,
                    logLevel.ToString(),
                    _category,
                    _redactor.Redact(message));
            }
            catch
            {
                // Diagnostics must never break the host logging pipeline.
            }
        }
    }
}

/// <summary>Read-only host log tail used by the WebConsole plugin (gap G8).</summary>
public sealed class HostAdminLogReader : IAdminLogReader
{
    private readonly AdminLogBuffer _buffer;
    private readonly AdminOptions _options;
    private readonly IAdminAuditSink _audit;
    private readonly ILogger<HostAdminLogReader> _logger;

    public HostAdminLogReader(
        AdminLogBuffer buffer,
        AdminOptions options,
        IAdminAuditSink audit,
        ILogger<HostAdminLogReader> logger)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<IReadOnlyList<AdminLogEntry>> ReadAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var effectiveLimit = Math.Clamp(limit, 1, Math.Min(_options.MaxLogLimit, _buffer.Capacity));
        var entries = _buffer.Snapshot(effectiveLimit);

        if (_options.EnableAudit)
        {
            try
            {
                await _audit.RecordAsync(
                    new AdminAuditEvent(
                        DateTimeOffset.UtcNow,
                        "host",
                        AdminAuditActions.ReadLogs,
                        "host.log",
                        "ok",
                        null,
                        $"entries={entries.Count};limit={effectiveLimit}"),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Admin log read could not be audited.");
            }
        }

        return entries;
    }
}
