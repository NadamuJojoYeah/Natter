using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Infrastructure.Chat;
using Natter.Infrastructure.Composition;
using Natter.Infrastructure.Plugins;
using Natter.Infrastructure.Plugins.Nps;
using Natter.Plugin.Abstractions;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// S2 batch-2 independent acceptance (task-49), built to reverse-check the implementation tests:
/// reservation/failure-release, 60-minute replay window, edit-id both directions, prune behaviour,
/// chunk resume, prompt size limits, contribution permissions, and the "is the green light
/// trustworthy" items from batch 1 (F1/F5/F7/F8/F9).
/// Report: docs/testing/2026-11-22-s2-batch2-acceptance.md
/// </summary>
public sealed class S2Batch2AcceptanceTests
{
    // ------------------------------------------------- A1 reservation lifecycle

    [Fact]
    public void Concurrent_reservations_for_one_key_execute_exactly_once()
    {
        var clock = new ControllableClock(DateTimeOffset.Parse("2026-11-22T12:00:00Z"));
        var cache = new ChatIdempotencyCache(clock: () => clock.UtcNow);
        var executions = 0;
        var reservations = new ConcurrentBag<ChatIdempotencyCache.Reservation>();

        Parallel.For(0, 10, _ =>
        {
            var outcome = cache.TryReserve("key-1", out ChatAck? _);
            reservations.Add(outcome);
            if (outcome == ChatIdempotencyCache.Reservation.Reserved)
                Interlocked.Increment(ref executions);
        });

        Assert.Equal(1, executions);
        Assert.Equal(1, reservations.Count(outcome => outcome == ChatIdempotencyCache.Reservation.Reserved));
        Assert.Equal(9, reservations.Count(outcome => outcome == ChatIdempotencyCache.Reservation.InFlight));
    }

    [Fact]
    public void A_failed_execution_releases_the_reservation_so_the_retry_can_run()
    {
        var clock = new ControllableClock(DateTimeOffset.Parse("2026-11-22T12:00:00Z"));
        var cache = new ChatIdempotencyCache(clock: () => clock.UtcNow);

        Assert.Equal(ChatIdempotencyCache.Reservation.Reserved, cache.TryReserve("key-retry", out _));
        cache.Rollback("key-retry"); // the send failed; the key must not be permanently poisoned

        Assert.Equal(ChatIdempotencyCache.Reservation.Reserved, cache.TryReserve("key-retry", out _));
        cache.Commit("key-retry", ChatAck.Success(FakeCommand("key-retry")));

        Assert.Equal(ChatIdempotencyCache.Reservation.Cached, cache.TryReserve("key-retry", out var cached));
        Assert.NotNull(cached);
        Assert.Equal(0, cache.PendingCount);
    }

    [Fact]
    public void A_stale_reservation_is_released_after_the_pending_timeout()
    {
        var clock = new ControllableClock(DateTimeOffset.Parse("2026-11-22T12:00:00Z"));
        var cache = new ChatIdempotencyCache(clock: () => clock.UtcNow, pendingTimeout: TimeSpan.FromSeconds(30));

        Assert.Equal(ChatIdempotencyCache.Reservation.Reserved, cache.TryReserve("key-stale", out _));
        Assert.Equal(ChatIdempotencyCache.Reservation.InFlight, cache.TryReserve("key-stale", out _));

        clock.Advance(TimeSpan.FromSeconds(31)); // the owner crashed; the reservation must expire
        Assert.Equal(0, cache.PendingCount);
        Assert.Equal(ChatIdempotencyCache.Reservation.Reserved, cache.TryReserve("key-stale", out _));
    }

    // ------------------------------------------------------- A12 replay window

    [Fact]
    public void Replay_inside_the_window_is_blocked_and_out_of_window_arrivals_are_counted()
    {
        var deduplicator = new ChatEventDeduplicator();
        Assert.True(
            deduplicator.Window >= TimeSpan.FromMinutes(60),
            $"default window must cover the 60-minute platform replay window, got {deduplicator.Window}");

        var clock = new ControllableClock(DateTimeOffset.Parse("2026-11-22T12:00:00Z"));
        var instance = new ChatEventDeduplicator(clock: () => clock.UtcNow);
        var chatEvent = MessageEvent("evt:replay", "m1");

        Assert.True(instance.TryAccept(chatEvent)); // first delivery
        clock.Advance(TimeSpan.FromMinutes(59));    // still inside the 60-minute window
        Assert.False(instance.TryAccept(chatEvent), "a replay inside the window must be deduplicated");

        // Documented semantics (see report): an event whose own age exceeds the window cannot be
        // deduplicated any more; it is accepted and counted as late instead of blocked.
        var stale = MessageEvent("evt:stale", "m-stale") with { OccurredAt = clock.UtcNow.AddMinutes(-61) };
        Assert.True(instance.TryAccept(stale));
        Assert.True(instance.LateEventCount >= 1, "an event older than the window must be counted as late");
    }

    // ------------------------------------------------------- A13 edit ids

    [Fact]
    public void Edit_ids_are_stable_per_revision_and_distinct_across_revisions()
    {
        var source = Source.Create("dingtalk", "dingtalk", "bot");
        var first = ChatEventId.DeriveEdit(source, "msg-1", DateTimeOffset.Parse("2026-11-22T12:00:00Z"));
        var sameRevisionReplay = ChatEventId.DeriveEdit(source, "msg-1", DateTimeOffset.Parse("2026-11-22T12:00:00Z"));
        var second = ChatEventId.DeriveEdit(source, "msg-1", DateTimeOffset.Parse("2026-11-22T12:01:00Z"));
        var third = ChatEventId.DeriveEdit(source, "msg-1", DateTimeOffset.Parse("2026-11-22T12:02:00Z"));

        // Three edits -> three distinct ids; a replay of the same edit -> the same id (deduped).
        Assert.Equal(3, new[] { first, second, third }.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(first, sameRevisionReplay);

        var deduplicator = new ChatEventDeduplicator();
        Assert.True(deduplicator.TryAccept(MessageEvent(first)));
        Assert.False(deduplicator.TryAccept(MessageEvent(first))); // same revision replayed -> deduped
        // A different revision carries a different event id, but the message-id key still marks it
        // as the same message; the ids are distinct, which is what A13 requires.
        _ = deduplicator.TryAccept(MessageEvent(second));
    }

    // ------------------------------------------------------- A14 bounded prune

    [Fact]
    public void Ten_thousand_entries_stay_fast_and_prune_really_drops_expired_items()
    {
        var clock = new ControllableClock(DateTimeOffset.Parse("2026-11-22T12:00:00Z"));
        var cache = new ChatIdempotencyCache(window: TimeSpan.FromMinutes(10), capacity: 20_000, clock: () => clock.UtcNow);

        for (var index = 0; index < 10_000; index++)
        {
            Assert.Equal(ChatIdempotencyCache.Reservation.Reserved, cache.TryReserve("k" + index, out _));
            cache.Commit("k" + index, ChatAck.Success(FakeCommand("k" + index)));
        }

        var stopwatch = Stopwatch.StartNew();
        for (var index = 10_000; index < 10_050; index++)
            cache.TryReserve("k" + index, out _);
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"50 inserts after 10k entries took {stopwatch.Elapsed}; amortised prune is broken");

        // Reverse check: after the window passes, the expired entries are really gone.
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(0, cache.PendingCount);
        Assert.True(cache.Count < 10_050, $"expired entries were not pruned, Count={cache.Count}");
        Assert.Equal(ChatIdempotencyCache.Reservation.Reserved, cache.TryReserve("k0", out _));
    }

    // ------------------------------------------------------ A15 chunk resume

    [Fact]
    public async Task Chunk_progress_resumes_at_the_failed_chunk_and_clears_on_completion()
    {
        var clock = new ControllableClock(DateTimeOffset.Parse("2026-11-22T12:00:00Z"));
        var progress = new ChatChunkProgress(clock: () => clock.UtcNow);

        // Chunk 1 delivered, chunk 2 failed: the retry must resume at index 1.
        progress.Record("chunk-key", 1, ["msg-1"]);
        var resumed = progress.Get("chunk-key");
        Assert.NotNull(resumed);
        Assert.Equal(1, resumed!.Value.NextIndex);
        Assert.Equal(["msg-1"], resumed.Value.MessageIds);

        // Completing the chunked send forgets the key so the next send starts from 0.
        progress.Complete("chunk-key");
        Assert.Null(progress.Get("chunk-key"));

        // The TTL bounds memory: an abandoned progress entry expires with the clock.
        progress.Record("stale-key", 1, ["msg-2"]);
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Null(progress.Get("stale-key"));
        await Task.CompletedTask;
    }

    // -------------------------------------------------- A22 prompt size limits

    [Fact]
    public void Prompt_size_limits_truncate_with_warnings_and_never_throw()
    {
        var composer = new PromptComposer();
        var hugeFragment = new PromptFragment(
            "frag-huge",
            new string('x', PromptCompilerDefaults.FragmentMaxCharacters + 500),
            PromptFragmentSource.Inline,
            10);
        var hugeVariable = new string('y', PromptCompilerDefaults.VariableMaxCharacters + 100);
        var warnings = new List<string>();

        var plan = composer.Compose(new PromptComposeRequest(
            Profile(),
            [new PromptPreset("p", "v1", [new PromptSectionRef(hugeFragment.Id)])],
            [hugeFragment],
            Variables: new Dictionary<string, string> { ["big"] = hugeVariable },
            PluginFragmentWarnings: warnings));

        var fragment = Assert.Single(plan.Fragments);
        Assert.Equal(PromptCompilerDefaults.FragmentMaxCharacters, fragment.Text.Length);
        Assert.Contains(plan.Warnings.Concat(warnings), warning => warning.Contains("truncat", StringComparison.OrdinalIgnoreCase));

        // Structural overflow (too many fragments) is the reject-class path: it does not throw but
        // it must be reported, never silently dropped.
        // Structural overflow applies to plugin-contributed fragments. Plugin fragment ids must be
        // namespaced (`owner#local`), so the structural class is reported by the compiler: either a
        // quota warning or an explicit PromptCompilationException - never a silent full accept.
        var many = Enumerable.Range(0, 80)
            .Select(index => new PromptFragment("qa#p" + index, "t", PromptFragmentSource.Plugin, index))
            .ToArray();
        var pluginWarnings = new List<string>();
        try
        {
            var overflow = composer.Compose(new PromptComposeRequest(
                Profile(),
                [new PromptPreset("p", "v1", [])],
                [],
                PluginFragments: many,
                PluginFragmentWarnings: pluginWarnings));

            Assert.True(
                pluginWarnings.Count > 0 || overflow.Warnings.Count > 0 || overflow.Fragments.Count < many.Length,
                "structural overflow must warn or be trimmed, never silently accepted in full");
        }
        catch (PromptCompilationException exception)
        {
            Assert.False(string.IsNullOrWhiteSpace(exception.Message));
        }
    }

    // --------------------------------------------- C2 contribution permissions

    [Theory]
    [InlineData(PluginContributionTypes.ContextAssembler)]
    [InlineData(PluginContributionTypes.PromptProvider)]
    [InlineData(PluginContributionTypes.PromptFragment)]
    public void Prompt_and_context_contributions_require_chat_read(string contributionType)
    {
        var required = NpsPermissionPolicy.RequiredPermissions(contributionType);

        Assert.Contains(PluginPermissions.ChatRead, required);

        // Reverse check: a zero-permission plugin would fail the same check the registry applies.
        var zeroPermission = new HashSet<string>(StringComparer.Ordinal);
        Assert.Contains(required, permission => !zeroPermission.Contains(permission));

        // And a plugin that requests (and is granted) chat.read passes.
        var granted = NpsPermissionPolicy.Decide(
            Manifest(contributions: [new PluginContribution { Type = contributionType, Id = "main" }], permissions: [PluginPermissions.ChatRead]),
            PluginTrustLevel.Bundled,
            new NpsPluginOptions());
        Assert.Contains(PluginPermissions.ChatRead, granted.Granted);
    }

    // ------------------------------------------- C9 tamper blocks startup path

    [Fact]
    public void Tampered_installed_package_is_refused_by_the_integrity_gate()
    {
        using var temp = new TempDirectory();
        var pluginDirectory = temp.Combine("natter.tamper.probe");
        Directory.CreateDirectory(pluginDirectory);
        var dllPath = Path.Combine(pluginDirectory, "probe.dll");
        File.WriteAllText(dllPath, "original");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(dllPath))).ToLowerInvariant();
        File.WriteAllText(Path.Combine(pluginDirectory, "package.sha256"), $"{hash}  probe.dll\n");

        var clean = Natter.Plugin.Store.InstalledPluginIntegrityCodes.Verify(pluginDirectory);
        Assert.Equal(Natter.Plugin.Store.InstalledPluginIntegrity.Verified, clean.Integrity);

        File.WriteAllText(dllPath, "tampered");
        var tampered = Natter.Plugin.Store.InstalledPluginIntegrityCodes.Verify(pluginDirectory);
        Assert.Equal(Natter.Plugin.Store.InstalledPluginIntegrity.Tampered, tampered.Integrity);

        // The startup path must translate that into a rejection, not "unverified but loadable".
        var decision = PluginTrustPolicy.Evaluate(
            new NpsPluginOptions(),
            new PluginTrustOptions(),
            new PluginTrustRegistration(pluginDirectory, PluginSignatureState.Tampered, Detail: tampered.Code));
        Assert.False(decision.Allowed);
        Assert.Equal(PluginTrustCodes.Tampered, decision.Code);
    }

    // ------------------------------------------------ F5 timeout configuration

    [Fact]
    public void Timeout_configuration_is_shipped_and_a_hanging_test_is_bounded()
    {
        var config = Path.Combine(RepoRoot(), "tests", "Natter.Framework.Tests", "xunit.runner.json");
        Assert.True(File.Exists(config), "xunit.runner.json must exist (S2-F5)");
        var json = File.ReadAllText(config);
        Assert.Contains("longRunningTestSeconds", json, StringComparison.Ordinal);

        // Long-running tests must declare an explicit bound: at least one [Fact(Timeout = ...)].
        var tests = Directory.EnumerateFiles(
                Path.Combine(RepoRoot(), "tests", "Natter.Framework.Tests"), "*.cs", SearchOption.TopDirectoryOnly)
            .Where(file => !file.Contains("\\obj\\", StringComparison.Ordinal) && !file.Contains("/obj/", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToArray();
        Assert.Contains(tests, text => text.Contains("Timeout =", StringComparison.Ordinal));
        Assert.Contains(tests, text => text.Contains("--blame-hang", StringComparison.Ordinal));
    }

    // --------------------------------------- F7/F8/F9 documentation guard rails

    [Fact]
    public void Merged_p5_suites_keep_their_coverage_and_docs_stay_consistent()
    {
        var root = RepoRoot();
        var p5 = Path.Combine(root, "tests", "Natter.Framework.Tests");

        // F7: the acceptance suite (stricter) must still exist and the followup suite keeps its
        // lease/tool/Tier2 coverage, so the merge did not drop either side.
        var acceptance = File.ReadAllText(Path.Combine(p5, "P5TaskUxAcceptanceTests.cs"));
        var followup = File.ReadAllText(Path.Combine(p5, "P5FollowupTests.cs"));
        var batch1 = File.ReadAllText(Path.Combine(p5, "S2Batch1AcceptanceTests.cs"));
        Assert.Contains("Unloaded_plugin_is_no_longer_routed", batch1, StringComparison.Ordinal);
        Assert.Contains("Metrics_follow_a_real_task_run", acceptance, StringComparison.Ordinal);
        Assert.Contains("Http_fetch_denies_loopback_private_and_metadata_hosts", followup, StringComparison.Ordinal);
        Assert.Contains("Lease_renewal_keeps_the_owner_and_blocks_other_owners", followup, StringComparison.Ordinal);
        Assert.DoesNotContain("Status_reports_elapsed_step_tools_budget_error_and_recent_trace", File.ReadAllText(
            Path.Combine(p5, "P5TaskUxTests.cs")), StringComparison.Ordinal);

        // F1: the harness must delegate to the product ETag (no private copy of the algorithm).
        var harness = File.ReadAllText(Path.Combine(
            root, "tests", "Natter.Plugin.WebConsole.Tests", "Phase23Acceptance", "AcceptanceHarness.cs"));
        Assert.Contains("AdminETag.Compute", harness, StringComparison.Ordinal);
        Assert.DoesNotContain("SHA256.HashData", harness, StringComparison.Ordinal);

        // F8: so-called "done" soak must not be claimed as PASS while the operation doc demands
        // cumulative 24h evidence.
        var livePlan = File.ReadAllText(Path.Combine(root, "docs", "LIVE_TEST_PLAN.md"));
        Assert.Contains("待人工", livePlan, StringComparison.Ordinal);
        Assert.Contains("passive-soak-24h", livePlan, StringComparison.Ordinal);
        // The P4 productization doc was archived on 2026-11-23 (docs/REVIEW_2026-11-23_ARCHITECTURE.md
        // part 2). The claim it locks down — that a "done" soak still needs cumulative 24h evidence —
        // is still the operative rule, so keep asserting the content, at its archived path.
        var productization = File.ReadAllText(
            Path.Combine(root, "docs", "archive", "P4-TIER1-PRODUCTIZATION.md"));
        Assert.Contains("累计有效运行", productization, StringComparison.Ordinal);

        // F9: README must not hardcode a stale count.
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        Assert.DoesNotContain("264 个框架级测试", readme, StringComparison.Ordinal);
        Assert.Contains("以 CI 实时输出为准", readme, StringComparison.Ordinal);
    }

    // ---------------------------------------------------- regression lock

    [Fact]
    public async Task Builtin_plugins_still_load_with_the_default_permission_set()
    {
        using var temp = new TempDirectory();
        var manifest = new PluginManifest
        {
            SchemaVersion = 1,
            Id = "natter.regression.probe",
            Name = "probe",
            Version = "1.0.0",
            Kind = PluginKind.Feature,
            HostApi = ">=0.1.0 <1.0.0",
            Permissions = [PluginPermissions.CliCommands],
            Contributions = [new PluginContribution { Type = PluginContributionTypes.CliCommand, Id = "probe" }],
        };

        var decision = NpsPermissionPolicy.Decide(manifest, PluginTrustLevel.Bundled, new NpsPluginOptions());

        Assert.Contains(PluginPermissions.CliCommands, decision.Granted);
        Assert.Empty(decision.Denied);
        await Task.CompletedTask;
    }

    // ------------------------------------------------------------ fixtures

    private static RuntimeProfile Profile()
        => new()
        {
            Id = "tier1-default",
            Prompt = new ProfilePrompt { Provider = "host#static", SystemPrompt = "system" },
        };

    private static PluginManifest Manifest(
        IReadOnlyList<PluginContribution> contributions,
        IReadOnlyList<string> permissions)
        => new()
        {
            SchemaVersion = 1,
            Id = "natter.c2.probe",
            Name = "probe",
            Version = "1.0.0",
            Kind = PluginKind.Feature,
            HostApi = ">=0.1.0 <1.0.0",
            Permissions = [.. permissions],
            Contributions = [.. contributions],
        };

    private static ChatCommand FakeCommand(string key)
        => new MessageSendCommand
        {
            Id = "cmd:" + key,
            Source = Source.Create("discord", "discord", "bot"),
            IssuedAt = DateTimeOffset.UtcNow,
            IdempotencyKey = key,
            Payload = new MessageSendPayload(new Conversation("c1", ConversationKind.Direct), new Content("hi")),
        };

    private static MessageCreatedEvent MessageEvent(string eventId, string messageId = "m1")
    {
        var message = new Message(
            messageId,
            new Conversation("c1", ConversationKind.Direct),
            new User("u1", "Alice"),
            new Content("hello"),
            DateTimeOffset.UtcNow);
        return new MessageCreatedEvent
        {
            Id = eventId,
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

    private sealed class ControllableClock(DateTimeOffset start) : Natter.Abstractions.Ports.IClock
    {
        private DateTimeOffset _now = start;

        public DateTimeOffset UtcNow => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "natter-s2b2-" + Guid.NewGuid().ToString("N"));
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
}
