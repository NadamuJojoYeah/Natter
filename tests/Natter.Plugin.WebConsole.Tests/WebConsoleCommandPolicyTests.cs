using Xunit;

namespace Natter.Plugin.WebConsole.Tests;

public sealed class WebConsoleCommandPolicyTests
{
    private static readonly string[] Allowlist = ["health", "plugins list", "upstreams", "memory list"];

    [Fact]
    public void Allowlisted_commands_are_normalized_and_allowed()
    {
        var decision = WebConsoleCommandPolicy.Evaluate(
            new WebConsoleCommandRequest("  plugins   list ", ["--detail"]),
            Allowlist);

        Assert.True(decision.Allowed);
        Assert.Equal("command.allowed", decision.Code);
        Assert.Equal("plugins list", decision.Command);
        Assert.Equal(["--detail"], decision.Args);
    }

    [Fact]
    public void Unknown_command_is_rejected()
    {
        var decision = WebConsoleCommandPolicy.Evaluate(
            new WebConsoleCommandRequest("rm", ["-rf", "/"]),
            Allowlist);

        Assert.False(decision.Allowed);
        Assert.Equal("command.not_allowed", decision.Code);
    }

    [Fact]
    public void Empty_allowlist_rejects_everything()
    {
        var decision = WebConsoleCommandPolicy.Evaluate(
            new WebConsoleCommandRequest("health", []),
            []);

        Assert.False(decision.Allowed);
        Assert.Equal("command.allowlist_empty", decision.Code);
    }

    [Fact]
    public void Empty_command_is_invalid()
    {
        var decision = WebConsoleCommandPolicy.Evaluate(
            new WebConsoleCommandRequest("   ", []),
            Allowlist);

        Assert.False(decision.Allowed);
        Assert.Equal("command.invalid", decision.Code);
    }

    [Fact]
    public void Too_many_or_too_long_arguments_are_rejected()
    {
        var tooMany = WebConsoleCommandPolicy.Evaluate(
            new WebConsoleCommandRequest("health", Enumerable.Repeat("a", WebConsoleCommandPolicy.MaxArgs + 1).ToArray()),
            Allowlist);
        Assert.Equal("command.invalid", tooMany.Code);

        var tooLong = WebConsoleCommandPolicy.Evaluate(
            new WebConsoleCommandRequest("health", [new string('a', WebConsoleCommandPolicy.MaxArgLength + 1)]),
            Allowlist);
        Assert.Equal("command.invalid", tooLong.Code);
    }

    [Fact]
    public void Control_characters_are_rejected()
    {
        var decision = WebConsoleCommandPolicy.Evaluate(
            new WebConsoleCommandRequest("health\nrm", []),
            Allowlist);

        Assert.Equal("command.invalid", decision.Code);
    }

    [Fact]
    public void Request_parser_accepts_command_and_args()
    {
        var ok = WebConsoleCommandPolicy.TryParseRequest(
            """{"command":"health","args":["--json"]}""",
            out var request,
            out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("health", request.Command);
        Assert.Equal(["--json"], request.Args);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("""{"args":[]}""")]
    [InlineData("""{"command":"health","args":"--json"}""")]
    [InlineData("""{"command":"health","args":[1]}""")]
    [InlineData("{not-json}")]
    public void Request_parser_rejects_invalid_bodies(string body)
    {
        Assert.False(WebConsoleCommandPolicy.TryParseRequest(body, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Output_normalization_redacts_secrets()
    {
        var output = WebConsoleCommandPolicy.NormalizeOutput(
            "token sk-abcdefghijklmnop",
            "authorization: Bearer abc.def.ghi");

        Assert.Contains("[redacted]", output.Stdout);
        Assert.Contains("[redacted]", output.Stderr);
        Assert.DoesNotContain("sk-abcdefghijklmnop", output.Stdout);
        Assert.False(output.Truncated);
    }

    [Fact]
    public void Output_normalization_truncates_large_output()
    {
        var stdout = new string('x', WebConsoleCommandPolicy.MaxOutputBytes + 4096);

        var output = WebConsoleCommandPolicy.NormalizeOutput(stdout, "stderr");

        Assert.True(output.Truncated);
        Assert.True(output.OriginalBytes > WebConsoleCommandPolicy.MaxOutputBytes);
        Assert.EndsWith("…", output.Stdout);
        Assert.Empty(output.Stderr);
    }

    [Fact]
    public void Output_normalization_allocates_budget_across_streams()
    {
        var stdout = new string('a', WebConsoleCommandPolicy.MaxOutputBytes - 10);
        var output = WebConsoleCommandPolicy.NormalizeOutput(stdout, "0123456789abcdef");

        Assert.True(output.Truncated);
        Assert.True(output.Stdout.Length + output.Stderr.Length <= WebConsoleCommandPolicy.MaxOutputBytes);
    }
}
