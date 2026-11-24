using Natter.Cli.Abstractions;

namespace Natter.Infrastructure.Plugins.Nps;

/// <summary>
/// Lets the NPS runtime publish and withdraw the CLI commands a plugin contributes.
///
/// Implemented by the CLI command registry (in <c>Natter.Cli.Core</c>), so the runtime can keep the
/// registry in sync with the plugin lifecycle without taking a dependency on the CLI kernel
/// (Infrastructure must not reference Cli.Core — the direction is Cli.Core → Infrastructure).
/// </summary>
public interface IPluginCliCommandSink
{
    /// <summary>
    /// Replaces every command owned by <paramref name="owner"/> with <paramref name="commands"/>.
    /// Passing an empty sequence withdraws them. Implementations must be idempotent.
    /// </summary>
    void ReplaceOwnerCommands(string owner, IEnumerable<CliCommandDescriptor> commands);
}

/// <summary>Owner key used for NPS-contributed CLI commands; shared by runtime and registry.</summary>
public static class NpsCliCommandOwners
{
    /// <summary>Owner-key prefix used for every NPS-contributed CLI command.</summary>
    public const string Prefix = "plugin:";

    /// <summary>Returns the owner key for one plugin id, e.g. <c>plugin:natter.transport.messaging.discord</c>.</summary>
    public static string For(string pluginId) => $"{Prefix}{pluginId}";
}
