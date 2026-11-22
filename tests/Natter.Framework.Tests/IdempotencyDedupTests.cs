using System.Diagnostics;
using Natter.Application.Chat;
using Natter.Chat;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>S2 idempotency, dedupe and prompt-input limits (task-45).</summary>
public sealed class IdempotencyDedupTests
{
    // ------------------------------------------------------------------ S2-A1

    [Fact]
    public async Task Concurrent_same_key_executes_once_and_commits()
    {
        var clock = new TestClock();
        var cache = new ChatIdempotencyCache(clock: () => clock.UtcNow);
        var upstream = new GatedUpstream();
        var dispatcher = new ChatCommandDispatcher(Registry(upstream), cache, clock);
        var command = SendCommand("same-key");

        var first = dispatcher.ExecuteAsync(command).AsTask();
        await upstream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = await dispatcher.ExecuteAsync(command);
        Assert.False(second.Ok);
        Assert.Equal(ChatErrorCodes.Conflict, second.Error?.Code);

        upstream.Release.TrySetResult();
        Assert.True((await first).Ok);

        var third = await dispatcher.ExecuteAsync(command);
        Assert.True(third.Ok);
        Assert.Equal(1, upstream.Calls);
    }

    [Fact]
    public async Task Failed_execution_rolls_back_and_retry_succeeds()
    {
        var clock = new TestClock();
        var cache = new ChatIdempotencyCache(clock: () => clock.UtcNow);
        var upstream = new FlakyUpstream(failures: 1);
        var dispatcher = new ChatCommandDispatcher(Registry(upstream), cache, clock);
        var command = SendCommand("retry-key");

        var first = await dispatcher.ExecuteAsync(command);
        Assert.False(first.Ok);
        Assert.Equal(0, cache.Count);

        var second = await dispatcher.ExecuteAsync(command);
        Assert.True(second.Ok);
        Assert.Equal(2, upstream.Calls);

        var third = await dispatcher.ExecuteAsync(command);
        Assert.True(third.Ok);
        Assert.Equal(2, upstream.Calls);
    }

    [Fact]
    public void Pending_reservation_expires()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new ChatIdempotencyCache(
            window: TimeSpan.FromMinutes(10),
            pendingTimeout: TimeSpan.FromMilliseconds(100),
            clock: () => now);

        Assert.Equal(ChatIdempotencyCache.Reservation.Reserved, cache.TryReserve("k", out _));
        Assert.Equal(ChatIdempotencyCache.Reservation.InFlight, cache.TryReserve("k", out _));
        Assert.Equal(1, cache.PendingCount);

        now += TimeSpan.FromMilliseconds(150);
        Assert.Equal(ChatIdempotencyCache.Reservation.Reserved, cache.TryReserve("k", out _));
    }

    // ------------------------------------------------------------------ S2-A12

    [Fact]
    public void Dedupe_window_defaults_to_at_least_sixty_minutes()
    {
        var dedup = new ChatEventDeduplicator();
        Assert.True(dedup.Window >= TimeSpan.FromMinutes(60), $"window was {dedup.Window}");
    }

    [Fact]
    public void Replays_within_sixty_minutes_are_blocked_and_late_events_are_counted()
    {
        var now = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        var dedup = new ChatEventDeduplicator(clock: () => now);
        var created = MessageEvent("evt-1", now - TimeSpan.FromMinutes(30));
        Assert.True(dedup.TryAccept(created));
        Assert.False(dedup.TryAccept(created));

        now += TimeSpan.FromMinutes(55);
        Assert.False(dedup.TryAccept(MessageEvent("evt-1", now - TimeSpan.FromMinutes(30))));

        // 61 minutes after the original event the entry expires; a replay is accepted exactly
        // once and counted as a window-gap candidate.
        now += TimeSpan.FromMinutes(6);
        Assert.True(dedup.TryAccept(MessageEvent("evt-1", now - TimeSpan.FromMinutes(61))));
        Assert.Equal(1, dedup.LateEventCount);
        Assert.False(dedup.TryAccept(MessageEvent("evt-1", now - TimeSpan.FromMinutes(61))));
    }

    // ------------------------------------------------------------------ S2-A13

    [Fact]
    public void Edits_get_distinct_ids_but_a_replayed_edit_is_stable()
    {
        var source = Source.Create("discord", "discord", "bot");
        var firstEdit = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        var secondEdit = firstEdit.AddMinutes(1);

        var first = ChatEventId.DeriveEdit(source, "m1", firstEdit);
        var second = ChatEventId.DeriveEdit(source, "m1", secondEdit);
        Assert.NotEqual(first, second);
        Assert.Equal(first, ChatEventId.DeriveEdit(source, "m1", firstEdit));

        var now = DateTimeOffset.UtcNow;
        var dedup = new ChatEventDeduplicator(clock: () => now);
        Assert.True(dedup.TryAccept(EditEvent(first, source)));
        Assert.True(dedup.TryAccept(EditEvent(second, source)));
        Assert.False(dedup.TryAccept(EditEvent(second, source)));
    }

    // ------------------------------------------------------------------ S2-A14

    [Fact]
    public void Prune_does_not_scan_the_whole_table_per_event()
    {
        var now = DateTimeOffset.UtcNow;
        var dedup = new ChatEventDeduplicator(clock: () => now);
        for (var index = 0; index < 10_000; index++)
        {
            Assert.True(dedup.TryAccept(MessageEvent("evt-" + index, now)));
            now += TimeSpan.FromMilliseconds(1);
        }

        Assert.Equal(10_000, dedup.Count);

        var stopwatch = Stopwatch.StartNew();
        for (var index = 0; index < 2_000; index++)
        {
            dedup.TryAccept(MessageEvent("extra-" + index, now));
            now += TimeSpan.FromMilliseconds(1);
        }

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"prune took {stopwatch.Elapsed}");
    }

    // ------------------------------------------------------------------ S2-A15

    [Fact]
    public async Task Chunked_retry_resumes_from_the_failed_chunk_without_duplicates()
    {
        var clock = new TestClock();
        var cache = new ChatIdempotencyCache(clock: () => clock.UtcNow);
        var upstream = new ChunkFailingUpstream(failChunkIndex: 1);
        var dispatcher = new ChatCommandDispatcher(Registry(upstream), cache, clock);
        var command = SendCommand("chunk-key", "abcdefghijklmnopqrstuvwxyz");

        var first = await dispatcher.ExecuteAsync(command);
        Assert.False(first.Ok);

        var second = await dispatcher.ExecuteAsync(command);
        Assert.True(second.Ok);

        // The retry must not send the chunk the platform already accepted a second time.
        Assert.Equal(1, upstream.SentTexts.Count(text => text == upstream.FirstChunkText));
        Assert.Contains(upstream.SentTexts, text => text.Contains("uvwxyz", StringComparison.Ordinal));
        Assert.Equal(2, upstream.Attempts);
    }

    // ------------------------------------------------------------------ S2-A22

    [Fact]
    public void Prompt_size_limits_truncate_with_warnings_consistently()
    {
        var longVariable = new string('x', PromptCompilerDefaults.VariableMaxCharacters + 100);
        var plan = new PromptComposer().Compose(new PromptComposeRequest(
            new RuntimeProfile { Id = "p", Ai = new ProfileAi { Provider = "test#p" } },
            [new PromptPreset("default", "v1", [new PromptSectionRef("greet", 10)])],
            [new PromptFragment("greet", "user={{user.name}}", PromptFragmentSource.Inline, 10)],
            UserName: longVariable));

        Assert.Contains($"{PromptCompilerCodes.VariableTruncated}:user.name", plan.Warnings);
        Assert.Equal(
            PromptCompilerDefaults.VariableMaxCharacters,
            PromptVariables.Escape(longVariable).Length);

        var pluginText = new string('y', PromptCompilerDefaults.FragmentMaxCharacters + 500);
        var pluginPlan = new PromptComposer().Compose(new PromptComposeRequest(
            new RuntimeProfile { Id = "p", Ai = new ProfileAi { Provider = "test#p" } },
            [new PromptPreset("default", "v1", [new PromptSectionRef("natter.plugin.x#big", 10)])],
            [],
            PluginFragments:
            [
                new PromptFragment("natter.plugin.x#big", pluginText, PromptFragmentSource.Plugin, 10),
            ]));

        Assert.Contains($"{PromptCompilerCodes.FragmentTruncated}:natter.plugin.x#big", pluginPlan.Warnings);
        Assert.True(pluginPlan.Fragments[0].Text.Length <= PromptCompilerDefaults.FragmentMaxCharacters);
    }

    // ------------------------------------------------------------------ helpers

    private static MessageSendCommand SendCommand(string key, string text = "hello")
        => new()
        {
            Id = "cmd-" + key,
            Source = Source.Create("discord", "discord", "bot"),
            IssuedAt = DateTimeOffset.UtcNow,
            IdempotencyKey = key,
            Payload = new MessageSendPayload(
                new Conversation("c1", ConversationKind.Direct),
                new Content(text)),
        };

    private static FakeRegistry Registry(IChatUpstream upstream)
    {
        var registry = new FakeRegistry();
        registry.Register(upstream);
        return registry;
    }

    private static MessageCreatedEvent MessageEvent(string id, DateTimeOffset occurredAt)
        => new()
        {
            Id = id,
            Source = Source.Create("discord", "discord", "bot"),
            Sequence = 1,
            OccurredAt = occurredAt,
            ReceivedAt = occurredAt,
            Payload = new MessagePayload(new Message(
                "m-" + id,
                new Conversation("c1", ConversationKind.Direct),
                new User("u1", "User"),
                new Content("hi"),
                occurredAt)),
        };

    private static MessageUpdatedEvent EditEvent(string id, Source source)
    {
        var at = DateTimeOffset.UtcNow;
        return new MessageUpdatedEvent
        {
            Id = id,
            Source = source,
            Sequence = 1,
            OccurredAt = at,
            ReceivedAt = at,
            Payload = new MessagePayload(new Message(
                "m1",
                new Conversation("c1", ConversationKind.Direct),
                new User("u1", "User"),
                new Content("edited"),
                at,
                EditedAt: at)),
        };
    }

    private sealed class FakeRegistry : IChatUpstreamRegistry
    {
        private readonly Dictionary<Source, IChatUpstream> _upstreams = [];

        public void Register(IChatUpstream upstream) => _upstreams[upstream.Source] = upstream;

        public bool Unregister(Source source) => _upstreams.Remove(source);

        public bool TryGet(Source source, out IChatUpstream? upstream)
            => _upstreams.TryGetValue(source, out upstream);

        public IReadOnlyList<IChatUpstream> Upstreams => _upstreams.Values.ToArray();
    }

    private abstract class FakeUpstream : IChatUpstream
    {
        public Source Source { get; } = Source.Create("discord", "discord", "bot");

        public bool IsConnected => true;

        public IObservable<ChatEvent> Events { get; } = new EmptyObservable();

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public virtual ValueTask<Capabilities> GetCapabilitiesAsync(Conversation? conversation = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new Capabilities
            {
                ConversationKinds = [ConversationKind.Direct],
                MaxMessageLength = 10,
            });

        public abstract ValueTask<ChatAck> ExecuteAsync(ChatCommand command, CancellationToken cancellationToken = default);

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

    private sealed class GatedUpstream : FakeUpstream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls { get; private set; }

        public override async ValueTask<ChatAck> ExecuteAsync(ChatCommand command, CancellationToken cancellationToken = default)
        {
            Calls++;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return ChatAck.Success(command, ["m1"]);
        }
    }

    private sealed class FlakyUpstream(int failures) : FakeUpstream
    {
        public int Calls { get; private set; }

        public override ValueTask<ChatAck> ExecuteAsync(ChatCommand command, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(Calls <= failures
                ? ChatAck.Failure(command, ChatError.Create(ChatErrorCodes.Internal, "flaky"))
                : ChatAck.Success(command, ["m1"]));
        }
    }

    private sealed class ChunkFailingUpstream(int failChunkIndex) : FakeUpstream
    {
        public string FirstChunkText { get; private set; } = string.Empty;

        public List<string> SentTexts { get; } = [];

        public int Attempts { get; private set; }

        public override ValueTask<ChatAck> ExecuteAsync(ChatCommand command, CancellationToken cancellationToken = default)
        {
            var text = command is MessageSendCommand send ? send.Payload.Content.Text ?? string.Empty : string.Empty;
            var chunkIndex = SentTexts.Count(text => text.StartsWith("chunk:", StringComparison.Ordinal) is false && text == text);
            _ = chunkIndex;
            if (Attempts == 0)
                FirstChunkText = FirstChunkText.Length == 0 ? text : FirstChunkText;

            var chunkNumber = command.Id.Contains(":chunk-", StringComparison.Ordinal)
                ? int.Parse(command.Id[(command.Id.LastIndexOf(":chunk-", StringComparison.Ordinal) + 7)..])
                : 0;

            if (Attempts == 0 && chunkNumber == failChunkIndex)
            {
                Attempts++;
                return ValueTask.FromResult(ChatAck.Failure(command, ChatError.Create(ChatErrorCodes.Unavailable, "chunk failed")));
            }

            SentTexts.Add(text);
            if (chunkNumber >= 2)
                Attempts = 2;
            return ValueTask.FromResult(ChatAck.Success(command, ["chunk-" + chunkNumber]));
        }
    }
}
