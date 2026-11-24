using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Natter.Chat;
using Natter.Chat.Satori;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// task-74: Satori declared <c>MaxMessageLength</c> but sent every reply as one request, so an
/// over-long message was rejected as a whole. These tests pin the chunking contract it now shares
/// with the Discord and DingTalk adapters: platform-sized chunks, rune-safe boundaries, balanced
/// markdown fences per chunk, unchanged short messages, and the same failure semantics.
/// </summary>
public sealed class SatoriChunkingTests
{
    [Fact]
    public async Task Long_message_is_split_into_chunks_within_the_declared_limit()
    {
        var (upstream, transport, clock) = await NewUpstream(new SatoriCapabilityOptions { MaxMessageLength = 40 });

        var text = string.Join(" ", Enumerable.Repeat("lorem ipsum", 40));
        var ack = await upstream.ExecuteAsync(Send(clock, text));

        Assert.True(ack.Ok);
        Assert.True(transport.Requests.Count > 1, $"expected multiple chunks, got {transport.Requests.Count}");

        var bodies = transport.Requests.Select(ContentOf).ToArray();
        Assert.All(bodies, body => Assert.True(body.Length <= 40, $"chunk over limit: {body.Length}"));
        // Chunking must not drop a single character of the reply.
        Assert.Equal(text, string.Concat(bodies));
        Assert.Equal(transport.Requests.Count, ack.Result!.MessageIds!.Count);
    }

    [Fact]
    public async Task Short_message_is_sent_as_one_request_unchanged()
    {
        var (upstream, transport, clock) = await NewUpstream(new SatoriCapabilityOptions { MaxMessageLength = 40 });

        var ack = await upstream.ExecuteAsync(Send(clock, "hi"));

        Assert.True(ack.Ok);
        var request = Assert.Single(transport.Requests);
        Assert.Equal("/v1/message.create", request.Route);
        Assert.Equal("hi", ContentOf(request));
    }

    [Fact]
    public async Task Explicit_null_disables_chunking_instead_of_chunking_at_a_guessed_limit()
    {
        // null is the "we do not know this platform's limit" escape hatch. It is allowed, but the
        // upstream must say so out loud (see Explicit_null_disables_chunking_and_is_warned_about).
        var (upstream, transport, clock) = await NewUpstream(new SatoriCapabilityOptions { MaxMessageLength = null });

        var text = new string('x', 500);
        var ack = await upstream.ExecuteAsync(Send(clock, text));

        Assert.True(ack.Ok);
        Assert.Equal(text, ContentOf(Assert.Single(transport.Requests)));
    }

    [Fact]
    public async Task Fenced_code_block_is_closed_and_reopened_so_every_chunk_renders()
    {
        var (upstream, transport, clock) = await NewUpstream(new SatoriCapabilityOptions { MaxMessageLength = 60 });

        var code = string.Join("\n", Enumerable.Repeat("var value = 1;", 10));
        var text = "before\n```csharp\n" + code + "\n```\nafter";
        var ack = await upstream.ExecuteAsync(Send(clock, text, ContentFormat.Markdown));

        Assert.True(ack.Ok);
        var bodies = transport.Requests.Select(ContentOf).ToArray();
        Assert.True(bodies.Length > 1, "the code block must span several chunks for this test to mean anything");

        // Every chunk is individually balanced: an opening fence always has its closing marker.
        Assert.All(bodies, body => Assert.Equal(0, CountOf(body, "```") % 2));
        // The info string survives on the reopened fence and the surrounding prose is kept.
        Assert.Contains(bodies, body => body.Contains("```csharp", StringComparison.Ordinal));
        Assert.Contains(bodies, body => body.Contains("before", StringComparison.Ordinal));
        Assert.Contains(bodies, body => body.Contains("after", StringComparison.Ordinal));
        // Content is not lost to the fence repair. The repair itself may add one blank line per
        // boundary (documented as cosmetic), so assert on the code lines rather than exact bytes.
        Assert.Equal(10, bodies.Sum(body => CountOf(body, "var value = 1;")));
    }

    [Fact]
    public async Task Plain_text_does_not_get_fence_markers_injected()
    {
        // Fence repair is markdown-only; a plain-text reply must never gain ``` markers.
        var (upstream, transport, clock) = await NewUpstream(new SatoriCapabilityOptions { MaxMessageLength = 30 });

        var text = "```not a fence``` " + new string('y', 120);
        var ack = await upstream.ExecuteAsync(Send(clock, text));

        Assert.True(ack.Ok);
        var bodies = transport.Requests.Select(ContentOf).ToArray();
        Assert.True(bodies.Length > 1);
        Assert.DoesNotContain(bodies.Skip(1), body => body.StartsWith("```", StringComparison.Ordinal));
        Assert.Equal(text, string.Concat(bodies));
    }

    [Fact]
    public async Task Emoji_is_never_cut_in_the_middle_of_a_surrogate_pair()
    {
        var (upstream, transport, clock) = await NewUpstream(new SatoriCapabilityOptions { MaxMessageLength = 24 });

        const string family = "👨‍👩‍👧‍👦";
        const string flag = "🇨🇳";
        var text = string.Concat(Enumerable.Repeat(family + flag, 30));
        var ack = await upstream.ExecuteAsync(Send(clock, text));

        Assert.True(ack.Ok);
        var bodies = transport.Requests.Select(ContentOf).ToArray();
        Assert.True(bodies.Length > 1);

        Assert.All(bodies, body => Assert.False(HasLoneSurrogate(body), "chunk contains half of a surrogate pair"));
        // Every code unit still reaches the platform, in order.
        Assert.Equal(text, string.Concat(bodies));
    }

    [Fact]
    public async Task Only_the_first_chunk_keeps_the_reply_reference()
    {
        var (upstream, transport, clock) = await NewUpstream(new SatoriCapabilityOptions { MaxMessageLength = 30 });

        var text = string.Join(" ", Enumerable.Repeat("reply text", 30));
        var ack = await upstream.ExecuteAsync(Send(clock, text, replyTo: "parent-1"));

        Assert.True(ack.Ok);
        var replyTos = transport.Requests.Select(ReplyToOf).ToArray();
        Assert.True(replyTos.Length > 1);
        Assert.Equal("parent-1", replyTos[0]);
        Assert.All(replyTos.Skip(1), Assert.Null);
    }

    [Fact]
    public async Task Failing_chunk_ends_the_send_with_that_chunks_error_and_is_not_cached()
    {
        var (upstream, transport, clock) = await NewUpstream(new SatoriCapabilityOptions { MaxMessageLength = 30 });
        var send = Send(
            clock,
            string.Join(" ", Enumerable.Repeat("retry me", 40)),
            idempotencyKey: "chunk-failure-1");

        transport.FailOnCall = 2;
        var failed = await upstream.ExecuteAsync(send);

        // Same semantics as Discord/DingTalk: the first failing chunk wins, nothing is rolled back,
        // the remaining chunks are not attempted, and the error is passed through verbatim.
        Assert.False(failed.Ok);
        Assert.Equal("rate_limited", failed.Error!.Code);
        Assert.Equal(3_000, failed.Error.RetryAfterMs);
        Assert.Equal(2, transport.Requests.Count);

        // A failed send must not be cached as a successful ack: retrying the same idempotency key
        // actually re-sends.
        transport.FailOnCall = 0;
        var retried = await upstream.ExecuteAsync(send);

        Assert.True(retried.Ok);
        Assert.True(transport.Requests.Count > 2, "the retry must re-send, not replay a cached failure");
    }

    [Fact]
    public async Task Successful_send_is_cached_by_idempotency_key()
    {
        // Regression lock for the existing behaviour that must survive the chunking change.
        var (upstream, transport, clock) = await NewUpstream(new SatoriCapabilityOptions { MaxMessageLength = 40 });
        var send = Send(clock, string.Join(" ", Enumerable.Repeat("once", 40)), idempotencyKey: "chunk-ok-1");

        var first = await upstream.ExecuteAsync(send);
        var afterFirst = transport.Requests.Count;
        var second = await upstream.ExecuteAsync(send);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.Equal(afterFirst, transport.Requests.Count);
        Assert.Equal(first.Result!.MessageIds, second.Result!.MessageIds);
    }

    [Fact]
    public async Task Host_default_options_chunk_at_the_conservative_default()
    {
        // No explicit limit from the host: the conservative default must already be in force,
        // otherwise this fix would ship silently inert (which is worse than no fix at all).
        var (upstream, transport, clock) = await NewUpstream(new SatoriCapabilityOptions());

        var text = new string('z', SatoriCapabilityOptions.DefaultMaxMessageLength + 500);
        var ack = await upstream.ExecuteAsync(Send(clock, text));

        Assert.True(ack.Ok);
        Assert.True(transport.Requests.Count > 1);
        Assert.All(
            transport.Requests.Select(ContentOf),
            body => Assert.True(body.Length <= SatoriCapabilityOptions.DefaultMaxMessageLength, $"chunk over default: {body.Length}"));
        Assert.Equal(text, string.Concat(transport.Requests.Select(ContentOf)));
    }

    [Fact]
    public async Task Explicit_null_disables_chunking_and_is_warned_about()
    {
        var logger = new RecordingLogger<SatoriChatUpstream>();
        var (upstream, transport, clock) = await NewUpstream(
            new SatoriCapabilityOptions { MaxMessageLength = null },
            logger);

        var text = new string('w', SatoriCapabilityOptions.DefaultMaxMessageLength + 500);
        var ack = await upstream.ExecuteAsync(Send(clock, text));

        Assert.True(ack.Ok);
        // Chunking really is off (the deployment asked for that) ...
        Assert.Equal(text, ContentOf(Assert.Single(transport.Requests)));
        // ... and the decision is visible in the journal instead of being a silent no-op.
        Assert.Contains(
            logger.Entries,
            entry => entry.Contains("chunking is disabled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Chunk_boundary_is_exact_at_the_declared_limit()
    {
        var (upstream, transport, clock) = await NewUpstream(new SatoriCapabilityOptions { MaxMessageLength = 10 });

        Assert.True((await upstream.ExecuteAsync(Send(clock, new string('a', 10)))).Ok);
        Assert.Single(transport.Requests);

        transport.Requests.Clear();
        Assert.True((await upstream.ExecuteAsync(Send(clock, new string('b', 11)))).Ok);
        Assert.Equal(2, transport.Requests.Count);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // helpers
    // ─────────────────────────────────────────────────────────────────────────────

    private static async Task<(SatoriChatUpstream Upstream, RecordingSatoriTransport Transport, TestClock Clock)> NewUpstream(
        SatoriCapabilityOptions options,
        ILogger<SatoriChatUpstream>? logger = null)
    {
        var clock = new TestClock();
        var transport = new RecordingSatoriTransport();
        var upstream = new SatoriChatUpstream(
            ChatTestData.Source(),
            transport,
            clock,
            capabilityOptions: options,
            logger: logger);
        await upstream.ConnectAsync();
        return (upstream, transport, clock);
    }

    private static MessageSendCommand Send(
        TestClock clock,
        string text,
        ContentFormat format = ContentFormat.Plain,
        string? replyTo = null,
        string? idempotencyKey = null)
        => new ChatCommandFactory(clock).Send(
            ChatTestData.Source(),
            ChatTestData.Conversation(),
            new Content(text, format),
            replyTo: replyTo,
            idempotencyKey: idempotencyKey);

    private static string ContentOf(SatoriApiRequest request)
        => request.Body.TryGetProperty("content", out var value) ? value.GetString() ?? string.Empty : string.Empty;

    private static string? ReplyToOf(SatoriApiRequest request)
        => request.Body.TryGetProperty("reply_to", out var value) ? value.GetString() : null;

    private static int CountOf(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static bool HasLoneSurrogate(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                if (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1]))
                    return true;

                index++;
                continue;
            }

            if (char.IsLowSurrogate(text[index]))
                return true;
        }

        return false;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly Lock _gate = new();
        private readonly List<string> _entries = [];

        public IReadOnlyList<string> Entries
        {
            get
            {
                lock (_gate)
                    return _entries.ToArray();
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
                _entries.Add($"{logLevel}: {formatter(state, exception)}");
        }
    }

    private sealed class RecordingSatoriTransport : ISatoriTransport
    {
        private readonly Subject<SatoriEvent> _events = new();
        private int _calls;

        public IObservable<SatoriEvent> Events => _events;

        public List<SatoriApiRequest> Requests { get; } = [];

        /// <summary>1-based call number that fails; 0 never fails.</summary>
        public int FailOnCall { get; set; }

        public SatoriSendResult Failure { get; set; } =
            new(false, ErrorCode: "rate_limited", ErrorMessage: "slow down", RetryAfterMs: 3_000);

        public ValueTask ConnectAsync(long? lastSn, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<SatoriSendResult> SendAsync(
            SatoriApiRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var call = Interlocked.Increment(ref _calls);
            return ValueTask.FromResult(FailOnCall == call
                ? Failure
                : new SatoriSendResult(true, [$"satori-message-{call}"]));
        }
    }
}
