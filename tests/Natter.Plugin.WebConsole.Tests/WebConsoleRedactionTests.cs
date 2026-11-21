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

    [Theory]
    [InlineData("token=abc123", "token=[redacted]")]
    [InlineData("API_KEY=XYZ-123", "API_KEY=[redacted]")]
    [InlineData("password: hunter2", "password: [redacted]")]
    [InlineData("secret = s3cr3t-value", "secret = [redacted]")]
    [InlineData("--api-key=plain-value", "--api-key=[redacted]")]
    [InlineData("{\"token\": \"abc123\"}", "{\"token\": \"[redacted]\"}")]
    public void Text_sanitizer_redacts_key_value_secrets_and_keeps_the_key(string input, string expected)
        => Assert.Equal(expected, WebConsoleRedaction.SanitizeText(input));

    [Fact]
    public void Text_sanitizer_keeps_secret_references_inside_key_value_pairs()
    {
        var result = WebConsoleRedaction.SanitizeText("token=${secret:WEBCONSOLE_TOKEN}");

        Assert.Equal("token=${secret:WEBCONSOLE_TOKEN}", result);
    }

    [Fact]
    public void Sanitized_config_redacts_key_value_pairs_inside_free_text()
    {
        var raw = JsonSerializer.SerializeToElement(new { note = "token=abc123 and password: hunter2" });

        var (sanitized, redacted) = WebConsoleRedaction.Sanitize(raw);

        var note = sanitized.GetProperty("note").GetString()!;
        Assert.DoesNotContain("abc123", note);
        Assert.DoesNotContain("hunter2", note);
        Assert.Single(redacted);
        Assert.Contains("$.note", redacted);
    }

    [Fact]
    public void Text_sanitizer_truncates_without_leaking()
    {
        var result = WebConsoleRedaction.SanitizeText(new string('x', 400), 50);
        Assert.Equal(51, result.Length);
        Assert.EndsWith("…", result);
    }
}
