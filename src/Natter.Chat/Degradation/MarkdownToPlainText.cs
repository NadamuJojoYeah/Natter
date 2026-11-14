using System.Text.RegularExpressions;

namespace Natter.Chat;

/// <summary>Conservative Markdown → readable plain-text conversion used by capability degradation.</summary>
internal static partial class MarkdownToPlainText
{
    public static string Convert(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return markdown ?? string.Empty;

        var text = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);

        text = ImagePattern().Replace(text, "$1 ($2)");
        text = LinkPattern().Replace(text, "$1 ($2)");
        text = HeadingPattern().Replace(text, string.Empty);
        text = QuotePattern().Replace(text, string.Empty);
        text = FencePattern().Replace(text, string.Empty);

        text = text.Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("__", string.Empty, StringComparison.Ordinal)
            .Replace("~~", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal);

        text = SingleEmphasisPattern().Replace(text, "$1");

        return text.Trim();
    }

    [GeneratedRegex(@"!\[([^\]]*)\]\(([^)]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex ImagePattern();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"(?m)^\s{0,3}#{1,6}\s+", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"(?m)^\s*>\s?", RegexOptions.CultureInvariant)]
    private static partial Regex QuotePattern();

    [GeneratedRegex(@"(?m)^\s*```.*$", RegexOptions.CultureInvariant)]
    private static partial Regex FencePattern();

    [GeneratedRegex(@"(\*|_)(\S(?:[^*_]*\S)?)\1", RegexOptions.CultureInvariant)]
    private static partial Regex SingleEmphasisPattern();
}
