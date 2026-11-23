using System.Net;
using Natter.Abstractions.Plugins;
using Natter.Plugin.Abstractions;
using Xunit;

namespace Natter.Plugin.WebConsole.Tests;

public sealed class WebConsoleHostPortTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 2, 0, 10, TimeSpan.Zero);

    private const string HealthJson = """
        {
          "status": "healthy",
          "uptimeSeconds": 42,
          "startedAt": "2026-11-18T02:00:00Z",
          "sampledAt": "2026-11-18T02:00:10Z",
          "profiles": 2,
          "conversations": 3,
          "tasks": { "created": 1, "completed": 2, "failed": 0, "cancelled": 0, "active": 1 },
          "upstreams": [
            {
              "source": "discord:bot",
              "connected": true,
              "lastConnectedAt": "2026-11-18T01:59:59Z",
              "lastDisconnectAt": "2026-11-18T01:59:50Z",
              "reconnectCount": 1,
              "lastError": null,
              "lastDisconnectError": "gateway closed (1006)",
              "lastDisconnectCode": "ws_closed",
              "sendFailures": 0
            }
          ],
          "providers": [
            { "id": "deepseek", "available": true, "profile": "default", "supportTier": 1 }
          ],
          "plugins": {
            "activated": 2,
            "failed": 1,
            "failures": [
              { "id": "natter.sample.broken", "state": "Failed", "phase": "activate", "code": "boom", "message": "activation failed" }
            ]
          }
        }
        """;

    [Fact]
    public async Task Health_endpoint_redacts_upstream_diagnostic_text()
    {
        const string canary = "sk-live-ABCDEFGHIJKLMNOPQRSTUV";
        var handler = new StaticJsonHandler(HealthJson.Replace(
            "\"lastError\": null",
            "\"lastError\": \"" + canary + "\"",
            StringComparison.Ordinal));
        using var client = new HttpClient(handler);
        using var port = new HealthEndpointHostPort(new Uri("http://127.0.0.1:5099/health"), client, new FakeTimeProvider(Now));

        var snapshot = await port.ReadAsync();

        Assert.DoesNotContain(canary, snapshot.Upstreams.Data![0].LastError!, StringComparison.Ordinal);
        Assert.Contains("[redacted]", snapshot.Upstreams.Data[0].LastError!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_endpoint_maps_sections()
    {
        var handler = new StaticJsonHandler(HealthJson);
        using var client = new HttpClient(handler);
        using var port = new HealthEndpointHostPort(new Uri("http://127.0.0.1:5099/health"), client, new FakeTimeProvider(Now));

        var snapshot = await port.ReadAsync();

        Assert.True(snapshot.Health.Available);
        Assert.Equal("healthy", snapshot.Health.Data!.Status);
        Assert.Equal(42, snapshot.Health.Data.UptimeSeconds);
        Assert.Equal(2, snapshot.Health.Data.Profiles);
        Assert.Equal(1, snapshot.Health.Data.Tasks.Active);

        Assert.True(snapshot.Upstreams.Available);
        Assert.Equal("discord:bot", snapshot.Upstreams.Data![0].Source);
        Assert.True(snapshot.Upstreams.Data[0].Connected);
        Assert.Null(snapshot.Upstreams.Data[0].LastError);
        Assert.Equal("gateway closed (1006)", snapshot.Upstreams.Data[0].LastDisconnectError);
        Assert.Equal("ws_closed", snapshot.Upstreams.Data[0].LastDisconnectCode);

        Assert.True(snapshot.Profiles.Available);
        Assert.Equal("deepseek", snapshot.Profiles.Data![0].Provider);
        Assert.Equal(1, snapshot.Profiles.Data[0].SupportTier);

        Assert.True(snapshot.Plugins.Available);
        Assert.Equal(2, snapshot.Plugins.Data!.Activated);
        Assert.Equal(1, snapshot.Plugins.Data.Failed);
        Assert.Equal("boom", snapshot.Plugins.Data.Failures[0].Code);
        Assert.Equal("health-endpoint", snapshot.Plugins.Data.Source);
    }

    [Fact]
    public async Task Health_endpoint_result_is_cached_briefly()
    {
        var handler = new StaticJsonHandler(HealthJson);
        using var client = new HttpClient(handler);
        var clock = new FakeTimeProvider(Now);
        using var port = new HealthEndpointHostPort(new Uri("http://127.0.0.1:5099/health"), client, clock);

        await port.ReadAsync();
        clock.Advance(TimeSpan.FromSeconds(2));
        await port.ReadAsync();
        Assert.Equal(1, handler.Calls);

        clock.Advance(TimeSpan.FromSeconds(2));
        await port.ReadAsync();
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Unreachable_health_endpoint_degrades_without_throwing()
    {
        using var client = new HttpClient(new ThrowingHandler());
        using var port = new HealthEndpointHostPort(new Uri("http://127.0.0.1:5099/health"), client, new FakeTimeProvider(Now));

        var snapshot = await port.ReadAsync();

        Assert.False(snapshot.Health.Available);
        Assert.Equal("host.health.unavailable", snapshot.Health.GapCode);
        Assert.False(snapshot.Upstreams.Available);
        Assert.False(snapshot.Plugins.Available);
    }

    [Fact]
    public async Task Plugin_diagnostics_port_maps_graph_when_available()
    {
        var diagnostics = new FakeRuntimeDiagnostics
        {
            Graph =
            [
                new PluginGraphNode("natter.sample.healthy", "0.1.0", "feature", PluginState.Activated, [], [], []),
                new PluginGraphNode("natter.sample.broken", "0.1.0", "feature", PluginState.Failed, [], [], []),
            ],
            Contributions =
            [
                new PluginContributionInfo("cli.command", "metrics", "natter.sample.healthy", 0, "active"),
            ],
            Permissions =
            [
                new PluginPermissionInfo("natter.sample.healthy", ["cli.commands"], ["cli.commands"], []),
            ],
        };

        var provider = new FakeServiceProvider().Add<IPluginRuntimeDiagnostics>(diagnostics);
        var port = new PluginDiagnosticsHostPort(provider);

        var snapshot = await port.ReadAsync();

        Assert.True(snapshot.Plugins.Available);
        var plugins = snapshot.Plugins.Data!;
        Assert.Equal("plugin-runtime", plugins.Source);
        Assert.Equal(1, plugins.Activated);
        Assert.Equal(1, plugins.Failed);
        Assert.Equal(2, plugins.Nodes.Count);
        Assert.Contains(plugins.Nodes, node => node.Id == "natter.sample.healthy" && node.State == "Activated");
        Assert.Single(plugins.Contributions);
        Assert.Equal("metrics", plugins.Contributions[0].Id);
        Assert.Single(plugins.Failures);
        Assert.Equal("boom", plugins.Failures[0].Code);
        Assert.False(snapshot.Health.Available);
    }

    [Fact]
    public async Task Missing_plugin_diagnostics_reports_documented_gap()
    {
        var port = new PluginDiagnosticsHostPort(new FakeServiceProvider());

        var snapshot = await port.ReadAsync();

        Assert.False(snapshot.Plugins.Available);
        Assert.Equal("host.plugin_diagnostics.unavailable", snapshot.Plugins.GapCode);
    }

    [Fact]
    public async Task Composite_prefers_health_sections_and_plugin_runtime_details()
    {
        var healthHandler = new StaticJsonHandler(HealthJson);
        using var client = new HttpClient(healthHandler);
        using var health = new HealthEndpointHostPort(new Uri("http://127.0.0.1:5099/health"), client, new FakeTimeProvider(Now));

        var diagnostics = new FakeRuntimeDiagnostics
        {
            Graph =
            [
                new PluginGraphNode("natter.sample.healthy", "0.1.0", "feature", PluginState.Activated, [], [], []),
            ],
        };
        var pluginPort = new PluginDiagnosticsHostPort(
            new FakeServiceProvider().Add<IPluginRuntimeDiagnostics>(diagnostics));

        var composite = new CompositeWebConsoleHostPort(health, pluginPort);
        var snapshot = await composite.ReadAsync();

        Assert.True(snapshot.Health.Available);
        Assert.Equal("healthy", snapshot.Health.Data!.Status);
        Assert.True(snapshot.Plugins.Available);
        Assert.Equal("plugin-runtime", snapshot.Plugins.Data!.Source);
        Assert.Equal(1, snapshot.Plugins.Data.Activated);
        Assert.True(snapshot.Profiles.Available);
        Assert.True(snapshot.Upstreams.Available);
    }
}
