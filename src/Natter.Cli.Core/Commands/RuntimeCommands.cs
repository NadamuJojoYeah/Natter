using System.Text.Json;
using Natter.Abstractions.Plugins;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Cli.Abstractions;
using Natter.Cli.Core.Rendering;

namespace Natter.Cli.Core.Commands;

/// <summary>Long-running host mode. The host is already started by NatterCli.</summary>
public sealed class ServeCommand(
    IChatUpstreamRegistry upstreams,
    IRuntimeProfileStore profiles,
    IChatResponderRegistry responders,
    IConversationStore conversations,
    IPluginCatalog plugins,
    HostRuntimeClock clock,
    TaskMetrics taskMetrics) : ICliCommandHandler
{

    public async ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var duration = invocation.GetIntOption("duration", 0);
        var failed = plugins.Plugins
            .Where(plugin => plugin.State == PluginState.Failed)
            .Select(plugin => new
            {
                id = plugin.Id,
                state = plugin.State.ToString(),
                phase = plugin.ErrorDetail?.Phase ?? "unknown",
                code = plugin.ErrorDetail?.Code ?? "unknown",
                message = RuntimeHealth.Sanitize(plugin.ErrorDetail?.Message ?? plugin.Error ?? "plugin failed"),
            })
            .ToArray();

        var summary = new
        {
            mode = "serve",
            profiles = profiles.Current.Profiles.Count,
            defaultProfile = profiles.Current.DefaultProfile,
            providers = profiles.Current.Profiles
                .Select(profile => profile.Ai.Provider)
                .Distinct(StringComparer.Ordinal)
                .Count(provider => responders.TryGet(provider, out _)),
            upstreams = upstreams.Upstreams.Select(upstream => new
            {
                source = upstream.Source.Key,
                connected = upstream.IsConnected,
            }).ToArray(),
            plugins = new
            {
                activated = plugins.Plugins.Count(plugin => plugin.State == PluginState.Activated),
                failed = failed.Length,
                failures = failed,
            },
        };

        if (invocation.HasFlag("json"))
            invocation.Console.Out.WriteLine(JsonSerializer.Serialize(summary, JsonOutput.CompactOptions));
        else
        {
            invocation.Console.Out.WriteLine(
                $"serve: profiles={summary.profiles} providers={summary.providers} "
                + $"upstreams={summary.upstreams.Length} plugins={summary.plugins.activated} failed={summary.plugins.failed}");
            foreach (var upstream in summary.upstreams)
                invocation.Console.Out.WriteLine($"upstream {upstream.source} connected={upstream.connected}");
            foreach (var failure in summary.plugins.failures)
                invocation.Console.Out.WriteLine(
                    $"plugin failed {failure.id} phase={failure.phase} code={failure.code}: {failure.message}");
        }

        using var healthServer = CreateHealthServer(invocation);
        healthServer?.Start();

        if (duration <= 0)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        else
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(duration), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        return CliExitCodes.Success;
    }

    private HealthHttpServer? CreateHealthServer(CliInvocation invocation)
    {
        var port = invocation.GetIntOption("health-port", 0);
        if (port <= 0)
            return null;

        return new HealthHttpServer(port, async () => await RuntimeHealth.CreateAsync(
            upstreams,
            profiles,
            responders,
            conversations,
            plugins,
            clock,
            taskMetrics).ConfigureAwait(false));
    }
}

/// <summary>One-shot health snapshot of upstreams, profiles, providers and plugins.</summary>
public sealed class HealthCommand(
    IChatUpstreamRegistry upstreams,
    IRuntimeProfileStore profiles,
    IChatResponderRegistry responders,
    IConversationStore conversations,
    IPluginCatalog plugins,
    HostRuntimeClock clock,
    TaskMetrics taskMetrics) : ICliCommandHandler
{

    public async ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var snapshot = await RuntimeHealth.CreateAsync(
            upstreams,
            profiles,
            responders,
            conversations,
            plugins,
            clock,
            taskMetrics,
            cancellationToken).ConfigureAwait(false);
        if (invocation.HasFlag("offline"))
        {
            // Offline means "the host never ran": say so explicitly instead of reporting upstreams
            // as disconnected (S2-E2).
            snapshot = snapshot with { Status = "offline", PluginActivation = "not-activated" };
        }

        if (invocation.HasFlag("json"))
        {
            invocation.Console.Out.WriteLine(JsonOutput.SerializeCompact(snapshot));
            return ExitCodeFor(snapshot);
        }

        invocation.Console.Out.WriteLine($"status: {snapshot.Status}");
        if (invocation.HasFlag("offline"))
        {
            invocation.Console.Out.WriteLine("mode: offline (host not started, plugins not activated)");
            invocation.Console.Out.WriteLine(
                "pluginActivation: not-activated — upstream/provider rows below are static config, not live state");
        }
        invocation.Console.Out.WriteLine($"uptimeSeconds: {snapshot.UptimeSeconds}");
        invocation.Console.Out.WriteLine($"startedAt: {snapshot.StartedAt:O}");
        invocation.Console.Out.WriteLine($"sampledAt: {snapshot.SampledAt:O}");
        invocation.Console.Out.WriteLine($"profiles: {snapshot.Profiles}");
        invocation.Console.Out.WriteLine($"conversations: {snapshot.Conversations}");
        invocation.Console.Out.WriteLine($"activeTasks: {snapshot.Tasks.Active}");
        foreach (var upstream in snapshot.Upstreams)
            invocation.Console.Out.WriteLine($"upstream {upstream.Source} connected={upstream.Connected}");
        foreach (var provider in snapshot.Providers)
            invocation.Console.Out.WriteLine($"provider {provider.Id} available={provider.Available} tier={provider.SupportTier}");
        if (snapshot.Memory is { } memory)
        {
            invocation.Console.Out.WriteLine(
                $"memory: store={memory.Store} migrationWarning={(memory.MigrationWarning is null ? "none" : memory.MigrationWarning)}");
        }

        invocation.Console.Out.WriteLine($"plugins: activated={snapshot.Plugins.Activated} failed={snapshot.Plugins.Failed}");
        foreach (var failure in snapshot.Plugins.Failures)
            invocation.Console.Out.WriteLine(
                $"plugin failed {failure.Id} phase={failure.Phase} code={failure.Code}: {failure.Message}");
        return ExitCodeFor(snapshot);
    }

    /// <summary>
    /// S2-E3: `degraded` must not exit 0, otherwise CI/automation reads it as healthy.
    /// `plugins-failed` gets its own code: the host serves fine, but silently returning 0 would hide
    /// a plugin that never activated. Callers that only care about outages can compare with
    /// <see cref="CliHealthExitCodes.Degraded"/>.
    /// </summary>
    private static int ExitCodeFor(RuntimeHealthSnapshot snapshot)
        => snapshot.Status switch
        {
            var status when string.Equals(status, "degraded", StringComparison.OrdinalIgnoreCase)
                => CliHealthExitCodes.Degraded,
            var status when string.Equals(status, "plugins-failed", StringComparison.OrdinalIgnoreCase)
                => CliHealthExitCodes.PluginsFailed,
            _ => CliExitCodes.Success,
        };
}
