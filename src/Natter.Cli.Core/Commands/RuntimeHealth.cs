using System.Text.RegularExpressions;
using Natter.Abstractions.Plugins;
using Natter.Application.Chat;
using Natter.Chat;

namespace Natter.Cli.Core.Commands;

/// <summary>Exit codes specific to health probes (S2-E3: automation must not read degraded as OK).</summary>
public static class CliHealthExitCodes
{
    /// <summary>Snapshot is `degraded`: core capability is impaired (upstream down / Tier 1 provider
    /// unavailable). Automation must treat this as a failure.</summary>
    public const int Degraded = 4;

    /// <summary>Snapshot is `plugins-failed`: the host and its upstreams work, but at least one plugin
    /// failed to load (typically still unconfigured). Distinct from <see cref="Degraded"/> so callers
    /// can warn without treating a freshly installed plugin as an outage; exit 0 would hide it
    /// entirely, so a dedicated code is used instead.</summary>
    public const int PluginsFailed = 5;
}

/// <summary>Single health snapshot builder shared by `natter health` and `/health`.</summary>
public static class RuntimeHealth
{
    /// <summary>
    /// One shared redaction core (C-03): /health and --json are operator-facing, so a rule that
    /// exists only here would leak whatever the shared core already learned to catch.
    /// </summary>
    public static string Sanitize(string message)
        => string.IsNullOrEmpty(message)
            ? message
            : Natter.Application.Redaction.SecretRedaction.Apply(
                message,
                Natter.Application.Redaction.SecretRedactionOptions.Admin);

    /// <summary>
    /// Maps the raw facts onto one of four health states. Kept pure so every combination is testable
    /// without standing up a host.
    /// </summary>
    /// <remarks>
    /// Four states, because collapsing them hides which action is needed:
    /// <list type="bullet">
    /// <item><c>unknown</c>: no upstream is configured at all, so there is nothing to judge.</item>
    /// <item><c>degraded</c>: core capability is impaired — an upstream dropped or a Tier 1 provider
    /// is unavailable. Users cannot be served properly.</item>
    /// <item><c>plugins-failed</c>: core capability is fine, but at least one plugin failed to load.
    /// This is the expected state right after installing plugins that still need config or secrets,
    /// so it must stay distinguishable from a real outage.</item>
    /// <item><c>healthy</c>: everything works.</item>
    /// </list>
    /// Precedence: <c>degraded</c> outranks <c>plugins-failed</c> (core beats periphery).
    /// </remarks>
    public static string Classify(
        int upstreamCount,
        bool anyUpstreamDisconnected,
        bool tier1ProviderUnavailable,
        int failedPluginCount)
    {
        if (upstreamCount == 0)
            return "unknown";
        if (anyUpstreamDisconnected || tier1ProviderUnavailable)
            return "degraded";
        return failedPluginCount > 0 ? "plugins-failed" : "healthy";
    }

    public static async ValueTask<RuntimeHealthSnapshot> CreateAsync(
        IChatUpstreamRegistry upstreams,
        IRuntimeProfileStore profiles,
        IChatResponderRegistry responders,
        IConversationStore conversations,
        IPluginCatalog plugins,
        HostRuntimeClock clock,
        TaskMetrics taskMetrics,
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
                        Sanitize(value.LastError),
                        value.SendFailures,
                        Sanitize(value.LastSendError),
                        Sanitize(value.LastDisconnectError),
                        value.LastDisconnectCode);
                }
                return new RuntimeHealthUpstream(upstream.Source.Key, upstream.IsConnected);
            })
            .ToArray();

        // A Tier 1 provider only counts as "unavailable" when plugins were actually activated.
        // With no activated plugin the profile merely references an unloaded plugin (for example
        // `--no-plugins` or `health --offline`), which must not read as a runtime degradation.
        var anyPluginActivated = plugins.Plugins.Any(plugin => plugin.State == PluginState.Activated);
        var tier1Unavailable = anyPluginActivated
                               && providers.Any(provider => provider.SupportTier == 1 && !provider.Available);
        var status = Classify(
            upstreamSnapshots.Length,
            upstreamSnapshots.Any(upstream => !upstream.Connected),
            tier1Unavailable,
            failedPlugins.Length);

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
                failedPlugins),
            taskMetrics.Snapshot(),
            PluginActivation: null,
            Memory: new RuntimeHealthMemory(
                "layered",
                Natter.Application.Chat.MemoryMigrationDiagnostics.Warning));
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
    RuntimeHealthPlugins Plugins,
    TaskMetricsSnapshot Tasks,
    /// <summary>
    /// "activated" when the host ran and activated plugins, "not-activated" for `health --offline`.
    /// An offline snapshot must not look like a disconnected runtime (S2-E2).
    /// </summary>
    string? PluginActivation = null,
    /// <summary>
    /// Layered memory diagnostics (P6.7). Null when memory is not part of this snapshot (offline).
    /// A non-null <see cref="RuntimeHealthMemory.MigrationWarning"/> means the v1 → v2 copy failed and
    /// layered recall is running empty while v1 rows are preserved.
    /// </summary>
    RuntimeHealthMemory? Memory = null);

public sealed record RuntimeHealthUpstream(
    string Source,
    bool Connected,
    DateTimeOffset? LastEventAt = null,
    DateTimeOffset? LastConnectedAt = null,
    DateTimeOffset? LastDisconnectAt = null,
    int ReconnectCount = 0,
    string? LastError = null,
    int SendFailures = 0,
    string? LastSendError = null,
    string? LastDisconnectError = null,
    string? LastDisconnectCode = null);

public sealed record RuntimeHealthProvider(string Id, bool Available, string Profile, int SupportTier);

/// <summary>Layered memory state. Never carries memory text (§10.2).</summary>
public sealed record RuntimeHealthMemory(string Store, string? MigrationWarning);

public sealed record RuntimeHealthPlugins(int Activated, int Failed, IReadOnlyList<RuntimeHealthPlugin> Failures);

public sealed record RuntimeHealthPlugin(string Id, string State, string Phase, string Code, string Message);
