namespace Natter.Application.Chat;

/// <summary>
/// Shared BM25-ish scoring fallback for stores without a native index. SQLite uses FTS5 BM25 and
/// applies the same decay; InMemory uses token matches so both implementations agree on ordering.
/// </summary>
public static class MemoryScoring
{
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
        var halfLifeDays = Math.Max(1e-6, halfLife.TotalDays);
        var decay = Math.Exp(-ageDays / halfLifeDays);
        return relevance * decay;
    }
}
