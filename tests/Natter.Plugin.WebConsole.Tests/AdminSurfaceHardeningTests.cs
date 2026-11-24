using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Natter.Plugin.WebConsole.Tests.Phase23;
using Xunit;

namespace Natter.Plugin.WebConsole.Tests;

/// <summary>
/// S2 management-surface hardening (task-46): recoverable login throttle, forwarded-header trust,
/// request budget/concurrency, unauthenticated audit aggregation and audit rotation retention.
/// </summary>
public sealed class AdminSurfaceHardeningTests
{
    // ---------------------------------------------------------------- S2-D1 throttle

    [Fact]
    public void Global_cap_is_recoverable_after_a_successful_login()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero));
        var throttle = new WebConsoleLoginThrottle(clock);

        for (var index = 0; index < WebConsoleLoginThrottle.MaxFailuresGlobal; index++)
            throttle.RecordFailure("198.51.100." + (index % 200));

        Assert.True(throttle.IsBlocked("203.0.113.9", out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);
        Assert.Equal(WebConsoleLoginThrottle.MaxFailuresGlobal, throttle.GlobalFailureCount);

        // A verified operator gets back in: one success releases the oldest global entries (S2-D1).
        throttle.RecordSuccess("203.0.113.9");

        Assert.False(throttle.IsBlocked("203.0.113.9", out _));
        Assert.Equal(
            WebConsoleLoginThrottle.MaxFailuresGlobal - WebConsoleLoginThrottle.GlobalRecoveryPerSuccess,
            throttle.GlobalFailureCount);
    }

    [Fact]
    public void Reset_clears_the_client_and_its_global_contribution()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero));
        var throttle = new WebConsoleLoginThrottle(clock);

        for (var index = 0; index < WebConsoleLoginThrottle.MaxFailuresPerClient; index++)
            throttle.RecordFailure("198.51.100.7");

        Assert.True(throttle.IsBlocked("198.51.100.7", out _));

        throttle.Reset("198.51.100.7");

        Assert.False(throttle.IsBlocked("198.51.100.7", out _));
        Assert.Equal(0, throttle.GlobalFailureCount);
    }

    [Fact]
    public void ResetAll_clears_the_global_backstop_for_everyone()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero));
        var throttle = new WebConsoleLoginThrottle(clock);

        for (var index = 0; index < WebConsoleLoginThrottle.MaxFailuresGlobal; index++)
            throttle.RecordFailure("198.51.100." + index);

        Assert.True(throttle.IsBlocked("203.0.113.9", out _));

        throttle.ResetAll();

        Assert.False(throttle.IsBlocked("203.0.113.9", out _));
        Assert.Equal(0, throttle.GlobalFailureCount);
    }

    [Fact]
    public void Global_backstop_uses_the_longer_window_and_decays()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero));
        var throttle = new WebConsoleLoginThrottle(clock);

        for (var index = 0; index < WebConsoleLoginThrottle.MaxFailuresGlobal; index++)
            throttle.RecordFailure("198.51.100." + index);

        Assert.True(throttle.IsBlocked("203.0.113.9", out _));

        // Still inside the per-client window but already past it for the global backstop? No: the
        // global window is longer, so the block must persist past WebConsoleLoginThrottle.Window.
        clock.Advance(WebConsoleLoginThrottle.Window + TimeSpan.FromSeconds(1));
        Assert.True(throttle.IsBlocked("203.0.113.9", out _));

        clock.Advance(WebConsoleLoginThrottle.GlobalWindow);
        Assert.False(throttle.IsBlocked("203.0.113.9", out _));
        Assert.Equal(0, throttle.GlobalFailureCount);
    }

    [Fact]
    public void Per_client_window_still_blocks_only_that_client()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero));
        var throttle = new WebConsoleLoginThrottle(clock);

        for (var index = 0; index < WebConsoleLoginThrottle.MaxFailuresPerClient; index++)
            throttle.RecordFailure("198.51.100.7");

        Assert.True(throttle.IsBlocked("198.51.100.7", out _));
        Assert.False(throttle.IsBlocked("198.51.100.8", out _));
    }

    // ---------------------------------------------------------------- S2-D2 forwarded headers

    [Fact]
    public async Task Forged_forwarded_for_does_not_poison_throttle_key_or_audit_ip()
    {
        // No trusted proxy configured (the new default): XFF must be ignored completely.
        using var harness = WebConsoleHarness.Create();
        var forged = new Dictionary<string, string> { ["X-Forwarded-For"] = "198.51.100.99" };

        for (var attempt = 0; attempt < WebConsoleLoginThrottle.MaxFailuresPerClient; attempt++)
        {
            var response = await harness.Handler.HandleAsync(harness.Request(
                "POST",
                "/api/session",
                JsonSerializer.Serialize(new { token = "wrong-token-value" }),
                ip: "203.0.113.50",
                extraHeaders: forged));
            Assert.Equal(401, response.StatusCode);
        }

        // The forged address never appears in the audit trail…
        var audit = harness.Audit.Read(50);
        Assert.DoesNotContain(audit, entry => entry.Ip == "198.51.100.99");
        Assert.Contains(audit, entry => entry.Action == "auth.login" && entry.Ip == "203.0.113.50");

        // …and the throttle key stays the real peer, so the real client is the one that is blocked.
        var blocked = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/session",
            JsonSerializer.Serialize(new { token = WebConsoleHarness.Token }),
            ip: "203.0.113.50",
            extraHeaders: forged));
        Assert.Equal(429, blocked.StatusCode);

        var otherClient = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/session",
            JsonSerializer.Serialize(new { token = WebConsoleHarness.Token }),
            ip: "203.0.113.51",
            extraHeaders: forged));
        Assert.Equal(200, otherClient.StatusCode);
    }

    [Fact]
    public async Task Trusted_proxy_uses_the_rightmost_untrusted_hop()
    {
        using var harness = WebConsoleHarness.Create(new WebConsoleOptions
        {
            Port = 5090,
            TrustedProxies = ["127.0.0.1", "10.0.0.0/8"],
        });
        var login = await harness.LoginAsync();

        // Chain: client (198.51.100.7) -> our second proxy (10.1.2.3) -> loopback proxy.
        var response = await harness.Handler.HandleAsync(harness.Request(
            "GET",
            "/api/snapshot",
            cookie: login.Cookie,
            ip: "127.0.0.1",
            extraHeaders: new Dictionary<string, string>
            {
                ["X-Forwarded-For"] = "203.0.113.9, 198.51.100.7, 10.1.2.3",
            }));

        Assert.Equal(200, response.StatusCode);
        Assert.Contains(harness.Audit.Read(20),
            entry => entry.Action == "snapshot.view" && entry.Ip == "198.51.100.7");
    }

    [Fact]
    public async Task Forwarded_chain_with_invalid_hop_falls_back_to_the_real_peer()
    {
        using var harness = WebConsoleHarness.Create(new WebConsoleOptions
        {
            Port = 5090,
            TrustedProxies = ["127.0.0.1"],
        });
        var login = await harness.LoginAsync();

        await harness.Handler.HandleAsync(harness.Request(
            "GET",
            "/api/snapshot",
            cookie: login.Cookie,
            ip: "127.0.0.1",
            extraHeaders: new Dictionary<string, string> { ["X-Forwarded-For"] = "198.51.100.7, not-an-ip" }));

        Assert.Contains(harness.Audit.Read(20),
            entry => entry.Action == "snapshot.view" && entry.Ip == "127.0.0.1");
    }

    // ---------------------------------------------------------------- S2-D4 audit

    [Fact]
    public void Unauthenticated_audit_is_aggregated_per_action_and_client()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero));
        var gate = new WebConsoleAuditGate(clock);

        var written = 0;
        for (var index = 0; index < 50; index++)
        {
            if (gate.TryAcquire("origin.rejected", "203.0.113.9", out _))
                written++;
        }

        Assert.Equal(WebConsoleAuditGate.Burst, written);
        Assert.Equal(50 - WebConsoleAuditGate.Burst, gate.SuppressedFor("origin.rejected", "203.0.113.9"));

        // A different client keeps its own budget.
        Assert.True(gate.TryAcquire("origin.rejected", "203.0.113.10", out _));

        // The next window reports what the previous one suppressed, then resumes writing.
        clock.Advance(WebConsoleAuditGate.Window + TimeSpan.FromSeconds(1));
        Assert.True(gate.TryAcquire("origin.rejected", "203.0.113.9", out var suppressed));
        Assert.Equal(50 - WebConsoleAuditGate.Burst, suppressed);
        Assert.Equal(0, gate.SuppressedFor("origin.rejected", "203.0.113.9"));
    }

    [Fact]
    public async Task Unauthenticated_request_flood_stays_bounded_in_the_audit_file()
    {
        using var harness = WebConsoleHarness.Create();

        for (var index = 0; index < 40; index++)
        {
            await harness.Handler.HandleAsync(harness.Request(
                "GET",
                "/api/snapshot",
                origin: "https://evil.example"));
        }

        var entries = harness.Audit.Read(500);
        var originEntries = entries.Count(entry => entry.Action == "origin.rejected");
        Assert.True(originEntries <= WebConsoleAuditGate.Burst,
            $"expected at most {WebConsoleAuditGate.Burst} origin audit lines, saw {originEntries}");
        var fileLines = File.ReadAllLines(harness.AuditFilePath);
        Assert.True(fileLines.Length <= 40, $"audit file grew to {fileLines.Length} lines for 40 unauthenticated requests");
    }

    [Fact]
    public void Audit_rotation_keeps_the_configured_number_of_archives()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wc-rotation-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);

        try
        {
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero));
            using var store = new WebConsoleAuditStore(directory, clock, retentionFiles: 3);

            // Fill past the 1 MiB rotation threshold three times.
            for (var generation = 0; generation < 4; generation++)
            {
                while (new FileInfo(store.FilePath).Exists && new FileInfo(store.FilePath).Length < 1024 * 1024)
                {
                    var padding = new string('x', 900);
                    store.Write("test.pad", "admin", "127.0.0.1", padding, "ok", TimeSpan.Zero);
                }

                store.Write("test.rotate", "admin", "127.0.0.1", $"generation={generation}", "ok", TimeSpan.Zero);
            }

            Assert.True(File.Exists(store.FilePath + ".1"));
            Assert.True(File.Exists(store.FilePath + ".2"));
            Assert.True(File.Exists(store.FilePath + ".3"));
            Assert.False(File.Exists(store.FilePath + ".4"), "retention=3 must not keep a fourth archive");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    // ---------------------------------------------------------------- S2-D3 request budget / concurrency

    [Fact]
    public async Task Slow_body_upload_times_out_and_the_slot_is_returned()
    {
        var port = PortLease.Reserve();
        var options = new WebConsoleOptions
        {
            Port = port.Value,
            ReadOnly = true,
            RequestTimeoutSeconds = 1,
            MaxConcurrentRequests = 2,
        };

        var harness = WebConsoleHarness.Create(options);
        try
        {
            var status = new WebConsoleServerStatusHolder();
            var server = new WebConsoleServer(options, harness.Handler, status, new NullPluginLog());
            await using (server)
            {
                port.Release();
                await server.StartAsync();

                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, options.Port);
                await using var stream = client.GetStream();

                // Announce a large body, then send nothing: the budget must fire, not hang.
                var headers = Encoding.ASCII.GetBytes(
                    "POST /api/session HTTP/1.1\r\nHost: 127.0.0.1\r\n" +
                    "Content-Type: application/json\r\nContent-Length: 100000\r\n\r\n{\"token\":\"");
                await stream.WriteAsync(headers);
                await stream.FlushAsync();

                using var readBudget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var buffer = new byte[4096];
                var read = await stream.ReadAsync(buffer, readBudget.Token);
                Assert.True(read > 0, "expected a response for the timed-out slow request");

                var response = Encoding.ASCII.GetString(buffer, 0, read);
                Assert.Contains("408", response, StringComparison.Ordinal);

                // The slot is free again: a normal request still succeeds.
                using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var probeResponse = await probe.GetAsync($"http://127.0.0.1:{options.Port}/api/session");
                Assert.Equal(HttpStatusCode.Unauthorized, probeResponse.StatusCode);
            }
        }
        finally
        {
            harness.Dispose();
        }
    }

    [Fact]
    public async Task Saturated_console_answers_503_for_extra_connections()
    {
        var port = PortLease.Reserve();
        using var harness = WebConsoleHarness.Create(new WebConsoleOptions
        {
            Port = port.Value,
            ReadOnly = true,
            RequestTimeoutSeconds = 30,
            MaxConcurrentRequests = 1,
        });

        var options = harness.Options;
        var status = new WebConsoleServerStatusHolder();
        var server = new WebConsoleServer(options, harness.Handler, status, new NullPluginLog());

        await using (server)
        {
            port.Release();
            await server.StartAsync();

            // Occupy the only slot with a slow body.
            using var slow = new TcpClient();
            await slow.ConnectAsync(IPAddress.Loopback, options.Port);
            await using var slowStream = slow.GetStream();
            await slowStream.WriteAsync(Encoding.ASCII.GetBytes(
                "POST /api/session HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 1000\r\n\r\n{"));
            await slowStream.FlushAsync();
            await Task.Delay(200);

            using var second = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await second.GetAsync($"http://127.0.0.1:{options.Port}/api/session");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("server.busy",
                JsonDocument.Parse(await response.Content.ReadAsStringAsync())
                    .RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    // ---------------------------------------------------------------- S2-D5 redaction parity

    [Fact]
    public async Task Redaction_policy_prefers_the_host_redactor_when_present()
    {
        var store = new FakeAdminConfigStore().WithDocument("profiles", "{\"token\":\"sk-hostsecret1234\"}");
        using var harness = WebConsoleHarness.Create(
            new WebConsoleOptions { Port = 5090, ReadOnly = false, AllowConfigWrite = true },
            adminConfig: store,
            redactor: new UpperCaseRedactor());

        var login = await harness.LoginAsync();
        var response = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/config/profiles", cookie: login.Cookie));

        Assert.Equal(200, response.StatusCode);
        var body = Encoding.UTF8.GetString(response.Body);
        Assert.Contains("[HOST-REDACTED]", body, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-hostsecret1234", body, StringComparison.Ordinal);
        Assert.True(harness.Admin.Redaction.UsesHostRedactor);
    }

    [Fact]
    public async Task Redaction_policy_masks_the_same_shapes_everywhere_without_a_host_redactor()
    {
        const string pem = "-----BEGIN RSA PRIVATE KEY-----\\nMIIBOgIBAAJBAK\\n-----END RSA PRIVATE KEY-----";
        const string secret = "sk-plaintextsecret1234";
        var document = "{\"token\":\"" + secret + "\",\"key\":\"" + pem + "\",\"ref\":\"${secret:WEBCONSOLE_TOKEN}\"}";
        var store = new FakeAdminConfigStore().WithDocument("profiles", document);
        using var harness = WebConsoleHarness.Create(
            new WebConsoleOptions { Port = 5090, ReadOnly = false, AllowConfigWrite = true },
            adminConfig: store);

        var login = await harness.LoginAsync();

        var read = await harness.Handler.HandleAsync(harness.Request("GET", "/api/config/profiles", cookie: login.Cookie));
        var readBody = Encoding.UTF8.GetString(read.Body);

        var validate = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/config/profiles/validate",
            body: JsonSerializer.Serialize(new { content = "{\"schemaVersion\":2}" }),
            cookie: login.Cookie,
            csrf: login.Csrf));
        var validateBody = Encoding.UTF8.GetString(validate.Body);

        foreach (var body in new[] { readBody, validateBody })
        {
            Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
            Assert.DoesNotContain("MIIBOgIBAAJBAK", body, StringComparison.Ordinal);
            Assert.Contains("${secret:WEBCONSOLE_TOKEN}", body, StringComparison.Ordinal);
        }

        Assert.False(harness.Admin.Redaction.UsesHostRedactor);
        Assert.Equal(
            WebConsoleRedaction.RedactSecrets(document),
            harness.Admin.Redaction.Redact(document));
    }

    [Fact]
    public async Task Status_reports_the_redaction_source_that_is_actually_in_effect()
    {
        // Host redactor bridged (the normal case once admin.read is granted, S2-D5).
        var bridged = BuildRuntime(new UpperCaseRedactor());
        var bridgedStatus = bridged.DescribeStatus(json: true);
        Assert.Contains("\"source\":\"host\"", bridgedStatus, StringComparison.Ordinal);
        Assert.Contains("redaction: host", bridged.DescribeStatus(json: false), StringComparison.Ordinal);

        // Bridge absent (permission not granted / old host): the plugin-local rules are in effect.
        var local = BuildRuntime(null);
        Assert.Contains("\"source\":\"plugin\"", local.DescribeStatus(json: true), StringComparison.Ordinal);
        Assert.Contains("redaction: plugin", local.DescribeStatus(json: false), StringComparison.Ordinal);
    }

    private static WebConsoleRuntime BuildRuntime(Natter.Application.Admin.IAdminRedactor? redactor)
    {
        var directory = Path.Combine(Path.GetTempPath(), "wc-status-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        var wwwroot = Path.Combine(directory, "wwwroot");
        Directory.CreateDirectory(wwwroot);
        File.WriteAllText(Path.Combine(wwwroot, "index.html"), "<!doctype html>");
        File.WriteAllText(Path.Combine(wwwroot, "app.js"), "\"use strict\";");
        File.WriteAllText(Path.Combine(wwwroot, "app.css"), "body{}");

        var options = new WebConsoleOptions { Port = 5090 };
        var runtime = new WebConsoleRuntime(
            new WebConsoleIdentity(
                "natter.tool.admin.webconsole",
                "WebConsole",
                "0.1.0",
                "tool",
                "0.1.0",
                "Natter.Plugin.WebConsole",
                directory,
                Path.Combine(directory, "plugin.json")),
            options,
            WebConsoleConfig.Build(JsonSerializer.SerializeToElement(new { }), "test", null, options),
            new NullPluginLog(),
            TimeProvider.System,
            () => directory);

        // The console resolves the admin contracts (redactor included) from the plugin provider.
        var services = new FakeServiceProvider();
        if (redactor is not null)
            services.Add(redactor);
        runtime.StartAsync(new FakePluginSecrets(WebConsoleHarness.Token), services).GetAwaiter().GetResult();
        runtime.StopAsync().GetAwaiter().GetResult();
        return runtime;
    }

    private sealed class UpperCaseRedactor : Natter.Application.Admin.IAdminRedactor
    {
        public string Redact(string? value)
            => value is null ? string.Empty : WebConsoleRedaction.RedactSecrets(value).Replace("[redacted]", "[HOST-REDACTED]", StringComparison.Ordinal);
    }

    /// <summary>Reserves an ephemeral loopback port until the consumer is ready to bind it.</summary>
    private sealed class PortLease : IDisposable
    {
        private readonly TcpListener _listener;

        private PortLease(TcpListener listener, int value)
        {
            _listener = listener;
            Value = value;
        }

        public int Value { get; }

        public static PortLease Reserve()
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                try
                {
                    listener.Start();
                    return new PortLease(listener, ((IPEndPoint)listener.LocalEndpoint).Port);
                }
                catch (SocketException)
                {
                    listener.Stop();
                }
            }

            throw new InvalidOperationException("Could not reserve a loopback port.");
        }

        public void Release()
        {
            try { _listener.Stop(); } catch (SocketException) { }
        }

        public void Dispose() => Release();
    }
}
