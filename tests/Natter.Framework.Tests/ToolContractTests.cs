using System.Net;
using System.Net.Sockets;
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
        var tool = new HttpFetchTool(
            new HttpClient(new StubHandler(HttpStatusCode.NotFound, "nope")),
            new HashSet<string> { "example.com" },
            addressResolver: PublicResolver);
        var result = await tool.ExecuteAsync(new ToolRequest("https://example.com/missing"));
        Assert.False(result.Ok);
        Assert.Equal("http_error", result.ErrorCode);
        Assert.Contains("404", result.ErrorMessage);
    }

    [Fact]
    public async Task Http_fetch_honours_cancellation()
    {
        var tool = new HttpFetchTool(
            new HttpClient(new StubHandler(HttpStatusCode.OK, "ok", delay: TimeSpan.FromSeconds(2))),
            new HashSet<string> { "example.com" },
            addressResolver: PublicResolver);
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await tool.ExecuteAsync(new ToolRequest("https://example.com/slow"), cts.Token));
    }

    [Fact]
    public async Task Http_fetch_truncates_output_to_max_bytes()
    {
        var body = new string('x', 200_000);
        var tool = new HttpFetchTool(
            new HttpClient(new StubHandler(HttpStatusCode.OK, body)),
            new HashSet<string> { "example.com" },
            maxBytes: 1024,
            addressResolver: PublicResolver);
        var result = await tool.ExecuteAsync(new ToolRequest("https://example.com/big"));
        Assert.True(result.Ok);
        Assert.True(result.Output.Length <= 1024);
    }

    [Theory]
    [InlineData("https://127.0.0.1/x")]
    [InlineData("https://10.1.2.3/x")]
    [InlineData("https://172.16.5.4/x")]
    [InlineData("https://192.168.1.10/x")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://100.100.100.200/latest/meta-data/")]
    [InlineData("https://[::1]/x")]
    [InlineData("https://[fd00::1]/x")]
    [InlineData("https://[fe80::1]/x")]
    public async Task Http_fetch_rejects_private_and_metadata_literals_without_a_request(string url)
    {
        var uri = new Uri(url);
        var handler = new CountingHandler(HttpStatusCode.OK, "should-not-be-read");
        var tool = new HttpFetchTool(
            new HttpClient(handler),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { uri.Host, uri.DnsSafeHost });

        var result = await tool.ExecuteAsync(new ToolRequest(url));

        Assert.False(result.Ok);
        Assert.Equal("address_not_allowed", result.ErrorCode);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Http_fetch_rejects_hostnames_that_resolve_to_private_addresses_without_a_request()
    {
        var handler = new CountingHandler(HttpStatusCode.OK, "should-not-be-read");
        var tool = new HttpFetchTool(
            new HttpClient(handler),
            new HashSet<string> { "internal.example" },
            addressResolver: Resolver("10.0.0.8"));

        var result = await tool.ExecuteAsync(new ToolRequest("https://internal.example/x"));

        Assert.False(result.Ok);
        Assert.Equal("address_not_allowed", result.ErrorCode);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Http_fetch_rejects_unresolvable_hostnames_without_a_request()
    {
        var handler = new CountingHandler(HttpStatusCode.OK, "should-not-be-read");
        var tool = new HttpFetchTool(
            new HttpClient(handler),
            new HashSet<string> { "missing.example" },
            addressResolver: (_, _) => throw new SocketException((int)SocketError.HostNotFound));

        var result = await tool.ExecuteAsync(new ToolRequest("https://missing.example/x"));

        Assert.False(result.Ok);
        Assert.Equal("address_unresolved", result.ErrorCode);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Http_fetch_rejects_redirect_responses_without_following_them()
    {
        var handler = new CountingHandler(
            HttpStatusCode.Redirect,
            string.Empty,
            location: "https://169.254.169.254/latest/meta-data/");
        var tool = new HttpFetchTool(
            new HttpClient(handler),
            new HashSet<string> { "example.com" },
            addressResolver: PublicResolver);

        var result = await tool.ExecuteAsync(new ToolRequest("https://example.com/redirect"));

        Assert.False(result.Ok);
        Assert.Equal("redirect_blocked", result.ErrorCode);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Http_fetch_rejects_a_response_that_came_from_another_origin()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "metadata", finalUri: "https://169.254.169.254/latest/meta-data/");
        var tool = new HttpFetchTool(
            new HttpClient(handler),
            new HashSet<string> { "example.com" },
            addressResolver: PublicResolver);

        var result = await tool.ExecuteAsync(new ToolRequest("https://example.com/redirected"));

        Assert.False(result.Ok);
        Assert.Equal("redirect_blocked", result.ErrorCode);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.2.3.4", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("100.64.1.1", true)]
    [InlineData("192.168.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("::ffff:192.168.0.1", true)]
    [InlineData("93.184.216.34", false)]
    [InlineData("2606:2800:220:1:248:1893:25c8:1946", false)]
    public void Http_fetch_address_classifier_matches_the_block_list(string address, bool blocked)
    {
        Assert.Equal(blocked, HttpFetchTool.IsBlockedAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task Hardened_fetch_handler_disables_redirects_and_blocks_loopback_connects()
    {
        var handler = Natter.Infrastructure.Chat.ChatCompositionModule.CreateHttpFetchHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.NotNull(handler.ConnectCallback);

        // The connection callback itself cannot be constructed by callers; exercise it through a
        // real client. localhost resolves to a loopback address, so the callback must refuse the
        // socket before any TLS/connect happens.
        using var client = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://localhost:1/"));

        Assert.True(
            ContainsMessage(exception, "private"),
            $"Expected the hardening message, got: {exception}");
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

    private static bool ContainsMessage(Exception? exception, string fragment)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static readonly Func<string, CancellationToken, ValueTask<IPAddress[]>> PublicResolver =
        Resolver("93.184.216.34");

    private static Func<string, CancellationToken, ValueTask<IPAddress[]>> Resolver(params string[] addresses)
        => (_, _) => ValueTask.FromResult(addresses.Select(IPAddress.Parse).ToArray());

    private sealed class CountingHandler(HttpStatusCode status, string body, string? location = null) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            if (location is not null)
                response.Headers.Location = new Uri(location);

            return Task.FromResult(response);
        }
    }

    private sealed class StubHandler(
        HttpStatusCode status,
        string body,
        TimeSpan? delay = null,
        string? finalUri = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (delay is { } wait)
                await Task.Delay(wait, cancellationToken);

            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            response.RequestMessage = finalUri is null
                ? request
                : new HttpRequestMessage(HttpMethod.Get, finalUri);
            return response;
        }
    }
}
