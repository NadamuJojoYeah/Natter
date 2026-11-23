using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Natter.Plugin.WebConsole.Tests;

public sealed class WebConsoleServerTests
{
    [Fact]
    public async Task Server_serves_login_and_snapshot_over_loopback()
    {
        await using var serverUnderTest = await ServerUnderTest.StartAsync(port => new WebConsoleOptions { Port = port });
        var harness = serverUnderTest.Harness;
        var baseUrl = serverUnderTest.BaseUrl;

        Assert.Equal("listening", harness.Status.Current.State);

        try
        {
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });

            var index = await client.GetAsync(baseUrl + "/");
            Assert.Equal(HttpStatusCode.OK, index.StatusCode);
            Assert.Equal("text/html; charset=utf-8", index.Content.Headers.ContentType?.ToString());
            Assert.Equal(
                "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; "
                + "connect-src 'self'; font-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
                Assert.Single(index.Headers.GetValues("Content-Security-Policy")));

            var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, baseUrl + "/"));
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);
            Assert.Empty(await head.Content.ReadAsByteArrayAsync());
            Assert.True(head.Content.Headers.ContentLength > 0);

            var login = await client.PostAsync(
                baseUrl + "/api/session",
                new StringContent(
                    JsonSerializer.Serialize(new { token = WebConsoleHarness.Token }),
                    Encoding.UTF8,
                    "application/json"));

            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0];

            using var snapshotRequest = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/api/snapshot");
            snapshotRequest.Headers.Add("Cookie", cookie);
            var snapshot = await client.SendAsync(snapshotRequest);

            Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);
            using var document = JsonDocument.Parse(await snapshot.Content.ReadAsStringAsync());
            Assert.Equal("natter.tool.admin.webconsole", document.RootElement.GetProperty("self").GetProperty("pluginId").GetString());
        }
        finally
        {
            await serverUnderTest.Server.StopAsync();
        }

        Assert.Equal("stopped", harness.Status.Current.State);
    }

    [Fact]
    public async Task Port_conflict_reports_stable_error_code()
    {
        var port = LoopbackPortPool.Reserve();
        var blocker = new TcpListener(IPAddress.Loopback, port);
        blocker.Start();

        try
        {
            using var harness = WebConsoleHarness.Create(new WebConsoleOptions { Port = port });
            await using var server = new WebConsoleServer(
                harness.Options,
                harness.Handler,
                harness.Status,
                new NullPluginLog());

            var exception = await Assert.ThrowsAsync<WebConsoleStartupException>(() => server.StartAsync());

            Assert.Equal("webconsole.port-in-use", exception.Code);
            Assert.Equal("failed", harness.Status.Current.State);
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public void Missing_static_assets_fail_with_stable_code()
    {
        var directory = Path.Combine(Path.GetTempPath(), "natter-webconsole-empty", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var exception = Assert.Throws<WebConsoleStartupException>(() => WebConsoleStaticAssets.Load(directory));
            Assert.Equal("webconsole.static-assets-missing", exception.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Packaged_wwwroot_ships_a_module_document_with_no_inline_script()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "plugin-assets");
        if (!File.Exists(Path.Combine(root, "index.html")))
            return; // UI build not run in this checkout; the build gate asserts it in CI/publish.

        var assets = WebConsoleStaticAssets.Load(root);
        Assert.True(assets.TryGet("/", out var index));
        var html = Encoding.UTF8.GetString(index.Body);

        // Vite emits `<script type="module" crossorigin src="/assets/index-<hash>.js">`.
        Assert.Contains("type=\"module\"", html, StringComparison.Ordinal);
        Assert.Matches(@"src=""/assets/[A-Za-z0-9._-]+\.js""", html);
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("onclick=", html, StringComparison.Ordinal);
        Assert.Equal(WebConsoleStaticAssets.NoStoreCacheControl, index.Headers["Cache-Control"]);
    }

}
