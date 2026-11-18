using System.Text.Json;
using Xunit;

namespace Natter.Plugin.WebConsole.Tests;

public sealed class WebConsoleRedactionTests
{
    [Fact]
    public void Secret_references_are_preserved_and_sensitive_values_masked()
    {
        var raw = JsonSerializer.SerializeToElement(new
        {
            token = "${secret:WEBCONSOLE_TOKEN}",
            nested = new
            {
                password = "hunter2",
                apiKey = "sk-abcdefghijklmnop",
                note = "safe",
            },
            items = new[] { new { secret = "value" } },
        });

        var (sanitized, redacted) = WebConsoleRedaction.Sanitize(raw);

        Assert.Equal("${secret:WEBCONSOLE_TOKEN}", sanitized.GetProperty("token").GetString());
        Assert.Equal("***", sanitized.GetProperty("nested").GetProperty("password").GetString());
        Assert.Equal("***", sanitized.GetProperty("nested").GetProperty("apiKey").GetString());
        Assert.Equal("safe", sanitized.GetProperty("nested").GetProperty("note").GetString());
        Assert.Equal("***", sanitized.GetProperty("items")[0].GetProperty("secret").GetString());

        Assert.Contains("$.nested.password", redacted);
        Assert.Contains("$.nested.apiKey", redacted);
        Assert.Contains("$.items[0].secret", redacted);
        Assert.DoesNotContain("$.token", redacted);
    }

    [Fact]
    public void Long_strings_are_truncated()
    {
        var raw = JsonSerializer.SerializeToElement(new { description = new string('a', 2500) });
        var (sanitized, _) = WebConsoleRedaction.Sanitize(raw);

        var value = sanitized.GetProperty("description").GetString()!;
        Assert.EndsWith("…(truncated)", value);
        Assert.Equal(2000 + "…(truncated)".Length, value.Length);
    }

    [Theory]
    [InlineData("sk-abcdefghijklmnop")]
    [InlineData("Bearer abc.def.ghi")]
    public void Text_sanitizer_redacts_known_token_shapes(string input)
    {
        var result = WebConsoleRedaction.SanitizeText(input);
        Assert.Equal("[redacted]", result);
    }

    [Fact]
    public void Text_sanitizer_truncates_without_leaking()
    {
        var result = WebConsoleRedaction.SanitizeText(new string('x', 400), 50);
        Assert.Equal(51, result.Length);
        Assert.EndsWith("…", result);
    }
}
