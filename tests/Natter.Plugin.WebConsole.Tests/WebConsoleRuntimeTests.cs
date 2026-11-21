using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Natter.Plugin.WebConsole.Tests;

public sealed class WebConsoleRuntimeTests
{
    [Fact]
    public async Task Start_enters_external_mode_when_another_console_owns_the_port()
    {
        var port = FreePort();
        using var stub = new HttpListener();
        stub.Prefixes.Add($"http://127.0.0.1:{port}/");
        stub.Start();

        var loopCancellation = new CancellationTokenSource();
        var loop = Task.Run(() => ServeStubAsync(stub, loopCancellation.Token));

        var directory = CreateDirectory();
        try
        {
            var options = new WebConsoleOptions { Port = port };
            var identity = new WebConsoleIdentity(
                "natter.plugin.webconsole",
                "Natter WebConsole",
                "0.1.0",
                "tool",
                "0.1.0",
                "Natter.Plugin.WebConsole",
                directory,
                Path.Combine(directory, "plugin.json"));

            var runtime = new WebConsoleRuntime(
                identity,
                options,
                WebConsoleConfig.Build(JsonSerializer.SerializeToElement(new { }), "test", null, options),
                new NullPluginLog(),
                TimeProvider.System);

            await using (runtime)
            {
                await runtime.StartAsync(new FakePluginSecrets(WebConsoleHarness.Token), new FakeServiceProvider());

                Assert.Equal("external", runtime.Status.State);
                Assert.Equal($"http://127.0.0.1:{port}", runtime.Status.Url);
                Assert.Contains("\"state\":\"external\"", runtime.DescribeStatus(json: true));
            }

            Assert.Equal("stopped", runtime.Status.State);
        }
        finally
        {
            loopCancellation.Cancel();
            stub.Stop();
            stub.Close();
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Start_fails_when_the_port_is_held_by_a_non_console_process()
    {
        var port = FreePort();
        var blocker = new TcpListener(IPAddress.Loopback, port);
        blocker.Start();

        var directory = CreateDirectory();
        try
        {
            var options = new WebConsoleOptions { Port = port };
            var identity = new WebConsoleIdentity(
                "natter.plugin.webconsole",
                "Natter WebConsole",
                "0.1.0",
                "tool",
                "0.1.0",
                "Natter.Plugin.WebConsole",
                directory,
                Path.Combine(directory, "plugin.json"));

            var runtime = new WebConsoleRuntime(
                identity,
                options,
                WebConsoleConfig.Build(JsonSerializer.SerializeToElement(new { }), "test", null, options),
                new NullPluginLog(),
                TimeProvider.System);

            var exception = await Assert.ThrowsAsync<WebConsoleStartupException>(
                () => runtime.StartAsync(new FakePluginSecrets(WebConsoleHarness.Token), new FakeServiceProvider()));

            Assert.Equal("webconsole.port-in-use", exception.Code);
            Assert.Equal("failed", runtime.Status.State);
        }
        finally
        {
            blocker.Stop();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Audit_log_uses_the_state_root_when_NATTER_STATE_DIR_is_set()
    {
        var port = FreePort();
        var directory = CreateDirectory();
        var stateRoot = Path.Combine(directory, "state");
        var log = new NullPluginLog();

        try
        {
            var options = new WebConsoleOptions { Port = port };
            var runtime = new WebConsoleRuntime(
                Identity(directory),
                options,
                WebConsoleConfig.Build(JsonSerializer.SerializeToElement(new { }), "test", null, options),
                log,
                TimeProvider.System,
                () => stateRoot);

            await using (runtime)
            {
                await runtime.StartAsync(new FakePluginSecrets(WebConsoleHarness.Token), new FakeServiceProvider());

                var expected = Path.Combine(stateRoot, "webconsole", "audit");
                Assert.Equal(expected, runtime.AuditLocation!.Directory);
                Assert.Equal(WebConsoleAuditRoot.StateDirectory, runtime.AuditLocation.Root);
                Assert.Equal(Path.Combine(expected, WebConsoleDefaults.AuditFileName), runtime.AuditPath);
                Assert.True(File.Exists(runtime.AuditPath));
                Assert.DoesNotContain(log.Messages, message => message.Contains("NATTER_STATE_DIR is not set", StringComparison.Ordinal));

                var status = runtime.DescribeStatus(json: true);
                Assert.Contains("\"root\":\"state-directory\"", status);
                Assert.Contains("root=state-directory", runtime.DescribeStatus(json: false));

                if (!OperatingSystem.IsWindows())
                {
                    Assert.Equal(
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                        File.GetUnixFileMode(expected));
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Audit_log_falls_back_to_the_plugin_directory_with_a_warning()
    {
        var port = FreePort();
        var directory = CreateDirectory();
        var log = new NullPluginLog();

        try
        {
            var options = new WebConsoleOptions { Port = port };
            var runtime = new WebConsoleRuntime(
                Identity(directory),
                options,
                WebConsoleConfig.Build(JsonSerializer.SerializeToElement(new { }), "test", null, options),
                log,
                TimeProvider.System,
                () => null);

            await using (runtime)
            {
                await runtime.StartAsync(new FakePluginSecrets(WebConsoleHarness.Token), new FakeServiceProvider());

                Assert.Equal(WebConsoleAuditRoot.PluginDirectory, runtime.AuditLocation!.Root);
                Assert.Equal(Path.Combine(directory, "audit"), runtime.AuditLocation.Directory);
                Assert.Contains(log.Messages, message => message.Contains("NATTER_STATE_DIR is not set", StringComparison.Ordinal));
                Assert.Contains("\"root\":\"plugin-directory\"", runtime.DescribeStatus(json: true));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Unwritable_audit_root_fails_with_an_actionable_startup_code()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
            return;

        var port = FreePort();
        var directory = CreateDirectory();
        var stateRoot = Path.Combine(directory, "state");
        Directory.CreateDirectory(stateRoot);
        File.SetUnixFileMode(stateRoot, UnixFileMode.None);

        try
        {
            var options = new WebConsoleOptions { Port = port };
            var runtime = new WebConsoleRuntime(
                Identity(directory),
                options,
                WebConsoleConfig.Build(JsonSerializer.SerializeToElement(new { }), "test", null, options),
                new NullPluginLog(),
                TimeProvider.System,
                () => stateRoot);

            var exception = await Assert.ThrowsAsync<WebConsoleStartupException>(
                () => runtime.StartAsync(new FakePluginSecrets(WebConsoleHarness.Token), new FakeServiceProvider()));

            Assert.Equal("webconsole.audit-unwritable", exception.Code);
            Assert.Contains("NATTER_STATE_DIR", exception.Message, StringComparison.Ordinal);
            Assert.Contains("/var/lib/natter", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(stateRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static WebConsoleIdentity Identity(string directory) => new(
        "natter.plugin.webconsole",
        "Natter WebConsole",
        "0.1.0",
        "tool",
        "0.1.0",
        "Natter.Plugin.WebConsole",
        directory,
        Path.Combine(directory, "plugin.json"));

    [Theory]
    [InlineData("127.0.0.1", 5090, "http://127.0.0.1:5090")]
    [InlineData("::1", 5090, "http://[::1]:5090")]
    [InlineData("localhost", 5090, "http://localhost:5090")]
    public void Build_url_handles_loopback_hosts(string bind, int port, string expected)
    {
        Assert.Equal(expected, WebConsoleServer.BuildUrl(new WebConsoleOptions { Bind = bind, Port = port }));
    }

    private static async Task ServeStubAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(cancellationToken);
            }
            catch (Exception)
            {
                return;
            }

            var body = Encoding.UTF8.GetBytes("{\"authenticated\":false}");
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body, cancellationToken);
            context.Response.Close();
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "natter-webconsole-runtime", Guid.NewGuid().ToString("N"));
        var wwwroot = Path.Combine(directory, "wwwroot");
        Directory.CreateDirectory(wwwroot);
        File.WriteAllText(Path.Combine(wwwroot, "index.html"), "<!doctype html>");
        File.WriteAllText(Path.Combine(wwwroot, "app.js"), "\"use strict\";");
        File.WriteAllText(Path.Combine(wwwroot, "app.css"), "body{}");
        return directory;
    }
}
