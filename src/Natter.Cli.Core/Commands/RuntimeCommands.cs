using System.Text.Json;
using Natter.Abstractions.Plugins;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Cli.Abstractions;

namespace Natter.Cli.Core.Commands;

/// <summary>Long-running host mode. The host is already started by NatterCli.</summary>
public sealed class ServeCommand(
    IChatUpstreamRegistry upstreams,
    IRuntimeProfileStore profiles,
    IChatResponderRegistry responders,
    IConversationStore conversations,
    IPluginCatalog plugins) : ICliCommandHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

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
            invocation.Console.Out.WriteLine(JsonSerializer.Serialize(summary, JsonOptions));
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
            plugins).ConfigureAwait(false));
    }
}

/// <summary>One-shot health snapshot of upstreams, profiles, providers and plugins.</summary>
public sealed class HealthCommand(
    IChatUpstreamRegistry upstreams,
    IRuntimeProfileStore profiles,
    IChatResponderRegistry responders,
    IConversationStore conversations,
    IPluginCatalog plugins) : ICliCommandHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public async ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var snapshot = await RuntimeHealth.CreateAsync(
            upstreams,
            profiles,
            responders,
            conversations,
            plugins,
            cancellationToken).ConfigureAwait(false);

        if (invocation.HasFlag("json"))
        {
            invocation.Console.Out.WriteLine(JsonSerializer.Serialize(snapshot, JsonOptions));
            return CliExitCodes.Success;
        }

        invocation.Console.Out.WriteLine($"status: {snapshot.Status}");
        invocation.Console.Out.WriteLine($"uptimeSeconds: {snapshot.UptimeSeconds}");
        invocation.Console.Out.WriteLine($"profiles: {snapshot.Profiles}");
        invocation.Console.Out.WriteLine($"conversations: {snapshot.Conversations}");
        foreach (var upstream in snapshot.Upstreams)
            invocation.Console.Out.WriteLine($"upstream {upstream.Source} connected={upstream.Connected}");
        foreach (var provider in snapshot.Providers)
            invocation.Console.Out.WriteLine($"provider {provider.Id} available={provider.Available} tier={provider.SupportTier}");
        invocation.Console.Out.WriteLine($"plugins: activated={snapshot.Plugins.Activated} failed={snapshot.Plugins.Failed}");
        foreach (var failure in snapshot.Plugins.Failures)
            invocation.Console.Out.WriteLine(
                $"plugin failed {failure.Id} phase={failure.Phase} code={failure.Code}: {failure.Message}");
        return CliExitCodes.Success;
    }
}
