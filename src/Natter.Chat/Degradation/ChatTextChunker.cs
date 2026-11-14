using System.Globalization;
using System.Text;

namespace Natter.Chat;

/// <summary>
/// Splits long text on readable boundaries (text elements, never inside a surrogate pair).
/// Attachments and mentions stay on the first chunk; the last chunk carries the tail.
/// </summary>
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

        var pieces = SplitText(text, limit);
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

    /// <summary>Splits text into chunks whose UTF-16 length is at most <paramref name="maxLength"/>.</summary>
    public static IReadOnlyList<string> SplitText(string text, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);

        if (text.Length <= maxLength)
            return [text];

        var chunks = new List<string>();
        var current = new StringBuilder(maxLength);
        var enumerator = StringInfo.GetTextElementEnumerator(text);

        while (enumerator.MoveNext())
        {
            var element = (string)enumerator.Current;

            if (current.Length > 0 && current.Length + element.Length > maxLength)
            {
                FlushAtWordBoundary(current, chunks);
            }

            current.Append(element);
        }

        if (current.Length > 0)
            chunks.Add(current.ToString());

        return chunks;
    }

    private static void FlushAtWordBoundary(StringBuilder current, List<string> chunks)
    {
        var text = current.ToString();
        var boundary = FindBoundary(text);

        if (boundary > 0 && boundary < text.Length)
        {
            chunks.Add(text[..boundary]);
            current.Clear();
            current.Append(text[boundary..]);
            return;
        }

        chunks.Add(text);
        current.Clear();
    }

    private static int FindBoundary(string text)
    {
        var newline = text.LastIndexOf('\n');
        var space = text.LastIndexOf(' ');
        var boundary = Math.Max(newline, space);

        return boundary >= 0 ? boundary + 1 : -1;
    }
}
