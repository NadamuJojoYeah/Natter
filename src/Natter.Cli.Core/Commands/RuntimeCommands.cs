using System.Diagnostics;
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
                failed = plugins.Plugins.Count(plugin => plugin.State == PluginState.Failed),
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

        return new HealthHttpServer(port, async () =>
        {
            var conversationCount = await conversations.CountAsync().ConfigureAwait(false);
            return new
            {
                status = upstreams.Upstreams.All(upstream => upstream.IsConnected) ? "healthy" : "degraded",
                profiles = profiles.Current.Profiles.Count,
                conversations = conversationCount,
                providers = profiles.Current.Profiles
                    .Select(profile => profile.Ai.Provider)
                    .Distinct(StringComparer.Ordinal)
                    .Select(provider => new
                    {
                        id = provider,
                        available = responders.TryGet(provider, out _),
                    })
                    .ToArray(),
                upstreams = upstreams.Upstreams.Select(upstream => new
                {
                    source = upstream.Source.Key,
                    connected = upstream.IsConnected,
                }).ToArray(),
                plugins = new
                {
                    activated = plugins.Plugins.Count(plugin => plugin.State == PluginState.Activated),
                    failed = plugins.Plugins.Count(plugin => plugin.State == PluginState.Failed),
                },
            };
        });
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
    private static readonly Stopwatch Uptime = Stopwatch.StartNew();

    public async ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var conversationCount = await conversations.CountAsync(cancellationToken).ConfigureAwait(false);
        var providers = profiles.Current.Profiles
            .Select(profile => new
            {
                id = profile.Ai.Provider,
                available = responders.TryGet(profile.Ai.Provider, out _),
                profile = profile.Id,
                supportTier = profile.SupportTier,
            })
            .DistinctBy(item => item.id)
            .ToArray();

        var snapshot = new
        {
            status = upstreams.Upstreams.All(upstream => upstream.IsConnected) ? "healthy" : "degraded",
            uptimeSeconds = (long)Uptime.Elapsed.TotalSeconds,
            profiles = profiles.Current.Profiles.Count,
            conversations = conversationCount,
            upstreams = upstreams.Upstreams.Select(upstream => new
            {
                source = upstream.Source.Key,
                connected = upstream.IsConnected,
            }).ToArray(),
            providers,
            plugins = new
            {
                activated = plugins.Plugins.Count(plugin => plugin.State == PluginState.Activated),
                failed = plugins.Plugins.Count(plugin => plugin.State == PluginState.Failed),
            },
        };

        if (invocation.HasFlag("json"))
        {
            invocation.Console.Out.WriteLine(JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false,
            }));
            return CliExitCodes.Success;
        }

        invocation.Console.Out.WriteLine($"status: {snapshot.status}");
        invocation.Console.Out.WriteLine($"uptimeSeconds: {snapshot.uptimeSeconds}");
        invocation.Console.Out.WriteLine($"profiles: {snapshot.profiles}");
        invocation.Console.Out.WriteLine($"conversations: {snapshot.conversations}");
        foreach (var upstream in snapshot.upstreams)
            invocation.Console.Out.WriteLine($"upstream {upstream.source} connected={upstream.connected}");
        foreach (var provider in providers)
            invocation.Console.Out.WriteLine($"provider {provider.id} available={provider.available} tier={provider.supportTier}");
        invocation.Console.Out.WriteLine($"plugins: activated={snapshot.plugins.activated} failed={snapshot.plugins.failed}");
        return CliExitCodes.Success;
    }
}
