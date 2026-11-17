using System.Text.RegularExpressions;
using Natter.Abstractions.Plugins;
using Natter.Application.Chat;
using Natter.Chat;

namespace Natter.Cli.Core.Commands;

/// <summary>Single health snapshot builder shared by `natter health` and `/health`.</summary>
public static class RuntimeHealth
{
    private static readonly Regex SecretPattern = new(
        @"(sk-[A-Za-z0-9_\-]{8,}|ak-[A-Za-z0-9_\-]{8,}|Bearer\s+[A-Za-z0-9._\-]{8,})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Sanitize(string message)
        => string.IsNullOrEmpty(message) ? message : SecretPattern.Replace(message, "[redacted]");

    public static async ValueTask<RuntimeHealthSnapshot> CreateAsync(
        IChatUpstreamRegistry upstreams,
        IRuntimeProfileStore profiles,
        IChatResponderRegistry responders,
        IConversationStore conversations,
        IPluginCatalog plugins,
        HostRuntimeClock clock,
        CancellationToken cancellationToken = default)
    {
        var conversationCount = await conversations.CountAsync(cancellationToken).ConfigureAwait(false);
        var providers = profiles.Current.Profiles
            .Select(profile => new RuntimeHealthProvider(
                profile.Ai.Provider,
                responders.TryGet(profile.Ai.Provider, out _),
                profile.Id,
                profile.SupportTier))
            .DistinctBy(item => item.Id)
            .ToArray();

        var failedPlugins = plugins.Plugins
            .Where(plugin => plugin.State == PluginState.Failed)
            .Select(plugin => new RuntimeHealthPlugin(
                plugin.Id,
                plugin.State.ToString(),
                plugin.ErrorDetail?.Phase ?? "unknown",
                plugin.ErrorDetail?.Code ?? "unknown",
                Sanitize(plugin.ErrorDetail?.Message ?? plugin.Error ?? "plugin failed")))
            .ToArray();

        var upstreamSnapshots = upstreams.Upstreams
            .Select(upstream =>
            {
                if (upstream is IChatUpstreamHealth health)
                {
                    var value = health.Health;
                    return new RuntimeHealthUpstream(
                        value.SourceKey,
                        value.Connected,
                        value.LastEventAt,
                        value.LastConnectedAt,
                        value.LastDisconnectAt,
                        value.ReconnectCount,
                        value.LastError,
                        value.SendFailures,
                        value.LastSendError);
                }
                return new RuntimeHealthUpstream(upstream.Source.Key, upstream.IsConnected);
            })
            .ToArray();

        var tier1Unavailable = providers.Any(provider => provider.SupportTier == 1 && !provider.Available);
        var status = upstreamSnapshots.Length == 0
            ? "unknown"
            : upstreamSnapshots.Any(upstream => !upstream.Connected)
                || tier1Unavailable
                || failedPlugins.Length > 0
                ? "degraded"
                : "healthy";

        var sampledAt = DateTimeOffset.UtcNow;
        return new RuntimeHealthSnapshot(
            status,
            clock.UptimeSeconds,
            clock.StartedAt,
            sampledAt,
            profiles.Current.Profiles.Count,
            conversationCount,
            upstreamSnapshots,
            providers,
            new RuntimeHealthPlugins(
                plugins.Plugins.Count(plugin => plugin.State == PluginState.Activated),
                failedPlugins.Length,
                failedPlugins));
    }
}

public sealed record RuntimeHealthSnapshot(
    string Status,
    long UptimeSeconds,
    DateTimeOffset StartedAt,
    DateTimeOffset SampledAt,
    int Profiles,
    int Conversations,
    IReadOnlyList<RuntimeHealthUpstream> Upstreams,
    IReadOnlyList<RuntimeHealthProvider> Providers,
    RuntimeHealthPlugins Plugins);

public sealed record RuntimeHealthUpstream(
    string Source,
    bool Connected,
    DateTimeOffset? LastEventAt = null,
    DateTimeOffset? LastConnectedAt = null,
    DateTimeOffset? LastDisconnectAt = null,
    int ReconnectCount = 0,
    string? LastError = null,
    int SendFailures = 0,
    string? LastSendError = null);

public sealed record RuntimeHealthProvider(string Id, bool Available, string Profile, int SupportTier);

public sealed record RuntimeHealthPlugins(int Activated, int Failed, IReadOnlyList<RuntimeHealthPlugin> Failures);

public sealed record RuntimeHealthPlugin(string Id, string State, string Phase, string Code, string Message);
