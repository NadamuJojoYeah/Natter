using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Natter.Application.Admin;
using Natter.Cli.Abstractions;
using Natter.Infrastructure.Admin;

namespace Natter.Cli.Core;

/// <summary>
/// In-process implementation of <see cref="IAdminCommandRunner"/>. It resolves commands from the
/// host <see cref="CliCommandRegistry"/>, rejects anything outside the allowlist, runs the command
/// handler with a hard timeout and a bounded capturing console, then redacts and audits the result.
/// It never starts a shell or a child process and never concatenates arguments into a command line.
/// </summary>
public sealed class CliAdminCommandRunner : IAdminCommandRunner
{
    private readonly CliCommandRegistry _registry;
    private readonly IServiceProvider _services;
    private readonly ConsoleAccessor _consoleAccessor;
    private readonly AdminOptions _options;
    private readonly IAdminRedactor _redactor;
    private readonly IAdminAuditSink _audit;
    private readonly ILogger<CliAdminCommandRunner> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CliAdminCommandRunner(
        CliCommandRegistry registry,
        IServiceProvider services,
        ConsoleAccessor consoleAccessor,
        AdminOptions options,
        IAdminRedactor redactor,
        IAdminAuditSink audit,
        ILogger<CliAdminCommandRunner> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _consoleAccessor = consoleAccessor ?? throw new ArgumentNullException(nameof(consoleAccessor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<AdminCommandResult> RunAsync(
        string command,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var normalizedArgs = args?.ToArray() ?? [];

        if (!AdminCommandPolicy.TryValidate(command, normalizedArgs, out var normalizedCommand, out var validatedArgs, out var code, out var message))
            return await RejectAsync(command ?? string.Empty, normalizedArgs, code, message, startedAt, cancellationToken).ConfigureAwait(false);

        if (!AdminCommandPolicy.IsAllowed(normalizedCommand, _options.CommandAllowlist))
        {
            return await RejectAsync(
                normalizedCommand,
                normalizedArgs,
                AdminCodes.CommandNotAllowed,
                $"Command '{normalizedCommand}' is not in the host admin command allowlist.",
                startedAt,
                cancellationToken).ConfigureAwait(false);
        }

        if (!_registry.TryGet(normalizedCommand, out var descriptor))
        {
            return await RejectAsync(
                normalizedCommand,
                normalizedArgs,
                AdminCodes.CommandNotRegistered,
                $"Command '{normalizedCommand}' is not registered on this host.",
                startedAt,
                cancellationToken).ConfigureAwait(false);
        }

        var parsed = CliParser.Parse(descriptor, validatedArgs);
        if (parsed.ShowHelp || parsed.Error is not null)
        {
            return await RejectAsync(
                normalizedCommand,
                validatedArgs,
                AdminCodes.CommandInvalid,
                parsed.Error ?? "Command help is not part of the admin command surface.",
                startedAt,
                cancellationToken).ConfigureAwait(false);
        }

        // One admin command at a time: the ambient console accessor is process-wide state.
        var waitTimeout = TimeSpan.FromSeconds(Math.Max(1, _options.CommandTimeoutSeconds) + 1);
        if (!await _gate.WaitAsync(waitTimeout, cancellationToken).ConfigureAwait(false))
        {
            return await RejectAsync(
                normalizedCommand,
                normalizedArgs,
                AdminCodes.CommandBusy,
                "Another admin command is still running.",
                startedAt,
                cancellationToken).ConfigureAwait(false);
        }

        var console = new BoundedConsole(_options.MaxCommandOutputBytes);
        var timedOut = false;
        var exitCode = CliExitCodes.Success;
        string? failureMessage = null;

        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.CommandTimeoutSeconds)));

            var previousConsole = _consoleAccessor.Current;
            _consoleAccessor.Set(console);
            try
            {
                var handler = descriptor.HandlerFactory(_services);
                exitCode = await handler.ExecuteAsync(
                    new CliInvocation(descriptor.Name, parsed.Options, parsed.Positionals, _services, console),
                    budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested)
            {
                timedOut = !cancellationToken.IsCancellationRequested;
                exitCode = CliExitCodes.Cancelled;
                failureMessage = timedOut
                    ? $"Command timed out after {_options.CommandTimeoutSeconds}s."
                    : "Command was cancelled.";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Admin command {Command} failed.", normalizedCommand);
                exitCode = CliExitCodes.Failure;
                failureMessage = "Command failed: " + ex.Message;
            }
            finally
            {
                _consoleAccessor.Set(previousConsole);
            }
        }
        finally
        {
            _gate.Release();
        }

        var stdout = _redactor.Redact(console.Output);
        var stderr = _redactor.Redact(console.ErrorText);
        if (failureMessage is not null)
            stderr = string.IsNullOrEmpty(stderr) ? failureMessage : stderr + "\n" + failureMessage;
        stderr = _redactor.Redact(stderr);

        var result = new AdminCommandResult(
            Executed: true,
            Code: timedOut
                ? AdminCodes.CommandTimeout
                : failureMessage is null
                    ? AdminCodes.CommandSucceeded
                    : AdminCodes.CommandFailed,
            Message: timedOut
                ? "Command timed out."
                : failureMessage is null
                    ? "Command completed."
                    : failureMessage,
            Command: normalizedCommand,
            Args: normalizedArgs,
            ExitCode: exitCode,
            Stdout: stdout,
            Stderr: stderr,
            Truncated: console.Truncated,
            OutputBytes: Encoding.UTF8.GetByteCount(stdout) + Encoding.UTF8.GetByteCount(stderr),
            DurationMs: (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
            TimedOut: timedOut);

        await RecordAuditAsync(
            normalizedCommand,
            result.Code == AdminCodes.CommandTimeout || result.Code == AdminCodes.CommandFailed ? "error" : "ok",
            result.Code,
            normalizedArgs.Length,
            result.OutputBytes,
            startedAt,
            cancellationToken).ConfigureAwait(false);

        return result;
    }

    private async ValueTask<AdminCommandResult> RejectAsync(
        string command,
        IReadOnlyList<string> args,
        string code,
        string message,
        long startedAt,
        CancellationToken cancellationToken)
    {
        var result = new AdminCommandResult(
            Executed: false,
            Code: code,
            Message: message,
            Command: AdminCommandPolicy.NormalizeCommand(command),
            Args: args,
            ExitCode: CliExitCodes.Usage,
            Stdout: string.Empty,
            Stderr: string.Empty,
            Truncated: false,
            OutputBytes: 0,
            DurationMs: (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
            TimedOut: false);

        await RecordAuditAsync(result.Command, "denied", code, args.Count, 0, startedAt, cancellationToken)
            .ConfigureAwait(false);

        return result;
    }

    private async ValueTask RecordAuditAsync(
        string command,
        string outcome,
        string? code,
        int argCount,
        int outputBytes,
        long startedAt,
        CancellationToken cancellationToken)
    {
        if (!_options.EnableAudit)
            return;

        try
        {
            await _audit.RecordAsync(
                new AdminAuditEvent(
                    DateTimeOffset.UtcNow,
                    "host",
                    AdminAuditActions.RunCommand,
                    _redactor.Redact(command),
                    outcome,
                    code,
                    $"args={argCount};bytes={outputBytes}",
                    (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Admin command audit for {Command} could not be recorded.", command);
        }
    }

    /// <summary>Captures stdout/stderr into a shared UTF-8 byte budget and flags truncation.</summary>
    private sealed class BoundedConsole : IConsole
    {
        private readonly Lock _gate = new();
        private readonly StringBuilder _output = new();
        private readonly StringBuilder _error = new();
        private int _remainingBytes;
        private bool _truncated;

        public BoundedConsole(int maxBytes)
        {
            _remainingBytes = Math.Max(1_024, maxBytes);
            Out = new BoundedWriter(this, isError: false);
            Error = new BoundedWriter(this, isError: true);
        }

        public TextWriter Out { get; }

        public TextWriter Error { get; }

        public bool IsInteractive => false;

        public string? ReadLine() => null;

        public bool Truncated
        {
            get
            {
                lock (_gate)
                    return _truncated;
            }
        }

        public string Output
        {
            get
            {
                lock (_gate)
                    return _output.ToString();
            }
        }

        public string ErrorText
        {
            get
            {
                lock (_gate)
                    return _error.ToString();
            }
        }

        private void Append(bool isError, string? value)
        {
            if (string.IsNullOrEmpty(value))
                return;

            lock (_gate)
            {
                var target = isError ? _error : _output;
                var bytes = Encoding.UTF8.GetByteCount(value);
                if (bytes <= _remainingBytes)
                {
                    target.Append(value);
                    _remainingBytes -= bytes;
                    return;
                }

                _truncated = true;
                var budget = _remainingBytes;
                if (budget <= 0)
                    return;

                var used = 0;
                foreach (var rune in value.EnumerateRunes())
                {
                    if (used + rune.Utf8SequenceLength > budget)
                        break;

                    target.Append(rune.ToString());
                    used += rune.Utf8SequenceLength;
                }

                _remainingBytes -= used;
            }
        }

        private sealed class BoundedWriter : TextWriter
        {
            private readonly BoundedConsole _owner;
            private readonly bool _isError;

            public BoundedWriter(BoundedConsole owner, bool isError)
            {
                _owner = owner;
                _isError = isError;
            }

            public override Encoding Encoding => Encoding.UTF8;

            public override void Write(char value)
                => _owner.Append(_isError, value.ToString());

            public override void Write(string? value)
                => _owner.Append(_isError, value);

            public override void WriteLine(string? value)
                => _owner.Append(_isError, value + Environment.NewLine);
        }
    }
}
