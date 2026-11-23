using Xunit;

namespace Natter.Plugin.WebConsole.Tests;

public sealed class WebConsoleConfigEditTests
{
    [Fact]
    public void Valid_object_is_accepted_and_gets_stable_etag()
    {
        var content = """{"port": 5090, "nested": {"enabled": true}}""";

        var first = WebConsoleConfigEditPolicy.Validate("profiles", content);
        var second = WebConsoleConfigEditPolicy.Validate("profiles", content);

        Assert.True(first.Valid);
        Assert.Empty(first.Issues);
        Assert.False(first.ContainsSecretLiteral);
        Assert.StartsWith("sha256:", first.ETag);
        Assert.Equal(first.ETag, second.ETag);
        Assert.NotEqual(first.ETag, WebConsoleConfigEditPolicy.ComputeETag(content + " "));
    }

    [Fact]
    public void Invalid_json_reports_an_issue()
    {
        var result = WebConsoleConfigEditPolicy.Validate("profiles", "{not-json}");

        Assert.False(result.Valid);
        Assert.Contains(result.Issues, issue => issue.Code == "config.invalid_json");
    }

    [Fact]
    public void Root_scalar_is_rejected()
    {
        var result = WebConsoleConfigEditPolicy.Validate("profiles", "\"just a string\"");

        Assert.False(result.Valid);
        Assert.Contains(result.Issues, issue => issue.Code == "config.root_type");
    }

    [Fact]
    public void Secret_literals_are_rejected_but_secret_references_are_allowed()
    {
        var withReference = """{"token": "${secret:WEBCONSOLE_TOKEN}"}""";
        var withLiteral = """{"token": "plain-token-value"}""";
        var withSk = """{"apiKey": "sk-abcdefghijklmnop"}""";

        Assert.True(WebConsoleConfigEditPolicy.Validate("profiles", withReference).Valid);

        var literal = WebConsoleConfigEditPolicy.Validate("profiles", withLiteral);
        Assert.False(literal.Valid);
        Assert.True(literal.ContainsSecretLiteral);
        Assert.Contains(literal.Issues, issue => issue.Code == "config.secret_literal");

        var sk = WebConsoleConfigEditPolicy.Validate("profiles", withSk);
        Assert.True(sk.ContainsSecretLiteral);
    }

    [Fact]
    public void Oversized_content_is_rejected()
    {
        var content = "{\"padding\":\"" + new string('a', WebConsoleConfigEditPolicy.MaxContentBytes) + "\"}";
        var result = WebConsoleConfigEditPolicy.Validate("profiles", content);

        Assert.False(result.Valid);
        Assert.Contains(result.Issues, issue => issue.Code == "config.too_large");
    }

    [Fact]
    public void Nul_characters_are_rejected()
    {
        var result = WebConsoleConfigEditPolicy.Validate("profiles", "{\"a\":\"x\0y\"}");

        Assert.Contains(result.Issues, issue => issue.Code == "config.control_chars");
    }

    [Theory]
    [InlineData("profiles", true)]
    [InlineData("plugin:natter.tool.admin.webconsole", true)]
    [InlineData("plugin:natter.sample.event.metrics", true)]
    [InlineData("plugin:../escape", false)]
    [InlineData("plugin:", false)]
    [InlineData("secrets", false)]
    [InlineData("", false)]
    public void Config_ids_follow_the_whitelist(string id, bool expected)
    {
        Assert.Equal(expected, WebConsoleConfigEditPolicy.IsAllowedId(id));
    }

    [Fact]
    public void Diff_reports_added_removed_and_changed_lines()
    {
        var diff = WebConsoleConfigEditPolicy.Diff("a\nb\nc\n", "a\nx\nc\n");

        Assert.Equal(1, diff.Added);
        Assert.Equal(1, diff.Removed);
        Assert.Equal(1, diff.Changed);
        Assert.False(diff.Truncated);
        Assert.Contains("-b", diff.Lines);
        Assert.Contains("+x", diff.Lines);
        Assert.Contains(" a", diff.Lines);
    }

    [Fact]
    public void Diff_handles_pure_insertions_and_deletions()
    {
        var added = WebConsoleConfigEditPolicy.Diff("a\nb\n", "a\nb\nc\n");
        Assert.Equal(1, added.Added);
        Assert.Equal(0, added.Removed);

        var removed = WebConsoleConfigEditPolicy.Diff("a\nb\nc\n", "a\nb\n");
        Assert.Equal(0, removed.Added);
        Assert.Equal(1, removed.Removed);
    }

    [Fact]
    public void Diff_falls_back_for_large_inputs()
    {
        var before = string.Join('\n', Enumerable.Range(0, 1200).Select(index => $"line {index}"));
        var after = string.Join('\n', Enumerable.Range(0, 1200).Select(index => $"line {index} changed"));

        var diff = WebConsoleConfigEditPolicy.Diff(before, after);

        Assert.True(diff.Truncated);
        Assert.Empty(diff.Lines);
        Assert.Equal(1200, diff.Added);
        Assert.Equal(1200, diff.Removed);
    }

    [Fact]
    public void Diff_caps_preview_lines()
    {
        var before = "same";
        var after = "same\n" + string.Join('\n', Enumerable.Range(0, 500).Select(index => $"new {index}"));

        var diff = WebConsoleConfigEditPolicy.Diff(before, after);

        Assert.True(diff.Truncated);
        Assert.True(diff.Lines.Count <= 200);
        Assert.Equal(500, diff.Added);
    }
}
