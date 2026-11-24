using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Natter.Plugin.WebConsole.Tests;

/// <summary>
/// Renders the real React views server-side (react-dom/server via esbuild + node) and asserts on the
/// produced markup: data → expected text, three states, error codes, ETag/disabled branches and the
/// "never render untrusted HTML" guarantee (task-65).
/// </summary>
public sealed class UiViewRenderTests
{
    private static JsonElement? cachedReport;
    private static string? cachedFailure;

    [Fact]
    public void Overview_renders_health_upstreams_plugins_and_gaps()
    {
        var report = Report();
        var html = report.GetProperty("overview").GetString()!;

        Assert.Contains("natter.tool.admin.webconsole", html, StringComparison.Ordinal);
        Assert.Contains("degraded", html, StringComparison.Ordinal);
        Assert.Contains("discord:discord:11", html, StringComparison.Ordinal);
        Assert.Contains("gateway closed", html, StringComparison.Ordinal);
        Assert.Contains("host.logs", html, StringComparison.Ordinal);
        Assert.Contains("natter.sample.bad", html, StringComparison.Ordinal);
        Assert.Contains("snapshot.view", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Trace_timeline_renders_every_kind_duration_and_error_code()
    {
        var html = Report().GetProperty("trace").GetString()!;

        foreach (var marker in new[] { "task_abc123", "Failed", "started", "step", "tool", "final", "clock" })
            Assert.Contains(marker, html, StringComparison.Ordinal);

        // Error code is shown and copyable; no parsed text timeline remains.
        Assert.Contains("agent.timeout", html, StringComparison.Ordinal);
        Assert.Contains("duration bar", html, StringComparison.Ordinal);
        Assert.DoesNotContain("task_", html.Replace("task_abc123", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Trace_timeline_empty_state_is_explicit()
    {
        var html = Report().GetProperty("traceEmpty").GetString()!;

        Assert.Contains("该任务没有持久化的 trace 事件", html, StringComparison.Ordinal);
        Assert.Contains("4", html, StringComparison.Ordinal); // version still shown in the summary
    }

    [Fact]
    public void Config_view_keeps_the_disabled_and_unavailable_branches()
    {
        var report = Report();
        var disabled = report.GetProperty("configDisabled").GetString()!;
        var unavailable = report.GetProperty("configUnavailable").GetString()!;

        // readOnly / allowConfigWrite=false must be "功能未启用", not a vague message.
        Assert.Contains("功能未启用", disabled, StringComparison.Ordinal);
        Assert.Contains("保存（不可写）", disabled, StringComparison.Ordinal);

        // Missing host contract is a 501-style reason, distinct from the disabled one.
        Assert.Contains("host.admin_config.unavailable", unavailable, StringComparison.Ordinal);
        Assert.NotEqual(disabled, unavailable);
    }

    [Fact]
    public void Config_diff_reports_original_and_hidden_line_counts()
    {
        var html = Report().GetProperty("diff").GetString()!;

        Assert.Contains("原文 90 行", html, StringComparison.Ordinal);
        Assert.Contains("显示 60 行", html, StringComparison.Ordinal);
        Assert.Contains("已截断 30 行", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Commands_view_distinguishes_permission_from_disabled()
    {
        var report = Report();
        var noPermission = report.GetProperty("commandsNoPermission").GetString()!;
        var disabled = report.GetProperty("commandsDisabled").GetString()!;

        Assert.Contains("需要 admin 角色", noPermission, StringComparison.Ordinal);
        Assert.Contains("功能未启用", disabled, StringComparison.Ordinal);
        Assert.NotEqual(noPermission, disabled);
    }

    [Fact]
    public void Diag_and_logs_views_render_their_initial_states()
    {
        var report = Report();

        Assert.Contains("尚未导出诊断包", report.GetProperty("diagIdle").GetString()!, StringComparison.Ordinal);
        Assert.Contains("宿主日志", report.GetProperty("logsUnavailable").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void Login_prompt_shows_the_reason_and_code()
    {
        var html = Report().GetProperty("login").GetString()!;

        Assert.Contains("auth.required", html, StringComparison.Ordinal);
        Assert.Contains("会话已过期", html, StringComparison.Ordinal);
        Assert.Contains("Token", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Three_state_components_render_distinct_markup()
    {
        var states = Report().GetProperty("states");

        Assert.Contains("ant-skeleton", states.GetProperty("loading").GetString()!, StringComparison.Ordinal);

        var error = states.GetProperty("error").GetString()!;
        Assert.Contains("auth.forbidden", error, StringComparison.Ordinal);
        Assert.Contains("需要 admin 角色", error, StringComparison.Ordinal);
        // AntD spaces two-character CJK button labels ("重 试"); compare without whitespace.
        Assert.Contains("重试", error.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

        var noData = states.GetProperty("emptyNoData").GetString()!;
        var noPermission = states.GetProperty("emptyNoPermission").GetString()!;
        var disabled = states.GetProperty("emptyDisabled").GetString()!;
        var host = states.GetProperty("emptyHost").GetString()!;

        Assert.Contains("当前没有数据", noData, StringComparison.Ordinal);
        Assert.Contains("需要 admin 角色", noPermission, StringComparison.Ordinal);
        Assert.Contains("功能未启用", disabled, StringComparison.Ordinal);
        Assert.Contains("501", host, StringComparison.Ordinal);
        Assert.Equal(4, new[] { noData, noPermission, disabled, host }.Distinct().Count());
    }

    [Fact]
    public void Duration_prefers_the_measured_value_and_labels_the_fallback_as_estimate()
    {
        var html = Report().GetProperty("durations").GetString()!;

        // ① Measured: 45ms shown as the tool's own duration, no estimate marker on that row.
        Assert.Contains("45ms", html, StringComparison.Ordinal);
        // Fallback (step without ms): the gap is shown but explicitly tagged as an estimate.
        Assert.Contains("估算", html, StringComparison.Ordinal);
        Assert.Contains("estimated", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Config_dirty_detection_covers_edits_and_whitespace()
    {
        var dirty = Report().GetProperty("dirty");

        Assert.False(dirty.GetProperty("unchanged").GetBoolean());
        Assert.True(dirty.GetProperty("changed").GetBoolean());
        Assert.False(dirty.GetProperty("bothEmpty").GetBoolean());
        Assert.True(dirty.GetProperty("trailingWhitespace").GetBoolean());
    }

    [Fact]
    public void Downloads_use_a_blob_anchor_not_browser_navigation()
    {
        var download = Report().GetProperty("download");

        Assert.Equal("natter-logs-1.jsonl", download.GetProperty("fileName").GetString());
        Assert.Equal(6, download.GetProperty("bytes").GetInt32());
        Assert.Equal("blob:probe", download.GetProperty("url").GetString());
        Assert.Equal("blob:probe", download.GetProperty("anchorHref").GetString());
        Assert.Equal("natter-logs-1.jsonl", download.GetProperty("anchorDownload").GetString());
        Assert.Equal("blob:probe", download.GetProperty("revoked").GetString());
        Assert.True(download.GetProperty("truncated").GetBoolean());
        Assert.Equal(10, download.GetProperty("files").GetInt32());
    }

    [Fact]
    public void Frontend_sources_never_use_location_assign_for_downloads()
    {
        var root = Path.Combine(RepositoryRoot(), "ui", "src");
        var sources = Directory.GetFiles(root, "*.tsx", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(root, "*.ts", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}legacy{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        foreach (var source in sources)
        {
            // Comments may mention the forbidden API; only an actual call is a violation.
            Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"location\s*\.\s*assign\s*\("),
                File.ReadAllText(source));
        }
    }

    [Fact]
    public void Conversation_messages_are_escaped_not_rendered_as_html()
    {
        var html = Report().GetProperty("messages").GetString()!;

        Assert.Contains("hello", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img src=x", html, StringComparison.Ordinal);
        Assert.Contains("&lt;img", html, StringComparison.Ordinal);
    }

    private static JsonElement Report()
    {
        if (cachedReport is { } cached)
        {
            return cached;
        }

        Assert.True(cachedFailure is null, cachedFailure);

        var root = RepositoryRoot();
        var harness = Path.Combine(root, "tests", "Natter.Plugin.WebConsole.Tests", "ui", "render.tsx");
        // CJS bundle: react-dom/server ships CJS and dynamic `require` breaks in ESM output.
        var bundle = Path.Combine(Path.GetTempPath(), "natter-ui-render-" + Guid.NewGuid().ToString("N")[..8] + ".cjs");
        var esbuild = Path.Combine(root, "ui", "node_modules", ".bin", "esbuild");

        Assert.True(File.Exists(esbuild), $"UI dependencies are missing ({esbuild}); run npm ci in ui/ first.");

        // The harness lives under tests/, so module resolution needs the UI workspace on NODE_PATH.
        Run(
            esbuild,
            $"\"{harness}\" --bundle --platform=node --format=cjs --jsx=automatic --log-level=warning --outfile=\"{bundle}\"",
            nodePath: Path.Combine(root, "ui", "node_modules"));
        var json = Run("node", $"\"{bundle}\"");
        cachedReport = JsonDocument.Parse(json).RootElement.Clone();
        return cachedReport.Value;
    }

    private static string Run(string fileName, string arguments, string? nodePath = null)
    {
        var start = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = RepositoryRoot(),
        };

        if (nodePath is not null)
            start.Environment["NODE_PATH"] = nodePath;

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(TimeSpan.FromSeconds(120));

        if (process.ExitCode != 0)
        {
            cachedFailure = $"render harness failed ({process.ExitCode}): {stderr}";
            Assert.Fail(cachedFailure);
        }

        return stdout;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Natter.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? Directory.GetCurrentDirectory();
    }
}
