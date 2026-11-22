using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Natter.Plugin.WebConsole.Tests;

/// <summary>
/// UI-DIRECTION P1 acceptance (task-51): native ES modules, trace timeline, folding instead of silent
/// truncation, the four distinguishable empty states, and the redacted diagnostics bundle.
/// </summary>
public sealed class UiDirectionTests
{
    private static readonly string WwwRoot = Path.Combine(RepositoryRoot(), "plugins", "Natter.Plugin.WebConsole", "wwwroot");
    private static readonly string HarnessPath = Path.Combine(
        RepositoryRoot(), "tests", "Natter.Plugin.WebConsole.Tests", "ui", "harness.mjs");

    // ---------------------------------------------------------------- modules served with correct MIME

    [Fact]
    public async Task Every_module_is_served_as_text_javascript()
    {
        var port = LoopbackPortPool.Reserve();
        using var harness = WebConsoleHarness.Create(
            new WebConsoleOptions { Port = port },
            wwwrootSource: WwwRoot);
        await using var server = new WebConsoleServer(harness.Options, harness.Handler, harness.Status, new NullPluginLog());
        await server.StartAsync();

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            foreach (var route in new[] { "/app.js", "/util.js", "/state.js", "/api.js", "/views/trace.js", "/views/diag.js" })
            {
                var response = await client.GetAsync($"http://127.0.0.1:{port}{route}");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Contains("javascript", response.Content.Headers.ContentType?.MediaType, StringComparison.OrdinalIgnoreCase);
            }

            var css = await client.GetAsync($"http://127.0.0.1:{port}/app.css");
            Assert.Equal("text/css", css.Content.Headers.ContentType?.MediaType);

            var index = await client.GetAsync($"http://127.0.0.1:{port}/");
            Assert.Equal("text/html", index.Content.Headers.ContentType?.MediaType);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public void Static_asset_whitelist_includes_view_modules_and_nothing_outside_wwwroot()
    {
        var assets = WebConsoleStaticAssets.Load(WwwRoot);

        Assert.Contains("/", assets.Files);
        Assert.Contains("/app.js", assets.Files);
        Assert.Contains("/app.css", assets.Files);
        Assert.Contains("/state.js", assets.Files);
        Assert.Contains("/views/trace.js", assets.Files);
        Assert.Contains("/views/overview.js", assets.Files);

        Assert.All(assets.Files, route => Assert.StartsWith("/", route, StringComparison.Ordinal));
        Assert.DoesNotContain(assets.Files, route => route.Contains("..", StringComparison.Ordinal));

        // Only whitelisted asset kinds are exposed.
        Assert.DoesNotContain(assets.Files, route => route.EndsWith(".csproj", StringComparison.Ordinal));
        Assert.DoesNotContain(assets.Files, route => route.EndsWith(".json", StringComparison.Ordinal));
    }

    [Fact]
    public void Wwwroot_has_no_external_imports_and_no_inline_script()
    {
        var scripts = Directory.EnumerateFiles(WwwRoot, "*.js", SearchOption.AllDirectories).ToArray();
        Assert.NotEmpty(scripts);

        var external = new Regex(
            @"(from|import)\s*\(?\s*[""'](https?:)?//|src\s*=\s*[""'](https?:)?//|@import\s+[""'](https?:)?//|cdn\.",
            RegexOptions.IgnoreCase);

        foreach (var script in scripts)
        {
            var text = File.ReadAllText(script);
            Assert.DoesNotMatch(external, text);

            // Every import must be a same-origin relative specifier.
            foreach (Match match in Regex.Matches(text, @"^\s*(?:import|export)\b[^;\n]*?from\s+[""']([^""']+)[""']", RegexOptions.Multiline))
            {
                var specifier = match.Groups[1].Value;
                Assert.True(
                    specifier.StartsWith("./", StringComparison.Ordinal) || specifier.StartsWith("../", StringComparison.Ordinal),
                    $"{Path.GetFileName(script)} imports non-relative module '{specifier}'");
            }
        }

        var html = File.ReadAllText(Path.Combine(WwwRoot, "index.html"));
        Assert.Contains("<script type=\"module\" src=\"/app.js\"></script>", html, StringComparison.Ordinal);

        // No inline script blocks: every <script ...> must carry src.
        var inline = Regex.Matches(html, @"<script(?![^>]*\bsrc=)[^>]*>");
        Assert.Empty(inline);
        Assert.DoesNotContain("unsafe-eval", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Csp_is_not_relaxed()
    {
        var response = WebConsoleHttpResponse.Text(200, "text/html", "x");
        Assert.NotNull(response);

        // The CSP shipped by the handler is asserted end-to-end in WebConsoleServerTests; here we pin
        // the exact policy text so a future edit cannot quietly widen it.
        var policy = File.ReadAllText(Path.Combine(RepositoryRoot(), "plugins", "Natter.Plugin.WebConsole", "WebConsoleRequestHandler.cs"));
        Assert.Contains("default-src 'none'; script-src 'self'; style-src 'self';", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("script-src 'self' http", policy, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- rendered views (node harness)

    [Fact]
    public void Views_render_the_timeline_fold_and_four_empty_states()
    {
        var report = RunHarness();

        // ---- four distinguishable empty states
        var blocks = report.GetProperty("util").GetProperty("stateBlocks");
        var noData = blocks.GetProperty("noData").GetString()!;
        var noPermission = blocks.GetProperty("noPermission").GetString()!;
        var disabled = blocks.GetProperty("disabled").GetString()!;
        var hostUnavailable = blocks.GetProperty("hostUnavailable").GetString()!;

        Assert.Contains("当前没有数据", noData, StringComparison.Ordinal);
        Assert.Contains("需要 admin 角色", noPermission, StringComparison.Ordinal);
        Assert.Contains("功能未启用", disabled, StringComparison.Ordinal);
        Assert.Contains("501", hostUnavailable, StringComparison.Ordinal);
        Assert.Equal(4, new[] { noData, noPermission, disabled, hostUnavailable }.Distinct().Count());

        Assert.Contains("已截断 300 字符", report.GetProperty("util").GetProperty("truncation").GetString()!, StringComparison.Ordinal);

        // ---- trace timeline
        var trace = report.GetProperty("trace");
        var model = trace.GetProperty("model");
        Assert.Equal("task_abc123", model.GetProperty("taskId").GetString());
        Assert.Equal("Completed", model.GetProperty("status").GetString());
        Assert.Equal(4, model.GetProperty("version").GetInt32());
        Assert.Equal(12400, model.GetProperty("elapsedMs").GetInt32());
        Assert.Equal(1, model.GetProperty("tools").GetProperty("used").GetInt32());
        Assert.Equal(3, model.GetProperty("tools").GetProperty("budget").GetInt32());

        var timeline = trace.GetProperty("html").GetString()!;
        foreach (var marker in new[] { "trace-timeline", "kind-started", "kind-step", "kind-tool", "kind-final", "trace-bar", "tools 1/3" })
            Assert.Contains(marker, timeline, StringComparison.Ordinal);

        // ---- result folding: 200-char summary + expandable full text
        var result = trace.GetProperty("result").GetString()!;
        Assert.Contains("原文 400 字符", result, StringComparison.Ordinal);
        Assert.Contains("显示 200 字符", result, StringComparison.Ordinal);
        Assert.Contains("<details>", result, StringComparison.Ordinal);

        // ---- trace folding: default 10 visible + 加载更多
        var limited = trace.GetProperty("limited").GetString()!;
        Assert.Contains("加载更多", limited, StringComparison.Ordinal);

        // ---- error rows: red marker + copyable error code
        var error = trace.GetProperty("error").GetString()!;
        Assert.Contains("data-copy=\"agent.timeout\"", error, StringComparison.Ordinal);
        Assert.Contains("trace-error", error, StringComparison.Ordinal);
        Assert.Contains("class=\"trace-row kind-final error\"", error, StringComparison.Ordinal);

        // ---- truncation accounting in config diff / command output
        var validation = report.GetProperty("config").GetProperty("validation").GetString()!;
        Assert.Contains("原文 90 行", validation, StringComparison.Ordinal);
        Assert.Contains("显示 60 行", validation, StringComparison.Ordinal);
        Assert.Contains("已截断 30 行", validation, StringComparison.Ordinal);

        var command = report.GetProperty("commands").GetProperty("result").GetString()!;
        Assert.Contains("原文 9000 字节", command, StringComparison.Ordinal);
        Assert.Contains("宿主已截断", command, StringComparison.Ordinal);

        // ---- config states are distinguishable
        Assert.Contains("需要 admin 角色", report.GetProperty("config").GetProperty("noPermission").GetString()!, StringComparison.Ordinal);
        Assert.Contains("功能未启用", report.GetProperty("config").GetProperty("disabled").GetString()!, StringComparison.Ordinal);

        // ---- logs / diag states
        Assert.Contains("宿主日志不可用", report.GetProperty("logs").GetProperty("unavailable").GetString()!, StringComparison.Ordinal);
        Assert.Contains("webconsole.diag-sentinel-failed", report.GetProperty("diag").GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void Escape_html_neutralises_markup_in_every_view()
    {
        var report = RunHarness();
        var escaped = report.GetProperty("util").GetProperty("escape").GetString()!;

        Assert.Contains("&lt;b", escaped, StringComparison.Ordinal);
        Assert.DoesNotContain("<b", escaped, StringComparison.Ordinal);

        // Audit rows and gaps route user-provided text through escapeHtml.
        var gaps = report.GetProperty("overview").GetProperty("gaps").GetString()!;
        Assert.DoesNotContain("<script", gaps, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- diagnostics bundle

    [Fact]
    public void Diagnostics_bundle_carries_no_plaintext_secrets_and_declares_truncation()
    {
        var sentinelToken = "sentinel-token-abcdef123456";
        var snapshot = BuildSnapshot(
            logs: Enumerable.Range(0, 30)
                .Select(index => new WebConsoleLogEntry(
                    DateTimeOffset.UnixEpoch.AddSeconds(index), "Information", "host",
                    index == 0 ? $"token={sentinelToken} sk-livesecret0123456789" : $"line {index}"))
                .ToArray(),
            audit: [new WebConsoleAuditEntry(DateTimeOffset.UnixEpoch, "auth.login", "admin", "127.0.0.1", null, "denied", 1)]);

        var documents = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["profiles"] = "{\"token\":\"" + sentinelToken + "\"}",
            ["plugin:natter.plugin.webconsole"] = "{\"ok\":true}",
        };

        var bundle = WebConsoleDiagnosticsBundle.Build(
            snapshot,
            documents,
            WebConsoleRedactionPolicy.Local,
            DateTimeOffset.UnixEpoch,
            collectedBy: "admin",
            sentinels: [sentinelToken]);

        Assert.True(bundle.Zip.Length > 0);
        Assert.True(bundle.Zip.Length <= WebConsoleDiagnosticsBundle.MaxTotalBytes);
        Assert.StartsWith("natter-diag-", bundle.FileName, StringComparison.Ordinal);

        using var stream = new MemoryStream(bundle.Zip);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var names = archive.Entries.Select(entry => entry.FullName).ToArray();

        Assert.Contains("manifest.json", names);
        Assert.Contains("health.json", names);
        Assert.Contains("upstreams.json", names);
        Assert.Contains("providers.json", names);
        Assert.Contains("plugins.json", names);
        Assert.Contains("logs.jsonl", names);
        Assert.Contains("audit.jsonl", names);
        Assert.Contains("config.redacted/profiles.json", names);

        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            var text = reader.ReadToEnd();
            Assert.DoesNotContain(sentinelToken, text, StringComparison.Ordinal);
            Assert.DoesNotContain("sk-livesecret", text, StringComparison.Ordinal);
        }

        // manifest: entry accounting + truncation declaration
        var manifestEntry = archive.GetEntry("manifest.json")!;
        using var manifestReader = new StreamReader(manifestEntry.Open(), Encoding.UTF8);
        using var manifest = JsonDocument.Parse(manifestReader.ReadToEnd());
        var root = manifest.RootElement;

        Assert.Equal("plugin", root.GetProperty("redaction").GetString());
        Assert.True(root.GetProperty("maxTotalBytes").GetInt64() > 0);
        Assert.True(root.GetProperty("files").GetArrayLength() >= 8);

        var logs = root.GetProperty("files").EnumerateArray()
            .Single(file => file.GetProperty("path").GetString() == "logs.jsonl");
        Assert.Equal(30, logs.GetProperty("items").GetInt32());
        Assert.Equal(30, logs.GetProperty("shownItems").GetInt32());

        var audit = root.GetProperty("files").EnumerateArray()
            .Single(file => file.GetProperty("path").GetString() == "audit.jsonl");
        Assert.Equal(1, audit.GetProperty("items").GetInt32());
    }

    [Fact]
    public void Diagnostics_bundle_declares_log_truncation_beyond_the_entry_cap()
    {
        var logs = Enumerable.Range(0, WebConsoleDiagnosticsBundle.MaxLogEntries + 250)
            .Select(index => new WebConsoleLogEntry(DateTimeOffset.UnixEpoch.AddSeconds(index), "Information", "host", $"line {index}"))
            .ToArray();

        var bundle = WebConsoleDiagnosticsBundle.Build(
            BuildSnapshot(logs, []),
            new Dictionary<string, string?>(StringComparer.Ordinal),
            WebConsoleRedactionPolicy.Local,
            DateTimeOffset.UnixEpoch);

        Assert.True(bundle.Manifest.Truncated);
        var logFile = bundle.Manifest.Files.Single(file => file.Path == "logs.jsonl");
        Assert.Equal(logs.Length, logFile.Items);
        Assert.Equal(WebConsoleDiagnosticsBundle.MaxLogEntries, logFile.ShownItems);
        Assert.NotNull(logFile.Truncation);
    }

    [Fact]
    public void Diagnostics_bundle_refuses_to_pack_a_surviving_secret()
    {
        var snapshot = BuildSnapshot([], []);

        var exception = Assert.Throws<WebConsoleStartupException>(() => WebConsoleDiagnosticsBundle.Build(
            snapshot,
            new Dictionary<string, string?>(StringComparer.Ordinal),
            WebConsoleRedactionPolicy.Local,
            DateTimeOffset.UnixEpoch,
            forceSentinel: "sentinel-that-must-not-survive"));

        Assert.Equal("webconsole.diag-sentinel-failed", exception.Code);
    }

    [Fact]
    public void Diagnostics_bundle_honours_the_host_redactor()
    {
        var snapshot = BuildSnapshot([], []);

        var bundle = WebConsoleDiagnosticsBundle.Build(
            snapshot,
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["profiles"] = "{\"token\":\"sk-hostvalue12345678\"}" },
            new WebConsoleRedactionPolicy(new UpperCaseRedactor()),
            DateTimeOffset.UnixEpoch);

        using var stream = new MemoryStream(bundle.Zip);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        using var reader = new StreamReader(archive.GetEntry("config.redacted/profiles.json")!.Open(), Encoding.UTF8);
        var content = reader.ReadToEnd();

        Assert.Contains("[HOST-REDACTED]", content, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-hostvalue12345678", content, StringComparison.Ordinal);
        Assert.Equal("host", bundle.Manifest.RedactionSource);
    }

    // ---------------------------------------------------------------- helpers

    private sealed class UpperCaseRedactor : Natter.Application.Admin.IAdminRedactor
    {
        public string Redact(string? value)
            => value is null
                ? string.Empty
                : WebConsoleRedaction.RedactSecrets(value).Replace("[redacted]", "[HOST-REDACTED]", StringComparison.Ordinal);
    }

    private static WebConsoleSnapshot BuildSnapshot(WebConsoleLogEntry[] logs, WebConsoleAuditEntry[] audit)
    {
        var self = new WebConsoleSelf(
            "natter.plugin.webconsole", "Natter WebConsole", "0.1.0", "tool", "0.1.0",
            "ok", "listening", "http://127.0.0.1:5090", "127.0.0.1", 5090,
            ReadOnly: true, AllowConfigWrite: false, AllowCommandConsole: false, SessionMinutes: 60,
            MaxLogLines: 500, TokenConfigured: true, StartedAt: DateTimeOffset.UnixEpoch, UptimeSeconds: 1,
            ServerRuntime: "httplistener", HostDataSource: "live");

        return new WebConsoleSnapshot(
            DateTimeOffset.UnixEpoch,
            self,
            [],
            WebConsoleSection<WebConsoleHealth>.Ok(
                new WebConsoleHealth("ok", DateTimeOffset.UnixEpoch, 1, DateTimeOffset.UnixEpoch, 0, 0, new WebConsoleTasks(0, 0, 0, 0, 0), [], 1, 0, []),
                DateTimeOffset.UnixEpoch),
            WebConsoleSection<WebConsolePlugins>.Ok(new WebConsolePlugins(1, 0, [], [], [], [], "host"), DateTimeOffset.UnixEpoch),
            WebConsoleSection<IReadOnlyList<WebConsoleUpstream>>.Ok([], DateTimeOffset.UnixEpoch),
            WebConsoleSection<IReadOnlyList<WebConsoleProfile>>.Ok([], DateTimeOffset.UnixEpoch),
            WebConsoleSection<IReadOnlyList<WebConsoleLogEntry>>.Ok(logs, DateTimeOffset.UnixEpoch),
            WebConsoleConfig.Build(JsonSerializer.SerializeToElement(new { }), "test", null, new WebConsoleOptions()),
            new WebConsoleAdminStatus(true, false, null, true, false, null, [], 10, true),
            [],
            audit);
    }

    private static JsonElement RunHarness()
    {
        var node = ResolveNode();
        Assert.False(node is null, "node is required for the UI render harness");

        var start = new ProcessStartInfo(node!, $"\"{HarnessPath}\" \"{WwwRoot}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = RepositoryRoot(),
        };

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(TimeSpan.FromSeconds(60));

        Assert.True(process.ExitCode == 0, $"harness failed ({process.ExitCode}): {stderr}");
        return JsonDocument.Parse(stdout).RootElement;
    }

    private static string? ResolveNode()
    {
        foreach (var candidate in new[] { "/usr/bin/node", "/usr/local/bin/node", "node" })
        {
            if (!candidate.Contains('/', StringComparison.Ordinal))
                return candidate;

            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Natter.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? Directory.GetCurrentDirectory();
    }
}
