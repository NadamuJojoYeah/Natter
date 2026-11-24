using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Natter.Application.Chat;
using Natter.Application.Redaction;
using Natter.Chat;
using Natter.Cli.Abstractions;
using Natter.Cli.Core;
using Natter.Cli.Core.Commands;
using Natter.Chat.DingTalk;
using Natter.Cli.Core.Logging;
using Natter.Infrastructure.Chat;
using Natter.Infrastructure.Composition;
using Natter.Infrastructure.Plugins;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// task-69: prove diagnostic text cannot leave the process unredacted. The decorators are the
/// mechanism; the static scan is the guard against a new raw output path.
/// </summary>
public sealed class DiagnosticsLeakTests
{
    private const string Canary = "sk-live-ABCDEFGHIJKLMNOPQRSTUV";
    private const string TokenCanary = "token=super-secret-value-42";
    private const string PemCanary = "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA\n-----END RSA PRIVATE KEY-----";

    [Fact]
    public void Redacting_console_removes_canaries_from_stdout_and_stderr()
    {
        var inner = new StringConsole();
        IConsole console = RedactingConsole.Wrap(inner);
        console.Out.WriteLine($"provider rejected {Canary}");
        console.Error.WriteLine($"config {TokenCanary}");
        console.Out.WriteLine(PemCanary);

        foreach (var text in new[] { inner.OutWriter.ToString(), inner.ErrorWriter.ToString() })
        {
            Assert.DoesNotContain(Canary, text, StringComparison.Ordinal);
            Assert.DoesNotContain("super-secret-value-42", text, StringComparison.Ordinal);
            Assert.DoesNotContain("MIIEowIBAAKCAQEA", text, StringComparison.Ordinal);
            Assert.Contains("[redacted]", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Redacting_logger_provider_removes_canaries_from_messages_and_exceptions()
    {
        var sink = new CapturingLoggerProvider();
        var provider = new RedactingLoggerProvider(sink);
        var logger = provider.CreateLogger("Natter.Test");
        logger.LogWarning("upstream failed lastError={LastError}", $"boom {Canary}");
        logger.LogError(new InvalidOperationException($"token {TokenCanary}"), "exception path");

        var text = string.Join("\n", sink.Messages);
        Assert.DoesNotContain(Canary, text, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-value-42", text, StringComparison.Ordinal);
        Assert.Contains("[redacted]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Redacted_text_serializes_redacted_but_keeps_raw()
    {
        var text = new RedactedText($"token {TokenCanary}");
        Assert.Equal($"token {TokenCanary}", text.Raw);
        Assert.DoesNotContain("super-secret-value-42", text.ToString(), StringComparison.Ordinal);
        Assert.Contains("[redacted]", text.ToString(), StringComparison.Ordinal);

        var json = JsonSerializer.Serialize(new { lastError = text });
        Assert.DoesNotContain("super-secret-value-42", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeHealth_json_never_contains_a_canary()
    {
        var upstream = new CanaryUpstream(Canary);
        var registry = new ChatUpstreamRegistry();
        registry.Register(upstream);
        var snapshot = await RuntimeHealth.CreateAsync(
            registry,
            new RuntimeProfileStore(null),
            new ChatResponderRegistry(),
            new InMemoryConversationStore(),
            new PluginCatalog(),
            new HostRuntimeClock(),
            new TaskMetrics());

        var json = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain(Canary, json, StringComparison.Ordinal);
        Assert.DoesNotContain("token=super-secret-value-42", json, StringComparison.Ordinal);
        Assert.Equal(Canary, snapshot.Upstreams.Single().LastError!.Raw);
    }

    [Fact]
    public void DingTalk_status_event_detail_is_redacted()
    {
        var redacted = DingTalkDiagnostics.Redact($"transport error {Canary}");
        Assert.DoesNotContain(Canary, redacted!, StringComparison.Ordinal);
        Assert.Contains("[redacted]", redacted!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_health_command_output_never_contains_a_canary()
    {
        var console = new StringConsole();
        var options = new NatterCliOptions
        {
            LoadPlugins = false,
            OnHostStartedAsync = services =>
            {
                services.GetRequiredService<IChatUpstreamRegistry>()
                    .Register(new CanaryUpstream($"transport {Canary}"));
                return ValueTask.CompletedTask;
            },
        };

        var exitCode = await NatterCli.RunAsync(["health", "--json"], console, options);
        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.DoesNotContain(Canary, console.OutWriter.ToString(), StringComparison.Ordinal);
        Assert.Contains("[redacted]", console.OutWriter.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Static_scan_allows_no_raw_LastError_output_in_production_sources()
    {
        var root = RepoRoot();
        var violations = StaticScan.FindViolations(root);
        Assert.Empty(violations);
    }

    [Fact]
    public void Static_scan_catches_a_synthetic_bare_output()
    {
        // Negative validation: the analyzer must flag a deliberately bad snippet, so a green
        // repository test cannot mean "the rule is broken".
        var bad = "invocation.Console.Out.WriteLine($\"lastError: {snapshot.Metrics.LastError ?? \"-\"}\");";
        var good = "invocation.Console.Out.WriteLine(RuntimeHealth.Sanitize(snapshot.Metrics.LastError));";
        Assert.Single(StaticScan.Analyze("synthetic.cs", bad));
        Assert.Empty(StaticScan.Analyze("synthetic.cs", good));
    }

    // ------------------------------------------------------------------ helpers

    private sealed class StringConsole : IConsole
    {
        public StringWriter OutWriter { get; } = new();

        public StringWriter ErrorWriter { get; } = new();

        public TextWriter Out => OutWriter;

        public TextWriter Error => ErrorWriter;

        public bool IsInteractive => false;

        public string? ReadLine() => null;

        public string Output => OutWriter.ToString();
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (messages)
                    messages.Add(formatter(state, exception) + (exception is null ? string.Empty : " | " + exception));
            }
        }
    }

    private sealed class CanaryUpstream(string error) : IChatUpstream, IChatUpstreamHealth
    {
        public Source Source { get; } = Source.Create("discord", "discord", "bot");

        public bool IsConnected => true;

        public IObservable<ChatEvent> Events { get; } = new EmptyObservable();

        public UpstreamHealth Health => new(
            Source.Key,
            true,
            null,
            null,
            DateTimeOffset.UtcNow,
            1,
            error,
            0,
            null,
            error,
            "transport_stream");

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<Capabilities> GetCapabilitiesAsync(Conversation? conversation = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new Capabilities());

        public ValueTask<ChatAck> ExecuteAsync(ChatCommand command, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ChatAck.Success(command));

        private sealed class EmptyObservable : IObservable<ChatEvent>
        {
            public IDisposable Subscribe(IObserver<ChatEvent> observer) => new EmptyDisposable();

            private sealed class EmptyDisposable : IDisposable
            {
                public void Dispose()
                {
                }
            }
        }
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Natter.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static class StaticScan
    {
        private static readonly string[] Roots = ["src", "samples", "plugins"];

        public static List<string> FindViolations(string root)
        {
            var violations = new List<string>();
            foreach (var directory in Roots)
            {
                var full = Path.Combine(root, directory);
                if (!Directory.Exists(full))
                    continue;
                foreach (var file in Directory.GetFiles(full, "*.cs", SearchOption.AllDirectories))
                {
                    if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                        continue;
                    violations.AddRange(Analyze(file, File.ReadAllText(file)));
                }
            }

            return violations;
        }

        public static IReadOnlyList<string> Analyze(string file, string source)
        {
            var violations = new List<string>();
            foreach (var line in source.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;

                var isOutput = trimmed.Contains("Console.Out.WriteLine", StringComparison.Ordinal)
                               || trimmed.Contains("JsonSerializer.Serialize", StringComparison.Ordinal);
                if (!isOutput || !trimmed.Contains("LastError", StringComparison.Ordinal))
                    continue;

                var isSafe = trimmed.Contains("Sanitize", StringComparison.Ordinal)
                             || trimmed.Contains("Redact", StringComparison.Ordinal)
                             || trimmed.Contains("RedactedText", StringComparison.Ordinal);
                if (!isSafe)
                    violations.Add($"{file}: raw LastError output: {trimmed}");
            }

            return violations;
        }
    }
}
