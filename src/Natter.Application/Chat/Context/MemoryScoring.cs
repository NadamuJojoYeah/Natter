namespace Natter.Application.Chat;

/// <summary>
/// Shared BM25-ish scoring fallback for stores without a native index. SQLite uses FTS5 BM25 and
/// applies the same decay; InMemory uses token matches so both implementations agree on ordering.
/// </summary>
public static class MemoryScoring
{
    /// <summary>
    /// Pre-tokenization for the FTS index (P6 Q8): CJK runs become bi-grams plus singles so any
    /// two-character Chinese word is indexed; non-CJK text is split on whitespace/punctuation.
    /// </summary>
    public static IReadOnlyList<string> TokenizeForIndex(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var tokens = new List<string>();
        var cjk = new System.Text.StringBuilder();
        var word = new System.Text.StringBuilder();

        void FlushCjk()
        {
            if (cjk.Length == 0)
                return;

            var run = cjk.ToString();
            for (var index = 0; index + 1 < run.Length; index++)
                tokens.Add(run.Substring(index, 2));
            foreach (var character in run)
                tokens.Add(character.ToString());
            cjk.Clear();
        }

        void FlushWord()
        {
            if (word.Length == 0)
                return;
            tokens.Add(word.ToString().ToLowerInvariant());
            word.Clear();
        }

        foreach (var character in text)
        {
            if (IsCjk(character))
            {
                FlushWord();
                cjk.Append(character);
                continue;
            }

            FlushCjk();
            if (char.IsLetterOrDigit(character))
                word.Append(character);
            else
                FlushWord();
        }

        FlushCjk();
        FlushWord();
        return tokens.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Space-separated pre-tokenized copy stored in memory_items.text_index.</summary>
    public static string BuildIndexText(string? text) => string.Join(' ', TokenizeForIndex(text));

    public static bool IsCjk(char character)
        => character is >= '\u3400' and <= '\u4DBF'
           or >= '\u4E00' and <= '\u9FFF'
           or >= '\uF900' and <= '\uFAFF';

    public static IReadOnlyList<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var tokens = new List<string>();
        var builder = new System.Text.StringBuilder();
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (builder.Length > 0)
            {
                tokens.Add(builder.ToString());
                builder.Clear();
            }
        }

        if (builder.Length > 0)
            tokens.Add(builder.ToString());
        return tokens;
    }

    public static double Score(string text, string query, DateTimeOffset updatedAt, DateTimeOffset now, TimeSpan halfLife)
    {
        var queryTokens = Tokenize(query);
        if (queryTokens.Count == 0)
            return 0;

        var haystack = text.ToLowerInvariant();
        var matches = queryTokens.Count(token => haystack.Contains(token, StringComparison.Ordinal));
        var relevance = (double)matches / queryTokens.Count;

        var ageDays = Math.Max(0, (now - updatedAt).TotalDays);
        // v1 live path keeps its historical exponential decay so this release does not change recall
        // behaviour (§5.1's power law is the v2 shape). Single implementation per mode lives in
        // MemoryDecay: LegacyRetention for v1, Retention for v2. Retired with v1 at P6.7.
        var stabilityDays = halfLife.TotalDays > 0 ? halfLife.TotalDays : 30d;
        var decay = MemoryDecay.LegacyRetention(ageDays, stabilityDays);
        return relevance * decay;
    }
}
