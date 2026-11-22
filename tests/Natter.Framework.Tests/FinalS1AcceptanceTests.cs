using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Natter.Abstractions.Ports;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Chat.Discord;
using Natter.Chat.Satori;
using Natter.Cli;
using Natter.Cli.Abstractions;
using Natter.Cli.Core;
using Natter.Feature.Ai;
using Natter.Infrastructure.Composition;
using Natter.Infrastructure.Plugins.Nps;
using Natter.Plugin.Abstractions;
using Natter.Plugin.Store;
using Xunit;
using ConversationKey = Natter.Application.Chat.ConversationKey;

namespace Natter.Framework.Tests;

/// <summary>
/// Final independent acceptance for the remaining review items (task-35): S1-05 conversation
/// consistency, S1-06 orchestrator ordering/parallelism/limits/drain, S1-07 task result delivery,
/// S1-03 Satori escaping, S1-11 CLI host startup policy and S1-12 plugin trust. The Discord gateway
/// watchdog (task-36) is verified read-only here with a fake transport.
/// Report: docs/testing/2026-11-22-final-s1-acceptance.md
/// </summary>
public sealed class FinalS1AcceptanceTests
{
    private static readonly IServiceProvider EmptyProvider = new ServiceCollection().BuildServiceProvider();

    // -------------------------------------------------- S1-05 会话一致性

    [Fact]
    public async Task Conversation_gate_serializes_one_key_and_parallelizes_distinct_keys()
    {
        var gate = new AiConversationGate(new AiResponderOptions { MaxConcurrentRequests = 4 });
        var sameKeyPeak = await PeakConcurrencyAsync(
            gate,
            ["conv-a", "conv-a"],
            index => TimeSpan.FromMilliseconds(120));
        var differentKeysPeak = await PeakConcurrencyAsync(
            gate,
            ["conv-a", "conv-b"],
            index => TimeSpan.FromMilliseconds(120));

        Assert.Equal(1, sameKeyPeak);
        Assert.Equal(2, differentKeysPeak);
    }

    [Fact]
    public async Task Concurrent_turns_on_one_conversation_never_read_stale_history()
    {
        var options = new AiResponderOptions { MaxConcurrentRequests = 4, HistoryMaxMessages = 20 };
        var store = new AiConversationStore(options);
        var client = new RecordingChatClient(TimeSpan.FromMilliseconds(80));
        var responder = new AiChatResponder(
            client,
            options,
            store,
            new AiConversationGate(options),
            new ChatCommandFactory(new TestClock()),
            new NullPluginLog());
        var source = Source.Create("discord", "discord", "bot");
        var conversation = new Conversation("c1", ConversationKind.Direct);
        var user = new User("u1", "Alice");

        var first = responder.RespondAsync(Event(source, conversation, user, "first", "m1")).AsTask();
        var second = responder.RespondAsync(Event(source, conversation, user, "second", "m2")).AsTask();
        await Task.WhenAll(first, second);

        var history = await store.GetMessagesAsync(store.GetKey(source, conversation, user.Id));
        var users = history.Where(message => message.Role == ChatRole.User).Select(message => message.Text).ToArray();
        var assistants = history.Where(message => message.Role == ChatRole.Assistant).Select(message => message.Text).ToArray();

        // Each turn is appended exactly once (no duplicated previous turn).
        Assert.Equal(["first", "second"], users);
        Assert.Equal(2, assistants.Length);
        // The second turn must have observed the first turn instead of an empty history.
        Assert.Contains("first", client.FirstTurnSeenBy("second"), StringComparison.Ordinal);
    }

    // -------------------------------------------------- S1-06 编排器

    [Fact]
    public async Task Same_conversation_events_are_processed_in_order()
    {
        var source = new RecordingEventSource();
        var order = new ConcurrentQueue<string>();
        var responder = new HandlerResponder((chatEvent, cancellation) =>
        {
            order.Enqueue(chatEvent.Id);
            return Task.Delay(30, cancellation);
        });
        var orchestrator = new ChatOrchestrator(
            source,
            new NoopDispatcher(),
            responder,
            new ChatOrchestrationOptions { QueueCapacity = 16, WorkerCount = 4, ProcessEditedMessages = true });
        await orchestrator.StartAsync(EmptyProvider);

        source.Publish(MessageEvent("m1", "c1", source.SourceKey));
        source.Publish(UpdatedEvent("m1", "c1", source.SourceKey));
        await WaitUntilAsync(() => Task.FromResult(orchestrator.ProcessedCount == 2), TimeSpan.FromSeconds(5));

        Assert.Equal(["evt:m1:created", "evt:m1:updated"], order.ToArray());
        Assert.Equal(0, orchestrator.FailedCount);
    }

    [Fact]
    public async Task Different_conversations_run_in_parallel()
    {
        var source = new RecordingEventSource();
        var peak = 0;
        var active = 0;
        var responder = new HandlerResponder(async (chatEvent, cancellation) =>
        {
            var current = Interlocked.Increment(ref active);
            InterlockedMax(ref peak, current);
            await Task.Delay(120, cancellation);
            Interlocked.Decrement(ref active);
        });
        var orchestrator = new ChatOrchestrator(
            source,
            new NoopDispatcher(),
            responder,
            new ChatOrchestrationOptions { QueueCapacity = 16, WorkerCount = 4 });
        await orchestrator.StartAsync(EmptyProvider);

        source.Publish(MessageEvent("m1", "c1", source.SourceKey));
        source.Publish(MessageEvent("m2", "c2", source.SourceKey));
        source.Publish(MessageEvent("m3", "c3", source.SourceKey));
        await WaitUntilAsync(() => Task.FromResult(orchestrator.ProcessedCount == 3), TimeSpan.FromSeconds(5));

        Assert.True(peak >= 2, $"different conversations must overlap, peak={peak}");
    }

    [Fact]
    public async Task Bounded_queue_drops_are_counted_and_logged()
    {
        var source = new RecordingEventSource();
        var logs = new CapturingLogger<ChatOrchestrator>();
        var responder = new HandlerResponder((chatEvent, cancellation) => Task.Delay(400, cancellation));
        var orchestrator = new ChatOrchestrator(
            source,
            new NoopDispatcher(),
            responder,
            new ChatOrchestrationOptions { QueueCapacity = 1, WorkerCount = 1 },
            logs);
        await orchestrator.StartAsync(EmptyProvider);

        for (var index = 0; index < 12; index++)
            source.Publish(MessageEvent("m" + index, "c1", source.SourceKey));

        Assert.True(orchestrator.DroppedCount > 0, $"expected drops with capacity 1, enqueued={orchestrator.EnqueuedCount}");
        Assert.Equal(12, orchestrator.EnqueuedCount + orchestrator.DroppedCount);
        Assert.Contains(logs.Entries, entry => entry.Contains("queue full", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StopAsync_drains_in_flight_work_and_handler_failures_are_counted()
    {
        var source = new RecordingEventSource();
        var completed = 0;
        var responder = new HandlerResponder(async (chatEvent, cancellation) =>
        {
            await Task.Delay(200, cancellation);
            if (chatEvent.Id.Contains("boom", StringComparison.Ordinal))
                throw new InvalidOperationException("handler exploded");
            Interlocked.Increment(ref completed);
        });
        using var orchestrator = new ChatOrchestrator(
            source,
            new NoopDispatcher(),
            responder,
            new ChatOrchestrationOptions { QueueCapacity = 16, WorkerCount = 2, DrainTimeout = TimeSpan.FromSeconds(5) });
        await orchestrator.StartAsync(EmptyProvider);

        source.Publish(MessageEvent("m1", "c1", source.SourceKey));
        source.Publish(MessageEvent("m-boom", "c2", source.SourceKey));
        source.Publish(MessageEvent("m3", "c3", source.SourceKey));
        await Task.Delay(50);

        await orchestrator.StopAsync(EmptyProvider);

        Assert.Equal(2, completed);
        Assert.Equal(1, orchestrator.FailedCount);
        Assert.Equal(0, orchestrator.DrainTimeoutCount);
        Assert.Equal(3, orchestrator.ProcessedCount + orchestrator.FailedCount);
    }

    // -------------------------------------------------- S1-07 任务结果发送

    [Fact]
    public async Task Lease_miss_sends_no_message_and_is_counted_as_skipped()
    {
        var store = new LeaseStubStore();
        var recorder = new RecordingCommandDispatcher();
        var task = PendingTask("task-lease");
        await store.CreateAsync(task);
        await using var dispatcher = new TaskDispatcher(
            store, new ImmediateWorker("done"), new FixedProfileResolver(), new RuntimeProfileStore(null),
            recorder, new ChatCommandFactory(new TestClock()), new TaskMetrics());

        await dispatcher.EnqueueAsync(task);
        await WaitUntilAsync(() => Task.FromResult(dispatcher.SkippedResults == 1), TimeSpan.FromSeconds(5));

        Assert.Empty(recorder.Commands);
        Assert.Equal(0, dispatcher.SentResultCount);
    }

    [Fact]
    public async Task Task_advanced_elsewhere_sends_no_duplicate_result()
    {
        var store = new InMemoryTaskStore();
        var recorder = new RecordingCommandDispatcher();
        var task = PendingTask("task-advanced");
        await store.CreateAsync(task);
        await store.TransitionAsync(task.TaskId, AgentTaskStatus.Running, 0);
        await store.TransitionAsync(task.TaskId, AgentTaskStatus.Completed, 1, resultText: "owner result");
        await using var dispatcher = new TaskDispatcher(
            store, new ImmediateWorker("late worker"), new FixedProfileResolver(), new RuntimeProfileStore(null),
            recorder, new ChatCommandFactory(new TestClock()), new TaskMetrics());

        await dispatcher.EnqueueAsync(task);
        await Task.Delay(150);

        Assert.Empty(recorder.Commands);
        Assert.Equal(0, dispatcher.SentResultCount);
        Assert.Equal("owner result", (await store.GetAsync(task.TaskId))!.ResultText);
    }

    [Fact]
    public async Task Genuine_failure_sends_exactly_one_message()
    {
        var store = new InMemoryTaskStore();
        var recorder = new RecordingCommandDispatcher();
        var task = PendingTask("task-fail");
        await store.CreateAsync(task);
        await using var dispatcher = new TaskDispatcher(
            store, new FailingWorker("tool_timeout"), new FixedProfileResolver(), new RuntimeProfileStore(null),
            recorder, new ChatCommandFactory(new TestClock()), new TaskMetrics());

        await dispatcher.EnqueueAsync(task);
        await WaitUntilAsync(() => Task.FromResult(dispatcher.SentResultCount == 1), TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        var sent = Assert.Single(recorder.Commands);
        Assert.Contains("Task failed.", Assert.IsType<MessageSendCommand>(sent).Payload.Content.Text, StringComparison.Ordinal);
        Assert.Equal(AgentTaskStatus.Failed, (await store.GetAsync(task.TaskId))!.Status);
    }

    [Fact]
    public async Task Idempotent_enqueue_still_runs_once_and_sends_once()
    {
        var store = new InMemoryTaskStore();
        var recorder = new RecordingCommandDispatcher();
        var metrics = new TaskMetrics();
        var task = PendingTask("task-idempotent");
        await store.CreateAsync(task);
        await using var dispatcher = new TaskDispatcher(
            store, new ImmediateWorker("done"), new FixedProfileResolver(), new RuntimeProfileStore(null),
            recorder, new ChatCommandFactory(new TestClock()), metrics);

        await Task.WhenAll(
            dispatcher.EnqueueAsync(task).AsTask(),
            dispatcher.EnqueueAsync(task).AsTask(),
            dispatcher.EnqueueAsync(task).AsTask());
        await WaitUntilAsync(() => Task.FromResult(dispatcher.SentResultCount == 1), TimeSpan.FromSeconds(5));
        await Task.Delay(150);

        Assert.Single(recorder.Commands);
        Assert.Equal(1, metrics.Snapshot().Created);
        Assert.Equal(1, metrics.Snapshot().Completed);
        Assert.Equal(0, metrics.Snapshot().Failed);
        Assert.Equal(0, metrics.Snapshot().Active);
    }

    // -------------------------------------------------- S1-03 Satori 转义

    [Fact]
    public void At_all_from_the_model_is_escaped_and_produces_no_mention()
    {
        var content = new Content("ping <at type=\"all\"/> everyone");

        var elements = SatoriOutboundContent.Build(content);
        var serialized = SatoriOutboundContent.Format(content);

        var text = Assert.IsType<SatoriTextElement>(Assert.Single(elements));
        Assert.Equal("ping <at type=\"all\"/> everyone", text.Text);
        // Only the tag delimiters are escaped (attributes inside plain text are inert), so the
        // output can never be parsed as an <at> element.
        Assert.Contains("&lt;at type=", serialized, StringComparison.Ordinal);
        Assert.Contains("/&gt;", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("<at ", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Img_br_and_ampersand_are_escaped()
    {
        var content = new Content("<img src=x onerror=alert(1)><br>a & b");

        var serialized = SatoriOutboundContent.Format(content);

        Assert.DoesNotContain("<img", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("<br", serialized, StringComparison.Ordinal);
        Assert.Contains("&lt;img", serialized, StringComparison.Ordinal);
        Assert.Contains("&lt;br", serialized, StringComparison.Ordinal);
        Assert.Contains("&amp;", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Structured_mentions_survive_and_plain_text_is_byte_identical()
    {
        const string plain = "Hello world — 你好, 100% & more; no markup at all.";
        var baseline = SHA256.HashData(Encoding.UTF8.GetBytes(plain));
        Assert.Equal(
            Convert.ToHexString(baseline),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                Assert.IsType<SatoriTextElement>(Assert.Single(SatoriOutboundContent.Build(new Content(plain)))).Text))));

        var mentioned = new Content("hi ", Mentions: [new Mention(MentionKind.User, "123", "Bob")]);
        var elements = SatoriOutboundContent.Build(mentioned);
        Assert.Contains(elements, element => element is SatoriMentionElement);
        Assert.Contains(
            elements.OfType<SatoriMentionElement>(),
            element => element.Mention.Id == "123");
    }

    // -------------------------------------------------- S1-11 CLI 启动策略

    [Theory]
    [InlineData("plugins", "list")]
    [InlineData("doctor")]
    [InlineData("plugins", "config", "validate")]
    [InlineData("version")]
    [InlineData("plugins", "store", "list")]
    public void Read_only_commands_plan_without_host_or_instance_lock(params string[] args)
    {
        var plan = CliHostStartupPlanner.Plan(args, NewCatalog(), new NatterCliOptions());

        Assert.NotEqual(CliHostRequirement.Runtime, plan.Requirement);
        Assert.False(plan.StartHost);
        Assert.False(plan.TakeInstanceLock);
        Assert.False(plan.Degraded);
    }

    [Theory]
    [InlineData("serve")]
    [InlineData("health")]
    [InlineData("upstreams")]
    [InlineData("plugins", "graph")]
    [InlineData("events", "watch")]
    public void Host_commands_plan_with_host_and_instance_lock(params string[] args)
    {
        var plan = CliHostStartupPlanner.Plan(args, NewCatalog(), new NatterCliOptions());

        Assert.Equal(CliHostRequirement.Runtime, plan.Requirement);
        Assert.True(plan.StartHost);
        Assert.True(plan.TakeInstanceLock);
        Assert.False(plan.Degraded);
    }

    [Fact]
    public void Offline_and_no_host_flags_degrade_with_an_explicit_note()
    {
        var options = new NatterCliOptions();
        var offline = CliHostStartupPlanner.Plan(["health", "--offline"], NewCatalog(), options);
        var noHost = CliHostStartupPlanner.Plan(["upstreams", "--no-host"], NewCatalog(), options);

        foreach (var plan in new[] { offline, noHost })
        {
            Assert.False(plan.StartHost);
            Assert.False(plan.TakeInstanceLock);
            Assert.True(plan.Degraded);
            Assert.NotNull(plan.Note);
            Assert.Contains("running without one", plan.Note!, StringComparison.Ordinal);
            Assert.Contains("live state will be unavailable", plan.Note!, StringComparison.Ordinal);
        }

        Assert.Contains("--offline", offline.Note!, StringComparison.Ordinal);
        Assert.Contains("--no-host", noHost.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_only_commands_do_not_start_a_host_while_serve_holds_the_lock()
    {
        using var state = new TempDirectory();
        using var serveLock = new FileStream(
            Path.Combine(state.Path, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var hostStarts = 0;
        var console = new StringConsole();
        var options = new NatterCliOptions
        {
            LoadPlugins = false,
            StateDirectory = state.Path,
            ConfigDirectory = state.Path,
            OnHostStartedAsync = _ =>
            {
                Interlocked.Increment(ref hostStarts);
                return ValueTask.CompletedTask;
            },
        };

        var readOnlyExit = await NatterCli.RunAsync(["plugins", "list", "--json"], console, options);
        Assert.Equal(CliExitCodes.Success, readOnlyExit);
        Assert.Equal(0, hostStarts);

        var runtimeConsole = new StringConsole();
        var runtimeExit = await NatterCli.RunAsync(["upstreams", "--json"], runtimeConsole, options);
        Assert.Equal(CliHostStartupCodes.HostBusy, runtimeExit);
        Assert.Equal(0, hostStarts);
        Assert.Contains("HL-CLI-HOST-BUSY", runtimeConsole.ErrorText, StringComparison.Ordinal);
        Assert.Contains("--no-host", runtimeConsole.ErrorText, StringComparison.Ordinal);
    }

    // -------------------------------------------------- S1-12 插件信任

    [Fact]
    public void Trust_matrix_covers_signed_unsigned_require_signed_and_tampered()
    {
        var options = new NpsPluginOptions { FilePluginTrustLevel = PluginTrustLevel.Local };
        var strict = new PluginTrustOptions { RequireSignedPlugins = true };
        var relaxed = new PluginTrustOptions();

        var signed = PluginTrustPolicy.Evaluate(options, strict,
            new PluginTrustRegistration("/p/signed", PluginSignatureState.Verified, "key-1"));
        Assert.True(signed.Allowed);
        Assert.True(signed.SignatureVerified);
        Assert.Equal(PluginTrustLevel.Signed, signed.Trust);

        var unsignedStrict = PluginTrustPolicy.Evaluate(options, strict,
            new PluginTrustRegistration("/p/unsigned", PluginSignatureState.Unverified));
        Assert.False(unsignedStrict.Allowed);
        Assert.Equal(PluginTrustCodes.UnsignedRejected, unsignedStrict.Code);

        var unsignedRelaxed = PluginTrustPolicy.Evaluate(options, relaxed,
            new PluginTrustRegistration("/p/unsigned", PluginSignatureState.Unverified));
        Assert.True(unsignedRelaxed.Allowed);
        Assert.False(unsignedRelaxed.SignatureVerified);
        Assert.Equal(PluginTrustCodes.IntegrityUnverified, unsignedRelaxed.Code);
        Assert.NotEmpty(unsignedRelaxed.Warnings);

        foreach (var requireSigned in new[] { true, false })
        {
            var tampered = PluginTrustPolicy.Evaluate(
                options,
                new PluginTrustOptions { RequireSignedPlugins = requireSigned },
                new PluginTrustRegistration("/p/tampered", PluginSignatureState.Tampered, Detail: "hash mismatch"));
            Assert.False(tampered.Allowed);
            Assert.Equal(PluginTrustCodes.Tampered, tampered.Code);
        }
    }

    [Fact]
    public void Tampered_installed_package_is_reported_as_a_hash_mismatch()
    {
        using var temp = new TempDirectory();
        var dll = Path.Combine(temp.Path, "plugin.dll");
        File.WriteAllText(dll, "original bytes");
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll))).ToLowerInvariant();
        File.WriteAllText(Path.Combine(temp.Path, "package.sha256"), $"{hash}  plugin.dll\n");

        var verified = InstalledPluginIntegrityCodes.Verify(temp.Path);
        Assert.Equal(InstalledPluginIntegrity.Verified, verified.Integrity);
        Assert.Equal(InstalledPluginIntegrityCodes.Verified, verified.Code);

        File.WriteAllText(dll, "tampered bytes");
        var tampered = InstalledPluginIntegrityCodes.Verify(temp.Path);
        Assert.NotEqual(InstalledPluginIntegrity.Verified, tampered.Integrity);
        Assert.Equal(InstalledPluginIntegrityCodes.Mismatch, tampered.Code);
    }

    [Fact]
    public void Default_trust_options_keep_unverified_packages_loadable_but_labelled()
    {
        var options = new NpsPluginOptions { FilePluginTrustLevel = PluginTrustLevel.Local };
        var decision = PluginTrustPolicy.Evaluate(
            options,
            new PluginTrustOptions(),
            new PluginTrustRegistration("/p/default", PluginSignatureState.Unverified, Detail: "no signature"));

        Assert.True(decision.Allowed);
        Assert.False(decision.SignatureVerified);
        Assert.Equal("local(unverified)", PluginTrustPolicy.TrustLabel(decision));
        Assert.Contains(decision.Warnings, warning => warning.StartsWith(PluginTrustCodes.IntegrityUnverified, StringComparison.Ordinal));

        var missing = InstalledPluginIntegrityCodes.Verify(Path.Combine(Path.GetTempPath(), "natter-review-missing-" + Guid.NewGuid().ToString("N")));
        Assert.Equal(InstalledPluginIntegrity.Unverified, missing.Integrity);
        Assert.Equal(InstalledPluginIntegrityCodes.ManifestMissing, missing.Code);
    }

    // ------------------------------------- task-36 网关看门狗（fake transport）

    [Fact]
    public async Task Watchdog_rebuilds_after_disconnect_and_counts_triggers()
    {
        var transport = new FakeDiscordTransport();
        using var upstream = NewUpstream(transport, maxAttempts: 3);
        await upstream.ConnectAsync();
        Assert.True(upstream.IsConnected);

        transport.Disconnect();
        await WaitUntilAsync(() => Task.FromResult(upstream.WatchdogTriggerCount >= 1), TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => Task.FromResult(upstream.IsConnected), TimeSpan.FromSeconds(5));

        Assert.True(transport.ConnectCount >= 2, $"transport must be rebuilt, connects={transport.ConnectCount}");
        Assert.Equal(0, upstream.ReconnectAttemptCount);
    }

    [Fact]
    public async Task Reconnect_backoff_requests_a_growing_delay_sequence()
    {
        // Flake fix (task-44): the watchdog computes NextReconnectAt from the injected IClock, so the
        // requested delays can be read back exactly. Never compare wall-clock deltas here: under a
        // Release build with parallel collections the scheduling jitter made "second > first"
        // flaky. The only real-time wait left is the watchdog tick, which is polled generously.
        var clock = new ControllableClock(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));
        var transport = new FakeDiscordTransport { FailConnectsAfterDisconnect = true };
        using var upstream = NewUpstream(transport, maxAttempts: 3, clock: clock);
        await upstream.ConnectAsync();
        transport.Disconnect();

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => Task.FromResult(upstream.ReconnectAttemptCount >= 1), TimeSpan.FromSeconds(10));
        var first = upstream.NextReconnectAt!.Value - clock.UtcNow;

        clock.Advance(TimeSpan.FromMilliseconds(90));
        await WaitUntilAsync(() => Task.FromResult(upstream.ReconnectAttemptCount >= 2), TimeSpan.FromSeconds(10));
        var second = upstream.NextReconnectAt!.Value - clock.UtcNow;

        // Exact requested delays: 80ms then 160ms (initial=80, capped shift by attempt).
        Assert.Equal(80d, first.TotalMilliseconds, precision: 3);
        Assert.Equal(160d, second.TotalMilliseconds, precision: 3);
        Assert.True(second > first, $"requested delay must grow: first={first}, second={second}");
    }

    [Fact]
    public async Task Watchdog_stops_after_max_attempts_and_requires_manual_intervention()
    {
        var transport = new FakeDiscordTransport { FailConnectsAfterDisconnect = true };
        using var upstream = NewUpstream(transport, maxAttempts: 2);
        await upstream.ConnectAsync();
        transport.Disconnect();

        await WaitUntilAsync(() => Task.FromResult(upstream.ReconnectExhausted), TimeSpan.FromSeconds(10));
        var attemptsAtExhaustion = upstream.ReconnectAttemptCount;
        await Task.Delay(300);

        Assert.True(upstream.RequiresManualIntervention);
        Assert.Equal(2, attemptsAtExhaustion);
        Assert.Equal(attemptsAtExhaustion, upstream.ReconnectAttemptCount);
        Assert.Equal(2, upstream.WatchdogTriggerCount);
    }

    [Fact]
    public async Task Disconnect_clears_the_watchdog_task_and_logs_never_contain_tokens()
    {
        var logs = new CapturingLoggerFactory();
        var transport = new FakeDiscordTransport { FailConnectsAfterDisconnect = true };
        var upstream = NewUpstream(transport, maxAttempts: 3, loggerFactory: logs);
        await upstream.ConnectAsync();
        transport.Disconnect();
        await WaitUntilAsync(() => Task.FromResult(upstream.ReconnectAttemptCount >= 1), TimeSpan.FromSeconds(5));

        await upstream.DisconnectAsync();

        Assert.Null(upstream.WatchdogTask);
        var text = string.Join("\n", logs.Entries);
        Assert.Contains("watchdog", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lastCode", text, StringComparison.Ordinal);
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bot ", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ fixtures

    private static CliHostRequirementCatalog NewCatalog()
        => new([new DefaultCliHostRequirements()]);

    private static DiscordChatUpstream NewUpstream(
        FakeDiscordTransport transport,
        int maxAttempts,
        CapturingLoggerFactory? loggerFactory = null,
        IClock? clock = null)
        => new(
            Source.Create("discord", "discord", "bot"),
            transport,
            new DiscordOptions
            {
                Token = "fake-token-not-used",
                ReconnectWatchdog = true,
                ReconnectWatchdogThresholdMs = 30,
                ReconnectWatchdogTickMs = 10,
                ReconnectBackoffInitialMs = 80,
                ReconnectBackoffMaxMs = 2_000,
                ReconnectMaxAttempts = maxAttempts,
                ReadyTimeoutSeconds = 3,
            },
            clock ?? new SystemClock(),
            loggerFactory);

    /// <summary>Deterministic clock: only test code moves it, so delay assertions cannot flake.</summary>
    private sealed class ControllableClock(DateTimeOffset start) : IClock
    {
        private DateTimeOffset _now = start;

        public DateTimeOffset UtcNow => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    private static async Task<int> PeakConcurrencyAsync(
        AiConversationGate gate,
        IReadOnlyList<string> keys,
        Func<int, TimeSpan> delay)
    {
        var active = 0;
        var peak = 0;
        var tasks = keys.Select((key, index) => Task.Run(async () =>
        {
            using var lease = await gate.AcquireAsync(key);
            var current = Interlocked.Increment(ref active);
            InterlockedMax(ref peak, current);
            await Task.Delay(delay(index));
            Interlocked.Decrement(ref active);
        })).ToArray();
        await Task.WhenAll(tasks);
        return peak;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var previous = Interlocked.CompareExchange(ref target, value, current);
            if (previous == current)
                return;
            current = previous;
        }
    }

    private static AgentTask PendingTask(string id)
        => new()
        {
            TaskId = id,
            SessionId = "session",
            Source = Source.Create("discord", "discord", "bot"),
            ConversationId = "c1",
            ActorId = "u1",
            Prompt = "do it",
            Status = AgentTaskStatus.Pending,
        };

    private static MessageCreatedEvent MessageEvent(string id, string conversationId, string sourceKey)
    {
        var parts = sourceKey.Split(':');
        var source = Source.Create(parts[0], parts[1], parts[2]);
        var message = new Message(
            id,
            new Conversation(conversationId, ConversationKind.Direct),
            new User("u1", "Alice"),
            new Content("hello " + id),
            DateTimeOffset.UtcNow);
        return new MessageCreatedEvent
        {
            Id = "evt:" + id + ":created",
            Source = source,
            Sequence = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            ReceivedAt = DateTimeOffset.UtcNow,
            Payload = new MessagePayload(message),
        };
    }

    private static MessageUpdatedEvent UpdatedEvent(string id, string conversationId, string sourceKey)
    {
        var parts = sourceKey.Split(':');
        var source = Source.Create(parts[0], parts[1], parts[2]);
        var message = new Message(
            id,
            new Conversation(conversationId, ConversationKind.Direct),
            new User("u1", "Alice"),
            new Content("edited " + id),
            DateTimeOffset.UtcNow);
        return new MessageUpdatedEvent
        {
            Id = "evt:" + id + ":updated",
            Source = source,
            Sequence = 2,
            OccurredAt = DateTimeOffset.UtcNow,
            ReceivedAt = DateTimeOffset.UtcNow,
            Payload = new MessagePayload(message),
        };
    }

    private static MessageCreatedEvent Event(Source source, Conversation conversation, User user, string text, string id)
    {
        var message = new Message(id, conversation, user, new Content(text), DateTimeOffset.UtcNow);
        return new MessageCreatedEvent
        {
            Id = "evt:" + id,
            Source = source,
            Sequence = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            ReceivedAt = DateTimeOffset.UtcNow,
            Payload = new MessagePayload(message),
        };
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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "natter-final-s1-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

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

    private sealed class SystemClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private sealed class RecordingEventSource : IChatEventSource
    {
        private readonly List<IObserver<ChatEvent>> _observers = [];

        public string SourceKey => "discord:discord:bot";

        public IObservable<ChatEvent> Events => new Observable(_observers);

        public void Publish(ChatEvent chatEvent)
        {
            IObserver<ChatEvent>[] observers;
            lock (_observers)
                observers = _observers.ToArray();
            foreach (var observer in observers)
                observer.OnNext(chatEvent);
        }

        private sealed class Observable(List<IObserver<ChatEvent>> observers) : IObservable<ChatEvent>
        {
            public IDisposable Subscribe(IObserver<ChatEvent> observer)
            {
                lock (observers)
                    observers.Add(observer);
                return new Subscription(observer, observers);
            }
        }

        private sealed class Subscription(IObserver<ChatEvent> observer, List<IObserver<ChatEvent>> observers) : IDisposable
        {
            public void Dispose()
            {
                lock (observers)
                    observers.Remove(observer);
            }
        }
    }

    private sealed class HandlerResponder(Func<ChatEvent, CancellationToken, Task> handler) : IChatResponder
    {
        public async ValueTask<ChatCommand?> RespondAsync(ChatEvent chatEvent, CancellationToken cancellationToken = default)
        {
            await handler(chatEvent, cancellationToken);
            return null;
        }
    }

    private sealed class NoopDispatcher : IChatCommandDispatcher
    {
        public ValueTask<ChatAck> ExecuteAsync(ChatCommand command, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ChatAck.Success(command));
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

    private sealed class FixedProfileResolver : IProfileResolver
    {
        public RuntimeProfile? Resolve(Source source, Conversation conversation, User sender)
            => new() { Id = "qa", Ai = new ProfileAi { Provider = "qa#model" } };
    }

    private sealed class ImmediateWorker(string text) : ITaskWorker
    {
        public ValueTask<TaskResult> ExecuteAsync(AgentTask task, TaskExecutionContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new TaskResult(text));
    }

    private sealed class FailingWorker(string code) : ITaskWorker
    {
        public ValueTask<TaskResult> ExecuteAsync(AgentTask task, TaskExecutionContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new TaskResult(null, code, "tool failed"));
    }

    /// <summary>Store whose lease is always refused, to prove a lease miss sends nothing.</summary>
    private sealed class LeaseStubStore : ITaskStore
    {
        private readonly InMemoryTaskStore _inner = new();

        public ValueTask<AgentTask> CreateAsync(AgentTask task, CancellationToken cancellationToken = default)
            => _inner.CreateAsync(task, cancellationToken);

        public ValueTask<AgentTask?> GetAsync(string taskId, CancellationToken cancellationToken = default)
            => _inner.GetAsync(taskId, cancellationToken);

        public ValueTask<IReadOnlyList<AgentTask>> ListActiveAsync(string sessionId, CancellationToken cancellationToken = default)
            => _inner.ListActiveAsync(sessionId, cancellationToken);

        public ValueTask<IReadOnlyList<AgentTask>> ListRecoverableAsync(CancellationToken cancellationToken = default)
            => _inner.ListRecoverableAsync(cancellationToken);

        public ValueTask<bool> TryAcquireLeaseAsync(string taskId, string owner, TimeSpan ttl, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public ValueTask<AgentTask> TransitionAsync(
            string taskId, AgentTaskStatus next, int expectedVersion, string? resultText = null,
            string? errorCode = null, string? errorMessage = null, CancellationToken cancellationToken = default)
            => _inner.TransitionAsync(taskId, next, expectedVersion, resultText, errorCode, errorMessage, cancellationToken);

        public ValueTask<IReadOnlyList<AgentTask>> ClaimExpiredLeasesAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
            => _inner.ClaimExpiredLeasesAsync(now, cancellationToken);

        public ValueTask SaveResultAsync(string taskId, string resultText, CancellationToken cancellationToken = default)
            => _inner.SaveResultAsync(taskId, resultText, cancellationToken);

        public ValueTask<IReadOnlyList<TaskEvent>> GetEventsAsync(string taskId, CancellationToken cancellationToken = default)
            => _inner.GetEventsAsync(taskId, cancellationToken);

        public ValueTask AppendEventAsync(string taskId, string kind, string? payloadJson, CancellationToken cancellationToken = default)
            => _inner.AppendEventAsync(taskId, kind, payloadJson, cancellationToken);
    }

    /// <summary>Chat client that records the history it was given per user turn.</summary>
    private sealed class RecordingChatClient(TimeSpan delay) : IChatClient
    {
        private readonly ConcurrentQueue<(string Prompt, string History)> _calls = new();

        public IReadOnlyList<(string Prompt, string History)> Calls => _calls.ToArray();

        public string FirstTurnSeenBy(string prompt)
            => Calls.FirstOrDefault(call => call.Prompt == prompt).History ?? string.Empty;

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var list = messages.ToArray();
            var lastUser = list.LastOrDefault(message => message.Role == ChatRole.User)?.Text ?? string.Empty;
            var history = string.Join("|", list.Where(message => message.Role != ChatRole.System).Select(message => message.Text));
            _calls.Enqueue((lastUser, history));
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return new ChatResponse([new ChatMessage(ChatRole.Assistant, "reply-" + lastUser)]);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            foreach (var message in response.Messages)
                yield return new ChatResponseUpdate(message.Role, message.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class NullPluginLog : Natter.Plugin.Abstractions.IPluginLog
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

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
                Entries.Add(formatter(state, exception));
        }
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public List<string> Entries { get; } = [];

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger<object>(Entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger<T>(List<string> entries) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
                => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (entries)
                    entries.Add(formatter(state, exception));
            }
        }
    }

    /// <summary>Fake Discord transport: no network, scripted connect failures.</summary>
    private sealed class FakeDiscordTransport : IDiscordTransport
    {
        private readonly System.Reactive.Subjects.Subject<DiscordTransportEvent> _events = new();
        private int _connectCount;
        private bool _disconnected;

        public bool FailConnectsAfterDisconnect { get; set; }

        public int ConnectFailures { get; set; }

        public int ConnectCount => Volatile.Read(ref _connectCount);

        public IObservable<DiscordTransportEvent> Events => _events;

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _connectCount);
            if (FailConnectsAfterDisconnect && Volatile.Read(ref _disconnected))
                throw new InvalidOperationException("fake gateway refused the connection");

            _events.OnNext(new DiscordTransportEvent { Kind = DiscordTransportEventKind.Ready, SelfId = "bot", SelfName = "Bot" });
            return ValueTask.CompletedTask;
        }

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
        {
            _events.OnNext(new DiscordTransportEvent
            {
                Kind = DiscordTransportEventKind.Disconnected,
                ErrorCode = "4000",
            });
            return ValueTask.CompletedTask;
        }

        public void Disconnect()
        {
            Volatile.Write(ref _disconnected, true);
            _events.OnNext(new DiscordTransportEvent
            {
                Kind = DiscordTransportEventKind.Disconnected,
                ErrorCode = "1006",
            });
        }

        public ValueTask<DiscordSendResult> SendMessageAsync(DiscordSendMessage request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(DiscordSendResult.Success("m1"));

        public ValueTask<DiscordSendResult> UpdateMessageAsync(DiscordUpdateMessage request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(DiscordSendResult.Success("m1"));

        public ValueTask<DiscordSendResult> DeleteMessageAsync(DiscordDeleteMessage request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(DiscordSendResult.Success("m1"));

        public ValueTask TriggerTypingAsync(DiscordConversationRef conversation, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<DiscordSendResult> RespondInteractionAsync(DiscordInteractionResponse response, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(DiscordSendResult.Success("m1"));
    }
}
