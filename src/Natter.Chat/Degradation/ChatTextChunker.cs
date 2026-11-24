using System.Globalization;
using System.Text;

namespace Natter.Chat;

/// <summary>
/// Splits long text on readable boundaries (text elements, never inside a surrogate pair) and keeps
/// every produced chunk inside the platform limit.
/// Attachments and mentions stay on the first chunk; the last chunk carries the tail.
/// </summary>
/// <remarks>
/// Guarantees for every returned chunk:
/// <list type="number">
///   <item><c>chunk.Length &lt;= maxLength</c> in UTF-16 code units — the unit adapter limits use.
///   A platform that rejects an over-long message would drop the whole reply, so this is an invariant,
///   not a preference.</item>
///   <item>A surrogate pair is never cut in half. The only documented deviation is a limit of 1 combined
///   with a supplementary-plane character: cutting the pair would emit invalid UTF-16, so the rune is kept
///   intact and that single chunk is two code units long. Real adapter limits are far above 1.</item>
/// </list>
/// When markdown fences are preserved, a chunk that ends inside a fenced code block is closed and the
/// following chunk reopens the same fence (marker plus info string), so every chunk renders on its own.
/// Scope of that repair:
/// <list type="bullet">
///   <item>Only fences that are line-initial in the source are tracked — that is what markdown itself
///   considers a fence. A ``` or ~~~ run in the middle of a line stays ordinary text.</item>
///   <item>A fence is only repaired when the limit can hold its opening marker (with info string), its
///   closing marker and at least one character of content. Below that the fence degrades to plain text
///   and the length invariant wins; real adapter limits are orders of magnitude above it.</item>
///   <item>Residual, not repaired: if a chunk boundary happens to fall exactly before a mid-line marker
///   run, the next chunk starts with that run and the renderer may show it as an opening fence. Avoiding
///   it would mean shifting an already-full chunk, i.e. dropping content, so it is left as is.</item>
/// </list>
/// </remarks>
public static class ChatTextChunker
{
    public static IReadOnlyList<Content> Chunk(Content content, int? maxMessageLength)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (maxMessageLength is not { } limit || limit <= 0)
            return [content];

        var text = content.Text ?? string.Empty;
        if (text.Length <= limit)
            return [content];

        // Fence repair is only meaningful for markdown; injecting fence markers into plain text would add
        // characters the platform displays literally.
        var pieces = SplitText(text, limit, preserveMarkdownFences: content.Format == ContentFormat.Markdown);
        var chunks = new List<Content>(pieces.Count);

        for (var index = 0; index < pieces.Count; index++)
        {
            chunks.Add(new Content(
                pieces[index],
                content.Format,
                index == 0 ? content.Attachments : null,
                index == 0 ? content.Mentions : null));
        }

        return chunks;
    }

    /// <summary>
    /// Splits text into chunks whose UTF-16 length is at most <paramref name="maxLength"/>, without
    /// markdown fence awareness.
    /// </summary>
    public static IReadOnlyList<string> SplitText(string text, int maxLength)
        => SplitText(text, maxLength, preserveMarkdownFences: false);

    /// <summary>
    /// Splits text into chunks whose UTF-16 length is at most <paramref name="maxLength"/>.
    /// When <paramref name="preserveMarkdownFences"/> is true, a chunk that ends inside a ``` or ~~~
    /// code fence is closed and the next chunk reopens the same fence.
    /// </summary>
    public static IReadOnlyList<string> SplitText(string text, int maxLength, bool preserveMarkdownFences)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);

        if (text.Length <= maxLength)
            return [text];

        var builder = new ChunkBuilder(maxLength, preserveMarkdownFences);
        builder.Append(text);
        return builder.Build();
    }

    /// <summary>A recognised code fence: the marker character, its run length and the info string.</summary>
    private readonly record struct Fence(char Marker, int Length, string Info);

    /// <summary>
    /// Packs source text into chunks while tracking fence state. Fence state is global to the source text;
    /// the marker lengths are reserved before content is appended so a chunk can always be closed/reopened
    /// without exceeding the limit.
    /// </summary>
    private sealed class ChunkBuilder(int maxLength, bool fenceAware)
    {
        private readonly List<string> _chunks = [];
        private readonly StringBuilder _current = new();

        /// <summary>Fence open in the source text at the current position, if any.</summary>
        private Fence? _fence;

        /// <summary>Fence that was open when the current chunk started (drives the reopening prefix).</summary>
        private Fence? _fenceAtChunkStart;

        private int PrefixLength => _fenceAtChunkStart is { } fence ? OpenLength(fence) : 0;

        private int SuffixLength => _fence is { } fence ? CloseLength(fence) : 0;

        /// <summary>Code units the current chunk may still hold as content.</summary>
        private int ContentBudget => Math.Max(1, maxLength - PrefixLength - SuffixLength);

        public void Append(string text)
        {
            var start = 0;
            while (start < text.Length)
            {
                var newline = text.IndexOf('\n', start);
                var end = newline < 0 ? text.Length : newline + 1;
                AppendLine(text[start..end]);
                start = end;
            }
        }

        public IReadOnlyList<string> Build()
        {
            Flush(allowCarry: false);

            if (!_chunks.Exists(chunk => chunk.Length > maxLength))
                return _chunks;

            // Not reachable: every append path reserves its markers. Kept as a hard backstop so a future
            // edit cannot silently ship an over-long chunk to a platform that rejects the whole message.
            var repaired = new List<string>(_chunks.Count);
            foreach (var chunk in _chunks)
            {
                if (chunk.Length <= maxLength)
                {
                    repaired.Add(chunk);
                    continue;
                }

                repaired.AddRange(HardCut(chunk, maxLength));
            }

            return repaired;
        }

        private void AppendLine(string line)
        {
            var content = line.AsSpan();
            if (content.Length > 0 && content[^1] == '\n')
                content = content[..^1];
            if (content.Length > 0 && content[^1] == '\r')
                content = content[..^1];

            if (fenceAware
                && TryParseFenceLine(content, out var candidate)
                && IsActiveFence(candidate))
            {
                AppendFenceLine(line, candidate);
                return;
            }

            AppendSegment(line);
        }

        /// <summary>
        /// True when the line really toggles fence state: it opens a fence that leaves room for its own
        /// markers, or it is the matching closing fence (same marker, at least as long, no info string).
        /// Anything else — a nested ~~~~ inside ```, a shorter run, a fence with an info string while
        /// already inside a block — is literal content.
        /// </summary>
        private bool IsActiveFence(Fence candidate)
        {
            if (_fence is not { } open)
                return OpenLength(candidate) + CloseLength(candidate) + 1 <= maxLength;

            return candidate.Marker == open.Marker
                && candidate.Length >= open.Length
                && candidate.Info.Length == 0;
        }

        private void AppendFenceLine(string line, Fence candidate)
        {
            var after = _fence is null ? candidate : (Fence?)null;
            var reserve = after is { } opening ? CloseLength(opening) : 0;
            var available = maxLength - PrefixLength - reserve;

            if (line.Length > available)
            {
                // The fence line cannot coexist with the markers it needs; keep the length invariant and
                // treat it as ordinary text (a fence that does not fit is not a fence).
                AppendSegment(line);
                return;
            }

            if (_current.Length > 0 && _current.Length + line.Length > available)
                Flush(allowCarry: false);

            if (_current.Length + line.Length > available)
            {
                AppendSegment(line);
                return;
            }

            _current.Append(line);
            _fence = after;
        }

        private void AppendSegment(string segment)
        {
            var enumerator = StringInfo.GetTextElementEnumerator(segment);
            while (enumerator.MoveNext())
            {
                var element = (string)enumerator.Current;

                if (element.Length > ContentBudget)
                {
                    // A single grapheme longer than the limit (ZWJ emoji family, flag, long combining
                    // sequence) would otherwise produce an over-long chunk the platform rejects.
                    foreach (var piece in HardCut(element, ContentBudget))
                        AppendPiece(piece);

                    continue;
                }

                AppendPiece(element);
            }
        }

        private void AppendPiece(string piece)
        {
            var rest = piece;

            while (rest.Length > 0)
            {
                var budget = ContentBudget;

                if (_current.Length + rest.Length <= budget)
                {
                    _current.Append(rest);
                    return;
                }

                if (_current.Length > 0)
                {
                    Flush(allowCarry: true);

                    // A carried-over tail can still leave the chunk too full: emit it as its own chunk.
                    if (_current.Length > 0 && _current.Length + rest.Length > ContentBudget)
                        Flush(allowCarry: false);

                    continue;
                }

                var take = TrimToRuneBoundary(rest, Math.Min(budget, rest.Length));
                if (take <= 0)
                {
                    // Limit of 1 with a supplementary-plane character: keeping the rune intact is the
                    // lesser evil, because a lone surrogate cannot be encoded.
                    take = Math.Min(2, rest.Length);
                }

                _current.Append(rest[..take]);
                rest = rest[take..];
                Flush(allowCarry: false);
            }
        }

        /// <summary>
        /// Emits the current chunk with its fence prefix/suffix. With <paramref name="allowCarry"/> the
        /// chunk is preferentially broken at the last space of its final line so the next chunk starts on a
        /// word boundary; the tail then moves into the next chunk.
        /// </summary>
        private void Flush(bool allowCarry)
        {
            if (_current.Length == 0)
                return;

            var body = _current.ToString();
            var carry = string.Empty;

            if (allowCarry)
            {
                if (_fence is not null || _fenceAtChunkStart is not null)
                {
                    // Inside a code block a space is not a word boundary — carrying a tail after the last
                    // space would chop a code line in half. Break at the last newline instead.
                    var newline = body.LastIndexOf('\n');
                    if (newline > 0 && newline < body.Length - 1)
                    {
                        var tail = body[(newline + 1)..];
                        if (tail.Length + (_fence is { } openFence ? OpenLength(openFence) : 0) <= maxLength)
                        {
                            carry = tail;
                            body = body[..(newline + 1)];
                        }
                    }
                }
                else
                {
                    // Only carry a tail from the last line: fence lines are always appended whole, so a
                    // boundary inside a line can never move fence state between chunks.
                    var lineStart = body.LastIndexOf('\n') + 1;
                    var space = body.LastIndexOf(' ');
                    if (space > lineStart)
                    {
                        var tail = body[(space + 1)..];
                        if (tail.Length > 0
                            && tail.Length + (_fence is { } fence ? OpenLength(fence) : 0) <= maxLength)
                        {
                            carry = tail;
                            body = body[..(space + 1)];
                        }
                    }
                }
            }

            var prefix = _fenceAtChunkStart is { } startFence ? OpenMarker(startFence) : string.Empty;
            var suffix = _fence is { } endFence ? CloseMarker(endFence) : string.Empty;

            _chunks.Add(prefix + body + suffix);

            _current.Clear();
            if (carry.Length > 0)
                _current.Append(carry);
            _fenceAtChunkStart = _fence;
        }

        private static int OpenLength(Fence fence) => fence.Length + fence.Info.Length + 1;

        private static string OpenMarker(Fence fence)
            => new string(fence.Marker, fence.Length) + fence.Info + "\n";

        /// <summary>Close marker plus the one code unit reserved for its leading newline.</summary>
        private static int CloseLength(Fence fence) => fence.Length + 1;

        /// <summary>
        /// Always emitted as a new line. If the body already ended with a newline this leaves one blank
        /// line inside the code block — cosmetic, and the price for a marker that can be removed
        /// unambiguously (marker + newline is always exactly what was added).
        /// </summary>
        private static string CloseMarker(Fence fence)
            => "\n" + new string(fence.Marker, fence.Length);

        private static bool TryParseFenceLine(ReadOnlySpan<char> line, out Fence fence)
        {
            fence = default;

            var index = 0;
            while (index < line.Length && line[index] == ' ')
                index++;

            if (index > 3 || index >= line.Length)
                return false;

            var marker = line[index];
            if (marker is not ('`' or '~'))
                return false;

            var run = 0;
            while (index + run < line.Length && line[index + run] == marker)
                run++;

            if (run < 3)
                return false;

            var info = line[(index + run)..].Trim();

            // CommonMark: a backtick fence's info string may not contain a backtick.
            if (marker == '`' && info.Contains('`'))
                return false;

            fence = new Fence(marker, run, info.ToString());
            return true;
        }

        private static int TrimToRuneBoundary(string text, int length)
        {
            if (length <= 0)
                return 0;

            if (length >= text.Length)
                return text.Length;

            return char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length])
                ? length - 1
                : length;
        }

        private static IReadOnlyList<string> HardCut(string text, int budget)
        {
            var limit = Math.Max(1, budget);
            var pieces = new List<string>();
            var rest = text;

            while (rest.Length > 0)
            {
                var take = TrimToRuneBoundary(rest, Math.Min(limit, rest.Length));
                if (take <= 0)
                    take = Math.Min(2, rest.Length);

                pieces.Add(rest[..take]);
                rest = rest[take..];
            }

            return pieces;
        }
    }
}
