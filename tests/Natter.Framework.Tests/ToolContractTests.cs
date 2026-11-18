using System.Net;
using Natter.Application.Chat;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>P5 Gate D tool contract checks (read-only, no shell/file write, bounded output).</summary>
public sealed class ToolContractTests
{
    [Fact]
    public async Task Text_transform_uppers_and_reports_length()
    {
        var tool = new TextTransformTool();
        var result = await tool.ExecuteAsync(new ToolRequest("hello"));
        Assert.True(result.Ok);
        Assert.Equal("HELLO (chars=5)", result.Output);
    }

    [Fact]
    public async Task Http_fetch_rejects_non_https()
    {
        var tool = new HttpFetchTool(new HttpClient(new StubHandler(HttpStatusCode.OK, "ok")), new HashSet<string> { "example.com" });
        var result = await tool.ExecuteAsync(new ToolRequest("http://example.com/x"));
        Assert.False(result.Ok);
        Assert.Equal("invalid_url", result.ErrorCode);
    }

    [Fact]
    public async Task Http_fetch_rejects_host_outside_allowlist()
    {
        var tool = new HttpFetchTool(new HttpClient(new StubHandler(HttpStatusCode.OK, "ok")), new HashSet<string> { "example.com" });
        var result = await tool.ExecuteAsync(new ToolRequest("https://evil.example/x"));
        Assert.False(result.Ok);
        Assert.Equal("host_not_allowed", result.ErrorCode);
    }

    [Fact]
    public async Task Http_fetch_maps_non_2xx_to_http_error()
    {
        var tool = new HttpFetchTool(new HttpClient(new StubHandler(HttpStatusCode.NotFound, "nope")), new HashSet<string> { "example.com" });
        var result = await tool.ExecuteAsync(new ToolRequest("https://example.com/missing"));
        Assert.False(result.Ok);
        Assert.Equal("http_error", result.ErrorCode);
        Assert.Contains("404", result.ErrorMessage);
    }

    [Fact]
    public async Task Http_fetch_honours_cancellation()
    {
        var tool = new HttpFetchTool(new HttpClient(new StubHandler(HttpStatusCode.OK, "ok", delay: TimeSpan.FromSeconds(2))), new HashSet<string> { "example.com" });
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await tool.ExecuteAsync(new ToolRequest("https://example.com/slow"), cts.Token));
    }

    [Fact]
    public async Task Http_fetch_truncates_output_to_max_bytes()
    {
        var body = new string('x', 200_000);
        var tool = new HttpFetchTool(new HttpClient(new StubHandler(HttpStatusCode.OK, body)), new HashSet<string> { "example.com" }, maxBytes: 1024);
        var result = await tool.ExecuteAsync(new ToolRequest("https://example.com/big"));
        Assert.True(result.Ok);
        Assert.True(result.Output.Length <= 1024);
    }

    [Fact]
    public void Read_only_policy_rejects_shell_and_file_write()
    {
        var policy = new ReadOnlyToolPolicy();
        var profile = new RuntimeProfile { Policy = new ProfilePolicy { AllowNetworkTools = false } };
        Assert.False(policy.IsAllowed(new FakeTool(ToolCapabilities.ReadOnly | ToolCapabilities.Shell), profile));
        Assert.False(policy.IsAllowed(new FakeTool(ToolCapabilities.ReadOnly | ToolCapabilities.FileWrite), profile));
        Assert.False(policy.IsAllowed(new FakeTool(ToolCapabilities.ReadOnly | ToolCapabilities.Network), profile));
        Assert.True(policy.IsAllowed(new FakeTool(ToolCapabilities.ReadOnly), profile));
    }

    private sealed class FakeTool(ToolCapabilities capabilities) : ITool
    {
        public string Name => "fake";
        public string Description => "fake";
        public ToolCapabilities Capabilities => capabilities;
        public ValueTask<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ToolResult(""));
    }

    private sealed class StubHandler(HttpStatusCode status, string body, TimeSpan? delay = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (delay is { } wait)
                await Task.Delay(wait, cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }
}
