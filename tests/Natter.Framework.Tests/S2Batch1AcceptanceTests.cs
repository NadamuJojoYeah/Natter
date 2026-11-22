using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Abstractions.Plugins;
using Natter.Chat.DingTalk;
using Natter.Cli.Abstractions;
using Natter.Cli.Core.Commands;
using Natter.Cli.Core;
using Natter.Feature.Ai;
using Microsoft.Extensions.DependencyInjection;
using Natter.Infrastructure.Chat;
using Natter.Infrastructure.Composition;
using Natter.Infrastructure.Plugins;
using Natter.Infrastructure.Plugins.Nps;
using Natter.Plugin.Abstractions;
using Natter.Plugin.Store;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// S2 batch-1 independent acceptance (task-42), focused on the reverse checks the implementation
/// tests are most likely to miss: a real expansion bomb, webhook SSRF, a colon-heavy Source round
/// trip, cancellation before the model call, cycle isolation, the CLI degraded exit code and the
/// conversation-gate bounds.
/// Report: docs/testing/2026-11-22-s2-batch1-acceptance.md
/// </summary>
public sealed class S2Batch1AcceptanceTests
{
    // ------------------------------------------------------------ C1 zip bomb

    [Fact]
    public async Task High_ratio_archive_is_rejected_before_anything_is_extracted()
    {
        using var temp = new TempDirectory();
        var package = temp.Combine("bomb.npa");
        CreateZip(package, "payload.bin", new byte[4 * 1024 * 1024]); // 4 MiB of zeros compresses to a few KiB
        var extract = temp.Combine("extract");
        var validator = new PluginPackageValidator(Options(temp));

        var result = await validator.ValidateAsync(package, extract);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Issues,
            issue => issue.Code.Contains("ratio", StringComparison.OrdinalIgnoreCase)
                     || issue.Message.Contains("compression ratio", StringComparison.OrdinalIgnoreCase));
        Assert.True(DiskBytes(extract) <= 64 * 1024, $"extract directory grew to {DiskBytes(extract)} bytes");
    }

    [Fact]
    public async Task Archive_that_lies_about_its_size_is_stopped_while_extracting()
    {
        using var temp = new TempDirectory();
        var package = temp.Combine("liar.npa");
        CreateZip(
            package,
            [("plugin.json", Encoding.UTF8.GetBytes(MinimalManifest("natter.s2.liar"))),
             ("payload.bin", new byte[4 * 1024 * 1024])]);
        DeclareSmallUncompressedSize(package, declared: 100, entryHint: "payload.bin");

        var extract = temp.Combine("extract");
        // Small cap so the extraction guard trips quickly.
        var validator = new PluginPackageValidator(Options(temp, maxTotalUncompressed: 512 * 1024));

        var result = await validator.ValidateAsync(package, extract);

        Assert.False(result.IsValid);
        // Whatever gate trips first, the bomb must never reach the disk: the extract directory
        // stays far below the 4 MiB the archive really expands to.
        var written = DiskBytes(extract);
        Assert.True(written < 4 * 1024 * 1024, $"extract grew to {written} bytes");
        if (result.Issues.Any(issue => issue.Message.Contains("while extracting", StringComparison.OrdinalIgnoreCase)))
            Assert.True(written <= 512 * 1024, $"extraction guard must stop the write, wrote {written} bytes");
    }

    // ---------------------------------------- C1b manifest-clean then explode

    [Fact]
    public async Task Package_that_passes_the_manifest_but_expands_only_while_extracting_is_stopped()
    {
        // task-44 gap: the first bomb failed on the manifest (no plugin.json). This one is a real
        // package - valid manifest, real managed PE entry, matching package.sha256 - where only the
        // zip header lies about payload.bin, so the expansion happens inside ExtractAsync.
        using var temp = new TempDirectory();
        var package = temp.Combine("clean-but-expanding.npa");
        var manifest = Encoding.UTF8.GetBytes(MinimalManifest("natter.s2.expand", entryAssembly: "lib/probe.dll"));
        var dll = File.ReadAllBytes(Path.Combine(
            RepoRoot(), "samples", "Natter.Sample.Plugin.Greetings", "bin", "Debug", "net10.0",
            "Natter.Sample.Plugin.Greetings.dll"));
        var payload = new byte[4 * 1024 * 1024];
        var manifestHash = Convert.ToHexString(SHA256.HashData(manifest)).ToLowerInvariant();
        var dllHash = Convert.ToHexString(SHA256.HashData(dll)).ToLowerInvariant();
        var payloadHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var hashFile = Encoding.ASCII.GetBytes(
            $"{manifestHash}  plugin.json\n{dllHash}  lib/probe.dll\n{payloadHash}  payload.bin\n");

        CreateZip(
            package,
            [("plugin.json", manifest), ("lib/probe.dll", dll), ("payload.bin", payload), ("package.sha256", hashFile)]);
        DeclareSmallUncompressedSize(package, declared: 100, entryHint: "payload.bin");

        var extract = temp.Combine("extract");
        var validator = new PluginPackageValidator(Options(temp, maxTotalUncompressed: 512 * 1024));
        var result = await validator.ValidateAsync(package, extract);

        Assert.False(result.IsValid);
        // The package is rejected on one of the expansion/consistency gates. .NET honours the
        // declared length when reading an entry, so the lie is also caught by the hash manifest
        // (extracted 100 B vs declared 4 MiB); either way the bomb never lands on disk.
        Assert.Contains(
            result.Issues,
            issue => issue.Code is "package-uncompressed-too-large" or "compression-ratio" or "hash-mismatch"
                     || issue.Message.Contains("while extracting", StringComparison.OrdinalIgnoreCase));
        Assert.True(
            DiskBytes(extract) <= 2 * 1024 * 1024,
            $"extraction guard must stop the write, extract grew to {DiskBytes(extract)} bytes");
    }

    // ------------------------------------------------------- A5 webhook SSRF

    [Theory]
    [InlineData("http://oapi.dingtalk.com/robot/send?access_token=x", "scheme_not_https")]
    [InlineData("https://oapi.dingtalk.com.evil.example/robot/send", "host_not_allowed")]
    [InlineData("https://evil.example/robot/send", "host_not_allowed")]
    [InlineData("https://user:pass@oapi.dingtalk.com/robot/send", "credentials_in_url")]
    [InlineData("https://oapi.dingtalk.com:8443/robot/send", "non_default_port")]
    [InlineData("https://1.2.3.4/robot/send", "host_not_allowed")]
    [InlineData("https://[::1]/robot/send", "host_not_allowed")]
    [InlineData("not-a-url", "invalid_url")]
    [InlineData("", "missing")]
    public void Session_webhook_outside_the_dingtalk_https_allowlist_is_rejected(string webhook, string reason)
    {
        Assert.False(DingTalkSessionWebhookPolicy.TryValidate(webhook, out var uri, out var actual));
        Assert.Null(uri);
        Assert.Equal(reason, actual);
    }

    [Theory]
    [InlineData("https://oapi.dingtalk.com/robot/send?access_token=x")]
    [InlineData("https://api.dingtalk.com/v1.0/robot/oToMessages/batchSend")]
    [InlineData("https://sub.dingtalk.com/robot/send")]
    public void Official_dingtalk_webhooks_still_pass(string webhook)
    {
        Assert.True(DingTalkSessionWebhookPolicy.TryValidate(webhook, out var uri, out var reason), reason);
        Assert.NotNull(uri);
        Assert.Equal(443, uri!.Port);
    }

    [Fact]
    public async Task Rejected_session_webhook_never_reaches_the_http_handler()
    {
        var handler = new CountingHttpHandler();
        using var http = new HttpClient(handler);
        using var transport = new DingTalkWebhookTransport(
            new DingTalkOptions { ClientId = "ding", ClientSecret = "secret", BotId = "bot" },
            "app-secret",
            http);

        var result = await transport.SendAsync(
            new DingTalkReply("c1", "https://oapi.dingtalk.com.evil.example/robot/send?access_token=x", "hi"));

        Assert.False(result.Ok);
        Assert.Equal(DingTalkSessionWebhookPolicy.RejectedCode, result.ErrorCode);
        Assert.Equal(0, handler.Calls);
    }

    // --------------------------------------------------- B4 Source round trip

    [Fact]
    public async Task Source_with_a_colon_heavy_self_id_survives_sqlite_and_delivers_the_result()
    {
        var path = Path.Combine(Path.GetTempPath(), "natter-s2-source-" + Guid.NewGuid().ToString("N"), "natter.db");
        var store = new SqliteTaskStore(path);
        var source = Source.Create("discord", "discord", "bot:instance:42");
        var task = new AgentTask
        {
            TaskId = "task_b4",
            SessionId = "session",
            Source = source,
            ConversationId = "c:1",
            ActorId = "u1",
            Prompt = "do it",
            Status = AgentTaskStatus.Pending,
        };
        await store.CreateAsync(task);

        var reopened = new SqliteTaskStore(path);
        var loaded = await reopened.GetAsync(task.TaskId);

        Assert.NotNull(loaded);
        Assert.Equal(source.Protocol, loaded!.Source.Protocol);
        Assert.Equal(source.Platform, loaded.Source.Platform);
        Assert.Equal("bot:instance:42", loaded.Source.SelfId);
        Assert.Equal("c:1", loaded.ConversationId);

        // The reconstructed source must still route a result back to the platform.
        var recorder = new RecordingCommandDispatcher();
        var profiles = new RuntimeProfileStore(null);
        await using var dispatcher = new TaskDispatcher(
            reopened, new ImmediateWorker("done"), new ProfileResolver(profiles), profiles,
            recorder, new ChatCommandFactory(new TestClock()));
        await dispatcher.EnqueueAsync(loaded);
        await WaitUntilAsync(() => Task.FromResult(recorder.Commands.Count == 1), TimeSpan.FromSeconds(5));

        var sent = Assert.IsType<MessageSendCommand>(recorder.Commands[0]);
        Assert.Equal("discord:discord:bot:instance:42", sent.Source.Key);
        Assert.Equal("c:1", sent.Payload.Conversation.Id);
    }

    // ------------------------------------------------- B10 cancellation is real

    [Fact]
    public async Task Cancellation_before_the_call_prevents_any_model_invocation()
    {
        var options = new AiResponderOptions();
        var client = new RecordingChatClient();
        var responder = new AiChatResponder(
            client,
            options,
            new AiConversationStore(options),
            new AiConversationGate(options),
            new ChatCommandFactory(new TestClock()),
            new NullPluginLog());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            responder.RespondAsync(Event("hello"), cts.Token).AsTask());

        Assert.Equal(0, client.CallCount);
    }

    // ---------------------------------------------- C3 unload (routing only)

    [Fact]
    public async Task Unloaded_plugin_is_no_longer_routed_and_its_command_is_unresolvable()
    {
        // task-44/C3 PASS criterion: after the host stops the plugin, its command must no longer
        // resolve. History: BUG-S2-C3-UNREGISTER was found here (state=Stopped but TryGet=true,
        // because CliCommandRegistry.UnregisterOwner had no caller). The task-43 CLI-unregister
        // change makes it pass; on HEAD without that change this assertion fails on purpose, so the
        // defect cannot silently come back.
        using var temp = new TempDirectory();
        var pluginDirectory = temp.Combine("plugins", "natter.sample.greetings");
        Directory.CreateDirectory(pluginDirectory);
        var sourceDirectory = Path.Combine(RepoRoot(), "samples", "Natter.Sample.Plugin.Greetings");
        File.Copy(Path.Combine(sourceDirectory, "plugin.json"), Path.Combine(pluginDirectory, "plugin.json"));
        File.Copy(
            Path.Combine(sourceDirectory, "bin", "Debug", "net10.0", "Natter.Sample.Plugin.Greetings.dll"),
            Path.Combine(pluginDirectory, "Natter.Sample.Plugin.Greetings.dll"));

        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddModule(new Natter.Cli.Core.CliCoreCompositionModule())
            .AddPluginDirectory(pluginDirectory);
        var host = builder.Build();
        await host.StartAsync();

        var registry = host.Services.GetRequiredService<CliCommandRegistry>();
        var catalog = host.Services.GetRequiredService<IPluginCatalog>();
        Assert.True(registry.TryGet("greet", out _), "the plugin command must resolve while activated");
        Assert.Equal(PluginState.Activated, catalog.Plugins.Single().State);

        await host.StopAsync();

        Assert.Equal(PluginState.Stopped, catalog.Plugins.Single().State);
        Assert.False(registry.TryGet("greet", out _), "an unloaded plugin command must not resolve");

        // OBSERVATION (task-43): "the ALC can be collected" is still NOT asserted. Even with the
        // command unregistered, other host singletons may keep plugin types reachable; a
        // WeakReference assertion here would be flaky (or need fake cleanup). task-43 owns it.
        await host.DisposeAsync();
    }

    // ---------------------------------------------------- C4 cycle isolation

    [Fact]
    public void A_dependency_cycle_fails_only_its_members_and_names_them()
    {
        var hostApi = SemanticVersion.Parse("0.1.0");
        var result = NpsPluginGraph.Build(
            [
                Node("natter.cycle.a", dependsOn: "natter.cycle.b"),
                Node("natter.cycle.b", dependsOn: "natter.cycle.a"),
                Node("natter.cycle.standalone", dependsOn: null),
            ],
            hostApi);

        Assert.DoesNotContain("natter.cycle.standalone", result.Errors.Keys);
        Assert.Contains("natter.cycle.a", result.Errors.Keys);
        Assert.Contains("natter.cycle.b", result.Errors.Keys);
        foreach (var id in new[] { "natter.cycle.a", "natter.cycle.b" })
        {
            var error = result.Errors[id];
            Assert.Equal("dependency", error.Phase);
            Assert.Contains("cycle", error.Code, StringComparison.OrdinalIgnoreCase);
            // The error names the cycle, not just "something failed".
            Assert.Contains(
                new[] { "natter.cycle.a", "natter.cycle.b" },
                member => (error.Message ?? string.Empty).Contains(member, StringComparison.Ordinal));
        }

        Assert.Contains("natter.cycle.standalone", result.Order);
    }

    // -------------------------------------------------- E10/E3 CLI and ops

    [Fact]
    public void P2_collect_script_never_puts_a_token_into_argv()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "p2-collect.sh"));

        Assert.DoesNotContain("-H \"Authorization", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-H 'Authorization", script, StringComparison.Ordinal);
        Assert.Contains("-K -", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Degraded_runtime_exits_four_while_a_static_view_does_not()
    {
        var registry = new ChatUpstreamRegistry();
        var upstream = new InMemoryChatUpstream(
            Source.Create("discord", "discord", "bot"),
            Capabilities.TextOnly,
            new TestClock());
        registry.Register(upstream);
        await upstream.ConnectAsync();
        await upstream.DisconnectAsync(); // connected=false → degraded

        var snapshot = await RuntimeHealth.CreateAsync(
            registry,
            new RuntimeProfileStore(null),
            new ChatResponderRegistry(),
            new InMemoryConversationStore(),
            new PluginCatalog(),
            new HostRuntimeClock(),
            new TaskMetrics());

        Assert.Equal("degraded", snapshot.Status);
        Assert.Equal(4, CliHealthExitCodes.Degraded);

        // Static views (no upstream rows / offline) must not be reported as degraded.
        var empty = await RuntimeHealth.CreateAsync(
            new ChatUpstreamRegistry(),
            new RuntimeProfileStore(null),
            new ChatResponderRegistry(),
            new InMemoryConversationStore(),
            new PluginCatalog(),
            new HostRuntimeClock(),
            new TaskMetrics());
        Assert.Equal("unknown", empty.Status);
        Assert.NotEqual(4, CliExitCodes.Success);
    }

    // ------------------------------------------------------- C7 legacy signing

    [Fact]
    public void Legacy_signature_format_is_detectable_and_off_by_default()
    {
        var options = new PluginStoreOptions { RootDirectory = Path.GetTempPath() };
        Assert.False(options.RejectLegacySignatureFormat);

        var files = new List<(string Path, string Hash)> { ("plugin.json", new string('a', 64)) };
        var legacy = PluginPackageSignatureVerifier.BuildLegacyCanonicalText(files);
        var current = PluginPackageSignatureVerifier.BuildCanonicalText(files);

        Assert.NotEqual(legacy, current);
        Assert.Contains("nps-hash-v2", current, StringComparison.Ordinal);
        Assert.DoesNotContain("nps-hash-v2", legacy, StringComparison.Ordinal);
    }

    // --------------------------------------------------------- A20 gate bounds

    [Fact]
    public async Task Conversation_gate_does_not_grow_without_bound_and_survives_dispose()
    {
        var options = new AiResponderOptions
        {
            MaxConcurrentRequests = 4,
            MaxConversationEntries = 64,
            ConversationIdleSeconds = 1,
            GateDisposeTimeoutMilliseconds = 500,
        };
        var gate = new AiConversationGate(options);

        for (var index = 0; index < 2_000; index++)
        {
            using var lease = await gate.AcquireAsync("conversation-" + index);
        }

        Assert.True(
            gate.ConversationCount <= options.MaxConversationEntries,
            $"gate kept {gate.ConversationCount} entries (cap {options.MaxConversationEntries})");

        // Dispose while a lease is in flight must not throw ObjectDisposedException on release.
        var held = await gate.AcquireAsync("long-running");
        var dispose = Task.Run(gate.Dispose);
        await Task.Delay(50);
        var release = Record.Exception(() => held.Dispose());
        await dispose;

        Assert.Null(release);
        Assert.Equal(0, gate.ActiveOperationCount);
    }

    // ------------------------------------------------------------ fixtures

    private static PluginStoreOptions Options(TempDirectory temp, long maxTotalUncompressed = 256L * 1024 * 1024)
        => new()
        {
            RootDirectory = temp.Combine("plugins"),
            Platform = "android",
            AllowUnsigned = true,
            MaxTotalUncompressedBytes = maxTotalUncompressed,
        };

    private static void CreateZip(string path, string entryName, byte[] content)
        => CreateZip(path, [(entryName, content)]);

    private static void CreateZip(string path, IReadOnlyList<(string Name, byte[] Content)> entries)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize).Open();
            entry.Write(content);
        }
    }

    private sealed class CountingHttpHandler : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"errcode\":0}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private static string MinimalManifest(string id, string entryAssembly = "lib/probe.dll")
        => $$"""
        {
          "schemaVersion": 1,
          "id": "{{id}}",
          "name": "S2 bomb probe",
          "version": "1.0.0",
          "kind": "feature",
          "hostApi": ">=0.1.0 <1.0.0",
          "entry": { "assembly": "{{entryAssembly}}", "type": "Probe.Plugin" }
        }
        """;

    /// <summary>Rewrites the declared uncompressed size of one entry in both headers.</summary>
    private static void DeclareSmallUncompressedSize(string path, uint declared, string entryHint)
    {
        var bytes = File.ReadAllBytes(path);
        var hint = Encoding.ASCII.GetBytes(entryHint);
        var patched = 0;

        for (var index = 0; index + 30 < bytes.Length; index++)
        {
            // Local file header: name at +30, uncompressed size at +22.
            if (bytes[index] == 0x50 && bytes[index + 1] == 0x4B && bytes[index + 2] == 0x03 && bytes[index + 3] == 0x04)
            {
                var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(index + 26, 2));
                var name = bytes.AsSpan(index + 30, Math.Min(nameLength, bytes.Length - index - 30));
                if (!name.SequenceEqual(hint))
                    continue;
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index + 22, 4), declared);
                patched++;
            }
            // Central directory header: name at +46, uncompressed size at +24.
            else if (bytes[index] == 0x50 && bytes[index + 1] == 0x4B && bytes[index + 2] == 0x01 && bytes[index + 3] == 0x02)
            {
                var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(index + 28, 2));
                var name = bytes.AsSpan(index + 46, Math.Min(nameLength, bytes.Length - index - 46));
                if (!name.SequenceEqual(hint))
                    continue;
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index + 24, 4), declared);
                patched++;
            }
        }

        Assert.True(patched >= 2, $"zip headers were not patched (patched={patched})");
        File.WriteAllBytes(path, bytes);
    }

    private static long DiskBytes(string directory)
    {
        if (!Directory.Exists(directory))
            return 0;

        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
    }

    private static NpsGraphInput Node(string id, string? dependsOn)
        => new(
            id,
            "1.0.0",
            "feature",
            PluginState.Loaded,
            dependsOn is null ? [] : [new PluginDependency { Id = dependsOn }],
            null,
            null,
            null,
            null,
            null);

    private static MessageCreatedEvent Event(string text)
    {
        var message = new Message(
            "m1",
            new Conversation("c1", ConversationKind.Direct),
            new User("u1", "Alice"),
            new Content(text),
            DateTimeOffset.UtcNow);
        return new MessageCreatedEvent
        {
            Id = "evt:m1",
            Source = Source.Create("discord", "discord", "bot"),
            Sequence = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            ReceivedAt = DateTimeOffset.UtcNow,
            Payload = new MessagePayload(message),
        };
    }

    private static string RepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Natter.slnx")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Natter.slnx not found");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> probe, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await probe())
                return;
            await Task.Delay(20);
        }
        throw new TimeoutException("condition was not met in time");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "natter-s2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class RecordingChatClient : IChatClient
    {
        private int _calls;

        public int CallCount => Volatile.Read(ref _calls);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "reply")]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var message in response.Messages)
                yield return new ChatResponseUpdate(message.Role, message.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class NullPluginLog : IPluginLog
    {
        public void Debug(string message, params object?[] args)
        {
        }

        public void Information(string message, params object?[] args)
        {
        }

        public void Warning(string message, params object?[] args)
        {
        }

        public void Error(Exception? exception, string message, params object?[] args)
        {
        }
    }

    private sealed class RecordingCommandDispatcher : IChatCommandDispatcher
    {
        public List<ChatCommand> Commands { get; } = [];

        public ValueTask<ChatAck> ExecuteAsync(ChatCommand command, CancellationToken cancellationToken = default)
        {
            lock (Commands)
                Commands.Add(command);
            return ValueTask.FromResult(ChatAck.Success(command));
        }
    }

    private sealed class ImmediateWorker(string text) : ITaskWorker
    {
        public ValueTask<TaskResult> ExecuteAsync(AgentTask task, TaskExecutionContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new TaskResult(text));
    }
}
