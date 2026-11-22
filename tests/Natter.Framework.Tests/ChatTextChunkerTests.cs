using System.Text;
using Natter.Chat;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// S3-12 (an over-long chunk can exceed the platform limit) and S3-13 (markdown fences break across
/// chunks). The chunker is the shared outbound path for every adapter, so the length invariant is
/// asserted as a hard property over hand-written cases and a deterministic fuzz corpus.
/// </summary>
public sealed class ChatTextChunkerTests
{
    private const string Family = "👨‍👩‍👧‍👦";   // ZWJ sequence, 11 UTF-16 code units
    private const string Flag = "🇨🇳";           // two regional indicators, 4 code units
    private const string Combining = "e\u0301";  // e + combining acute, 2 code units

    [Fact]
    public void Chunk_keeps_attachments_and_mentions_on_the_first_chunk()
    {
        var attachment = new Attachment("https://example.test/a.png");
        var mention = new Mention(MentionKind.User, "human-1");
        var content = new Content("abcdefghijkl", Attachments: [attachment], Mentions: [mention]);

        var chunks = ChatTextChunker.Chunk(content, 5);

        Assert.Equal(3, chunks.Count);
        Assert.Equal("abcde", chunks[0].Text);
        Assert.Equal("fghij", chunks[1].Text);
        Assert.Equal("kl", chunks[2].Text);
        Assert.Same(attachment, Assert.Single(chunks[0].Attachments!));
        Assert.Same(mention, Assert.Single(chunks[0].Mentions!));
        Assert.All(chunks.Skip(1), chunk =>
        {
            Assert.Null(chunk.Attachments);
            Assert.Null(chunk.Mentions);
        });
    }

    [Fact]
    public void Chunk_returns_the_original_when_it_already_fits()
    {
        var content = new Content("short", ContentFormat.Markdown);

        var chunks = ChatTextChunker.Chunk(content, 5);

        Assert.Same(content, Assert.Single(chunks));
    }

    [Fact]
    public void Chunk_does_not_split_when_the_limit_is_absent_or_disabled()
    {
        var content = new Content(new string('x', 500));

        Assert.Same(content, Assert.Single(ChatTextChunker.Chunk(content, null)));
        Assert.Same(content, Assert.Single(ChatTextChunker.Chunk(content, 0)));
        Assert.Same(content, Assert.Single(ChatTextChunker.Chunk(content, -1)));
    }

    [Fact]
    public void SplitText_splits_ascii_without_spaces_at_the_limit()
    {
        var chunks = ChatTextChunker.SplitText("abcdefghijkl", 5);

        Assert.Equal(["abcde", "fghij", "kl"], chunks);
    }

    [Fact]
    public void SplitText_prefers_a_word_boundary_when_one_is_available()
    {
        var text = "alpha bravo charlie delta echo";

        var chunks = ChatTextChunker.SplitText(text, 12);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 12, $"chunk '{chunk}' is over 12"));
        Assert.Equal(text, string.Concat(chunks));

        // At least one break lands on a space rather than mid-word.
        Assert.Contains(chunks.Take(chunks.Count - 1), chunk => chunk.EndsWith(' '));
    }

    [Fact]
    public void Over_long_zwj_emoji_never_exceeds_the_limit()
    {
        // Before the fix the first element was appended unconditionally, so this produced one chunk
        // of 11 code units for a limit of 5 and the platform rejected the whole message.
        var text = Family + Family + Family;

        var chunks = ChatTextChunker.SplitText(text, 5);

        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 5, $"len={chunk.Length}"));
        Assert.Equal(text, string.Concat(chunks));
        Assert.All(chunks, chunk => Assert.False(HasLoneSurrogate(chunk), "a surrogate pair was cut"));
    }

    [Fact]
    public void Over_long_flag_and_combining_sequences_never_exceed_the_limit()
    {
        foreach (var element in new[] { Flag, Combining, Family })
        {
            var text = string.Concat(Enumerable.Repeat(element, 6));

            foreach (var limit in new[] { 2, 3, 4, 5, 7 })
            {
                var chunks = ChatTextChunker.SplitText(text, limit);

                Assert.All(chunks, chunk => Assert.True(
                    chunk.Length <= limit,
                    $"element={element} len={chunk.Length} limit={limit}"));
                Assert.Equal(text, string.Concat(chunks));
                Assert.All(chunks, chunk => Assert.False(HasLoneSurrogate(chunk)));
            }
        }
    }

    [Fact]
    public void Hard_cut_chunks_survive_a_utf8_round_trip()
    {
        var text = string.Concat(Enumerable.Repeat(Family + Flag + "中文" + Combining, 4));

        var chunks = ChatTextChunker.SplitText(text, 6);

        foreach (var chunk in chunks)
        {
            Assert.True(chunk.Length <= 6);
            Assert.Equal(chunk, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(chunk)));
        }

        Assert.Equal(text, string.Concat(chunks));
    }

    [Fact]
    public void Markdown_fence_is_closed_and_reopened_across_chunks()
    {
        var body = string.Join("\n", Enumerable.Range(1, 40).Select(index => $"line {index:D2} of code"));
        var text = "intro\n```csharp\n" + body + "\n```\noutro\n";

        var chunks = ChatTextChunker.SplitText(text, 40, preserveMarkdownFences: true);

        Assert.True(chunks.Count > 2);
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 40, $"len={chunk.Length} chunk='{chunk}'"));
        Assert.All(chunks, chunk => Assert.True(IsFenceBalanced(chunk), $"unbalanced fence in chunk:\n{chunk}"));

        // The synthesised reopening keeps the info string.
        Assert.Contains(chunks.Skip(1), chunk => chunk.StartsWith("```csharp\n", StringComparison.Ordinal));

        // Every chunk that ends inside the block carries an explicit closing marker.
        Assert.Contains(chunks.Take(chunks.Count - 1), chunk => chunk.EndsWith("```", StringComparison.Ordinal));

        Assert.Equal(text, Reassemble(chunks, OpenFenceAtEnd(text)));
    }

    [Fact]
    public void Tilde_fence_is_closed_and_reopened_across_chunks()
    {
        var body = string.Join("\n", Enumerable.Range(1, 30).Select(index => $"row {index:D2}"));
        var text = "before\n~~~\n" + body + "\n~~~\nafter\n";

        var chunks = ChatTextChunker.SplitText(text, 24, preserveMarkdownFences: true);

        Assert.True(chunks.Count > 2);
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 24));
        Assert.All(chunks, chunk => Assert.True(IsFenceBalanced(chunk)));
        Assert.Contains(chunks.Skip(1), chunk => chunk.StartsWith("~~~\n", StringComparison.Ordinal));
        Assert.Equal(text, Reassemble(chunks, OpenFenceAtEnd(text)));
    }

    [Fact]
    public void Unclosed_fence_is_closed_at_the_end_without_throwing()
    {
        var body = string.Join("\n", Enumerable.Range(1, 20).Select(index => $"dangling {index:D2}"));
        var text = "```\n" + body + "\n";

        var chunks = ChatTextChunker.SplitText(text, 20, preserveMarkdownFences: true);

        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 20));
        Assert.All(chunks, chunk => Assert.True(IsFenceBalanced(chunk), "source fence was left open"));
        Assert.Equal(text, Reassemble(chunks, OpenFenceAtEnd(text)));
    }

    [Fact]
    public void Nested_fence_inside_a_block_is_treated_as_content()
    {
        // A ~~~ line inside a ``` block is literal content in CommonMark, not a second fence.
        var text = "```\n~~~\n" + new string('a', 40) + "\n~~~\n```\n";

        var chunks = ChatTextChunker.SplitText(text, 16, preserveMarkdownFences: true);

        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 16));
        Assert.All(chunks, chunk => Assert.True(IsFenceBalanced(chunk)));
        Assert.Equal(text, Reassemble(chunks, OpenFenceAtEnd(text)));
    }

    [Fact]
    public void Plain_format_never_gains_fence_markers()
    {
        // A plain-text payload may contain ``` as ordinary characters; injecting repair markers would
        // show extra characters to the user, so repair is markdown-only.
        var text = "```\n" + new string('b', 30) + "\n```\n";

        var chunks = ChatTextChunker.Chunk(new Content(text, ContentFormat.Plain), 12);

        Assert.All(chunks, chunk => Assert.True(chunk.Text.Length <= 12));
        Assert.Equal(text, string.Concat(chunks.Select(chunk => chunk.Text)));
    }

    [Fact]
    public void Markdown_format_repairs_fences_through_the_Content_path()
    {
        var body = string.Join("\n", Enumerable.Range(1, 25).Select(index => $"c {index:D2}"));
        var text = "```\n" + body + "\n```\n";

        var chunks = ChatTextChunker.Chunk(new Content(text, ContentFormat.Markdown), 18);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(chunk.Text.Length <= 18));
        Assert.All(chunks, chunk => Assert.True(IsFenceBalanced(chunk.Text)));
        Assert.Equal(text, Reassemble([.. chunks.Select(chunk => chunk.Text)], OpenFenceAtEnd(text)));
    }

    [Fact]
    public void Fence_that_cannot_fit_reserves_the_length_invariant()
    {
        // Limit 3 cannot hold "```" plus its own closing marker, so the line degrades to plain text:
        // the length invariant wins over fence repair.
        var text = "```\ncode\n```\n";

        var chunks = ChatTextChunker.SplitText(text, 3, preserveMarkdownFences: true);

        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 3, $"len={chunk.Length}"));
        Assert.Equal(text, string.Concat(chunks));
    }

    [Fact]
    public void Limit_of_one_cannot_host_a_supplementary_character_and_keeps_the_rune_intact()
    {
        // Documented deviation: cutting the surrogate pair would emit invalid UTF-16, so the pair stays
        // whole (2 code units) instead. Real adapter limits are far above 1.
        var chunks = ChatTextChunker.SplitText("👍👍", 1);

        Assert.All(chunks, chunk => Assert.False(HasLoneSurrogate(chunk)));
        Assert.Equal("👍👍", string.Concat(chunks));
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 2));
    }

    [Fact]
    public void SplitText_rejects_invalid_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => ChatTextChunker.SplitText(null!, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChatTextChunker.SplitText("abc", 0));
        Assert.Throws<ArgumentNullException>(() => ChatTextChunker.Chunk(null!, 10));
    }

    [Fact]
    public void Code_block_lines_break_at_newlines_not_at_spaces()
    {
        // Inside a fence a space is not a word boundary: carrying a tail after the last space would chop
        // "line 03" into "line" and " 03" and the code block would render as two lines.
        var body = string.Join("\n", Enumerable.Range(1, 24).Select(index => $"line {index:D2}"));
        var text = "```\n" + body + "\n```\n";

        var chunks = ChatTextChunker.SplitText(text, 34, preserveMarkdownFences: true);

        Assert.True(chunks.Count > 2);
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 34));

        // Every chunk's code lines are whole: dropping the fence marker lines leaves lines that all start
        // with "line " or are empty, never a bare continuation such as " 03".
        foreach (var chunk in chunks)
        {
            foreach (var line in chunk.Split('\n'))
            {
                if (line.Length == 0 || line.StartsWith("```", StringComparison.Ordinal))
                    continue;

                Assert.StartsWith("line ", line);
            }
        }

        Assert.Equal(text, Reassemble(chunks, OpenFenceAtEnd(text)));
    }

    [Fact]
    public void Invariant_fuzz_200_inputs()
    {
        var random = new Random(20261122);

        string[] pieces =
        [
            "你好", "测试文本", "。", "、", "abc", "hello world", " ", "\n", "\n\n",
            "👍", Flag, Family, Combining, "中文emoji👍混排",
            "a b c d e f g h i j k",
            "```csharp\n", "var x = 1;\n", "```\n",
            "~~~\n", "tilde body\n", "~~~\n",
            "````\n", "long fence\n", "````\n",
        ];

        var strongGroups = 0;
        var tinyGroups = 0;
        var hostileGroups = 0;

        static bool IsFencePiece(string piece)
            => piece.Length >= 3 && (piece[0] == '`' || piece[0] == '~');

        // A fence only counts as a fence when it starts a line, exactly like markdown. Producing
        // line-initial fences keeps the strong contract applicable to every generated group.
        string Generate(int limit, bool lineInitialFences, int extra)
        {
            var target = (limit * 2) + extra;
            var builder = new StringBuilder();
            while (builder.Length < target)
            {
                var piece = pieces[random.Next(pieces.Length)];
                if (lineInitialFences && IsFencePiece(piece) && builder.Length > 0 && builder[^1] != '\n')
                    builder.Append('\n');
                builder.Append(piece);
            }

            return builder.ToString();
        }

        // Phase 1 - realistic limits, fences only at line starts: the full fence contract applies.
        for (var group = 0; group < 150; group++)
        {
            var limit = random.Next(40, 201);
            var text = Generate(limit, lineInitialFences: true, random.Next(1, 80));
            var label = $"strong group={group} limit={limit} text={Escape(text)}";

            var fenced = ChatTextChunker.SplitText(text, limit, preserveMarkdownFences: true);
            Assert.All(fenced, chunk => Assert.True(chunk.Length <= limit, $"{label} len={chunk.Length}"));
            Assert.All(fenced, chunk => Assert.False(HasLoneSurrogate(chunk), label));
            Assert.All(fenced, chunk => Assert.True(IsFenceBalanced(chunk), $"{label} chunk='{chunk}'"));
            Assert.Equal(text, Reassemble(fenced, OpenFenceAtEnd(text)));

            var raw = ChatTextChunker.SplitText(text, limit);
            Assert.All(raw, chunk => Assert.True(chunk.Length <= limit, $"{label} len={chunk.Length}"));
            Assert.All(raw, chunk => Assert.False(HasLoneSurrogate(chunk), label));
            Assert.Equal(text, string.Concat(raw));

            strongGroups++;
        }

        // Phase 2 - limits too small to hold a fence's own markers: repair is impossible by definition,
        // so only the hard invariants are asserted.
        for (var group = 0; group < 25; group++)
        {
            var limit = random.Next(8, 39);
            var text = Generate(limit, lineInitialFences: true, random.Next(1, 40));
            var label = $"tiny group={group} limit={limit} text={Escape(text)}";

            var fenced = ChatTextChunker.SplitText(text, limit, preserveMarkdownFences: true);
            Assert.All(fenced, chunk => Assert.True(chunk.Length <= limit, $"{label} len={chunk.Length}"));
            Assert.All(fenced, chunk => Assert.False(HasLoneSurrogate(chunk), label));

            var raw = ChatTextChunker.SplitText(text, limit);
            Assert.All(raw, chunk => Assert.True(chunk.Length <= limit, $"{label} len={chunk.Length}"));
            Assert.Equal(text, string.Concat(raw));

            tinyGroups++;
        }

        // Phase 3 - hostile input where marker runs appear mid-line, so a split can land right before one
        // and the next chunk starts with a line-initial-looking marker. Documented residual: only the hard
        // invariants hold here.
        for (var group = 0; group < 25; group++)
        {
            var limit = random.Next(8, 60);
            var text = Generate(limit, lineInitialFences: false, random.Next(1, 40));
            var label = $"hostile group={group} limit={limit} text={Escape(text)}";

            var fenced = ChatTextChunker.SplitText(text, limit, preserveMarkdownFences: true);
            Assert.All(fenced, chunk => Assert.True(chunk.Length <= limit, $"{label} len={chunk.Length}"));
            Assert.All(fenced, chunk => Assert.False(HasLoneSurrogate(chunk), label));

            var raw = ChatTextChunker.SplitText(text, limit);
            Assert.All(raw, chunk => Assert.True(chunk.Length <= limit, $"{label} len={chunk.Length}"));
            Assert.Equal(text, string.Concat(raw));

            hostileGroups++;
        }

        Assert.Equal(150, strongGroups);
        Assert.Equal(25, tinyGroups);
        Assert.Equal(25, hostileGroups);
    }

    // ---- independent helpers (deliberately not reusing the production parser) --------------------

    private static string Escape(string text) => text.Replace("\n", "\\n", StringComparison.Ordinal);

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

    /// <summary>True when the chunk ends with no fence open (every fence it opens is closed inside it).</summary>
    private static bool IsFenceBalanced(string chunk)
    {
        var open = false;
        var marker = '\0';
        var run = 0;

        foreach (var raw in chunk.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!TryParseFence(line, out var lineMarker, out var lineRun, out var info))
                continue;

            if (!open)
            {
                open = true;
                marker = lineMarker;
                run = lineRun;
                continue;
            }

            if (lineMarker == marker && lineRun >= run && info.Length == 0)
                open = false;
        }

        return !open;
    }

    /// <summary>
    /// Returns the marker text (e.g. <c>```</c>) when the source text ends while a code fence is still
    /// open, otherwise null.
    /// </summary>
    private static string? OpenFenceAtEnd(string text)
    {
        string? markerText = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!TryParseFence(line, out var marker, out var run, out var info))
                continue;

            if (markerText is null)
            {
                markerText = new string(marker, run);
                continue;
            }

            if (marker == markerText[0] && run >= markerText.Length && info.Length == 0)
                markerText = null;
        }

        return markerText;
    }

    private static bool TryParseFence(string line, out char marker, out int run, out string info)
    {
        marker = '\0';
        run = 0;
        info = string.Empty;

        var index = 0;
        while (index < line.Length && line[index] == ' ')
            index++;

        if (index > 3 || index >= line.Length)
            return false;

        var candidate = line[index];
        if (candidate is not ('`' or '~'))
            return false;

        var count = 0;
        while (index + count < line.Length && line[index + count] == candidate)
            count++;

        if (count < 3)
            return false;

        var tail = line[(index + count)..].Trim();
        if (candidate == '`' && tail.Contains('`'))
            return false;

        marker = candidate;
        run = count;
        info = tail;
        return true;
    }

    /// <summary>
    /// Rebuilds the source text from chunks by removing the fence markers the chunker synthesised.
    /// A synthesised pair is recognised structurally: chunk[i] ends with a fence-closing marker line and
    /// chunk[i+1] starts with the same marker line. Only when both halves are present are they dropped —
    /// if the chunker repaired just one side, the pair does not match and this reconstruction fails,
    /// which is exactly what the equivalence assertion needs to catch.
    /// </summary>
    /// <param name="openAtEnd">
    /// Marker text (e.g. <c>```</c>) when the source text itself ends inside a fence, otherwise null.
    /// That final close was synthesised too and must be removed.
    /// </param>
    private static string Reassemble(IReadOnlyList<string> chunks, string? openAtEnd)
    {
        var builder = new StringBuilder();

        for (var index = 0; index < chunks.Count; index++)
        {
            var chunk = chunks[index];

            if (index > 0
                && TryTakeOpeningMarker(chunk, out var markerText, out var remainder)
                && TryStripTrailingMarker(builder, markerText))
            {
                builder.Append(remainder);
                continue;
            }

            builder.Append(chunk);
        }

        if (openAtEnd is not null)
            Assert.True(TryStripTrailingMarker(builder, openAtEnd), "the source fence was left unclosed");

        return builder.ToString();
    }

    private static bool TryTakeOpeningMarker(string chunk, out string markerText, out string remainder)
    {
        markerText = string.Empty;
        remainder = chunk;

        var newline = chunk.IndexOf('\n', StringComparison.Ordinal);
        if (newline <= 0)
            return false;

        var firstLine = chunk[..newline];
        if (!TryParseFence(firstLine, out var marker, out var run, out _))
            return false;

        markerText = new string(marker, run);
        remainder = chunk[(newline + 1)..];
        return true;
    }

    /// <summary>Removes one synthesised closing marker, which the chunker always emits as "\n" + marker.</summary>
    private static bool TryStripTrailingMarker(StringBuilder builder, string markerText)
    {
        var withNewline = "\n" + markerText;
        if (builder.Length < withNewline.Length || !EndsWith(builder, withNewline))
            return false;

        builder.Length -= withNewline.Length;
        return true;
    }

    private static bool EndsWith(StringBuilder builder, string value)
    {
        if (builder.Length < value.Length)
            return false;

        for (var index = 0; index < value.Length; index++)
        {
            if (builder[builder.Length - value.Length + index] != value[index])
                return false;
        }

        return true;
    }
}
