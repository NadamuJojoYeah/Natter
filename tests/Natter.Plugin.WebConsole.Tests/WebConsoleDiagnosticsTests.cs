using Xunit;

namespace Natter.Plugin.WebConsole.Tests;

public sealed class WebConsoleDiagnosticsTests
{
    [Fact]
    public async Task Snapshot_reports_audit_check_and_degraded_host_sections()
    {
        using var harness = WebConsoleHarness.Create();

        var snapshot = await harness.Diagnostics.CreateSnapshotAsync();

        Assert.Equal(6, snapshot.Checks.Count);
        Assert.Contains(snapshot.Checks, check => check.Name == "audit" && check.Status == "ok");
        Assert.Contains(snapshot.Checks, check => check.Name == "server" && check.Status == "ok");
        Assert.Contains(snapshot.Checks, check => check.Name == "host.health" && check.Status == "degraded");
        Assert.Equal("degraded", snapshot.Self.HealthStatus);
        Assert.False(snapshot.Logs.Available);
        Assert.Equal("host.logs.unavailable", snapshot.Logs.GapCode);
        Assert.NotEmpty(snapshot.Gaps);
        Assert.True(snapshot.Config.Values.ValueKind == System.Text.Json.JsonValueKind.Object);
    }

    [Fact]
    public async Task Snapshot_marks_token_failure_as_error()
    {
        using var harness = WebConsoleHarness.Create();
        var identity = new WebConsoleIdentity(
            "natter.plugin.webconsole",
            "Natter WebConsole",
            "0.1.0",
            "tool",
            "0.1.0",
            "Natter.Plugin.WebConsole",
            harness.Directory,
            Path.Combine(harness.Directory, "plugin.json"));

        var diagnostics = new WebConsoleDiagnostics(
            identity,
            harness.Options,
            harness.Diagnostics.ConfigPreview,
            UnavailableWebConsoleHostPort.Instance,
            harness.Status,
            harness.Audit,
            new WebConsoleTokenAuthenticator("short"),
            WebConsoleStaticAssets.Load(Path.Combine(harness.Directory, "wwwroot")),
            harness.Clock);

        var snapshot = await diagnostics.CreateSnapshotAsync();

        Assert.Contains(snapshot.Checks, check => check.Name == "token" && check.Status == "error");
        Assert.Equal("error", snapshot.Self.HealthStatus);
    }
}
