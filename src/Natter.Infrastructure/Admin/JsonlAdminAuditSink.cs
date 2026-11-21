using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Natter.Application.Admin;

namespace Natter.Infrastructure.Admin;

/// <summary>
/// Append-only JSONL audit sink for the host admin surface. Lines are redacted, bounded and
/// written with 0600 permissions; an audit failure never fails the admin operation itself.
/// </summary>
public sealed class JsonlAdminAuditSink : IAdminAuditSink
{
    public const int MaxDetailLength = 2_048;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly AdminOptions _options;
    private readonly IAdminRedactor _redactor;
    private readonly ILogger<JsonlAdminAuditSink> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonlAdminAuditSink(
        AdminOptions options,
        IAdminRedactor redactor,
        ILogger<JsonlAdminAuditSink> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask RecordAsync(AdminAuditEvent entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!_options.EnableAudit || string.IsNullOrWhiteSpace(_options.StateDirectory))
            return;

        var line = Serialize(entry);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetFullPath(_options.StateDirectory);
            Directory.CreateDirectory(directory);
            SetUnixMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, isDirectory: true);

            var path = Path.Combine(directory, _options.AuditFileName);
            var created = !File.Exists(path);
            await File.AppendAllTextAsync(path, line + "\n", Utf8NoBom, cancellationToken).ConfigureAwait(false);
            if (created)
                SetUnixMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite, isDirectory: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (ex is OperationCanceledException)
                return;

            _logger.LogWarning(ex, "Admin audit line for {Action} could not be written.", entry.Action);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string Serialize(AdminAuditEvent entry)
    {
        var normalized = entry with
        {
            Actor = _redactor.Redact(entry.Actor),
            Target = _redactor.Redact(entry.Target),
            Result = _redactor.Redact(entry.Result),
            Code = entry.Code is null ? null : _redactor.Redact(entry.Code),
            Detail = entry.Detail is null
                ? null
                : Truncate(_redactor.Redact(entry.Detail), MaxDetailLength),
        };

        return JsonSerializer.Serialize(normalized, JsonOptions);
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "…";

    private static void SetUnixMode(string path, UnixFileMode mode, bool isDirectory)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
