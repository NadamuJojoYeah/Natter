using Natter.Cli.Abstractions;
using Natter.Cli.Core.Rendering;
using Natter.Plugin.Store;

namespace Natter.Cli.Core.Commands;

/// <summary>Shared CLI construction for the platform-neutral plugin store.</summary>
internal static class PluginStoreCli
{
    public const string RootOption = "root";
    public const string TrustedKeyOption = "trusted-key";
    public const string AllowUnsignedOption = "allow-unsigned";

    public static PluginStore Create(CliInvocation invocation)
    {
        var root = invocation.GetOption(RootOption)
                   ?? Environment.GetEnvironmentVariable("NATTER_PLUGIN_STORE")
                   ?? Path.Combine(AppContext.BaseDirectory, "plugin-store");

        var keys = (invocation.GetOption(TrustedKeyOption) ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var options = new PluginStoreOptions
        {
            RootDirectory = root,
            Platform = "android",
            TrustedPublicKeys = keys,
            AllowUnsigned = invocation.HasFlag(AllowUnsignedOption),
        };

        return new PluginStore(options);
    }

    public static IEnumerable<CliOptionDescriptor> CommonOptions()
    {
        yield return new CliOptionDescriptor(RootOption, "Plugin store root directory.", TakesValue: true);
        yield return new CliOptionDescriptor(TrustedKeyOption, "Comma-separated base64 ECDSA P-256 publisher keys.", TakesValue: true);
        yield return new CliOptionDescriptor(AllowUnsignedOption, "Allow unsigned packages (debug only).");
    }
}

/// <summary>Lists installed plugin store records.</summary>
public sealed class PluginsStoreListCommand : ICliCommandHandler
{
    public ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var store = PluginStoreCli.Create(invocation);
        var plugins = store.GetInstalledPlugins();

        if (invocation.HasFlag("json"))
        {
            invocation.Console.Out.WriteLine(JsonOutput.Serialize(plugins));
            return ValueTask.FromResult(CliExitCodes.Success);
        }

        ConsoleTable.Write(
            invocation.Console.Out,
            ["ID", "NAME", "VERSION", "ACTIVE", "ENABLED", "TRUST", "STATE", "FAILURE"],
            plugins.Select(plugin => (IReadOnlyList<string>)
            [
                plugin.Id,
                plugin.Name,
                plugin.Version,
                plugin.ActiveVersion,
                plugin.Enabled.ToString().ToLowerInvariant(),
                plugin.Trust,
                plugin.LastKnownState.ToString(),
                plugin.FailureReason ?? "-",
            ]).ToList());

        return ValueTask.FromResult(CliExitCodes.Success);
    }
}

/// <summary>Installs/updates a package from disk.</summary>
public sealed class PluginsStoreInstallCommand : ICliCommandHandler
{
    public async ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var packagePath = invocation.GetPositional(0);
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            invocation.Console.Error.WriteLine("usage: natter plugins store install <package> [--root <dir>] [--trusted-key <key>] [--allow-unsigned]");
            return CliExitCodes.Usage;
        }

        try
        {
            var store = PluginStoreCli.Create(invocation);
            var result = await store.InstallAsync(packagePath, allowUpdate: !invocation.HasFlag("no-update"), cancellationToken)
                .ConfigureAwait(false);

            if (invocation.HasFlag("json"))
            {
                invocation.Console.Out.WriteLine(JsonOutput.Serialize(new
                {
                    installed = result.Record,
                    signerKeyId = result.Package.SignerKeyId,
                    isUpdate = result.IsUpdate,
                }));
            }
            else
            {
                invocation.Console.Out.WriteLine(
                    $"installed {result.Record.Id} {result.Record.ActiveVersion} "
                    + $"({result.Record.Trust}, update={result.IsUpdate.ToString().ToLowerInvariant()})");
                invocation.Console.Out.WriteLine("restart the host to activate the new version");
            }

            return CliExitCodes.Success;
        }
        catch (PluginStoreException ex)
        {
            invocation.Console.Error.WriteLine($"install failed: {ex.Message}");
            return CliExitCodes.Failure;
        }
    }
}

/// <summary>Enables/disables an installed plugin (restart required).</summary>
public sealed class PluginsStoreEnableCommand : ICliCommandHandler
{
    private readonly bool _enabled;

    public PluginsStoreEnableCommand(bool enabled) => _enabled = enabled;

    public async ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var pluginId = invocation.GetPositional(0);
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            invocation.Console.Error.WriteLine("usage: natter plugins store enable|disable <plugin-id> [--root <dir>]");
            return CliExitCodes.Usage;
        }

        try
        {
            var store = PluginStoreCli.Create(invocation);
            await store.SetEnabledAsync(pluginId, _enabled, cancellationToken).ConfigureAwait(false);
            invocation.Console.Out.WriteLine($"{(_enabled ? "enabled" : "disabled")} {pluginId}; restart the host to apply");
            return CliExitCodes.Success;
        }
        catch (PluginStoreException ex)
        {
            invocation.Console.Error.WriteLine(ex.Message);
            return CliExitCodes.Failure;
        }
    }
}

/// <summary>Rolls back to the previous version.</summary>
public sealed class PluginsStoreRollbackCommand : ICliCommandHandler
{
    public async ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var pluginId = invocation.GetPositional(0);
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            invocation.Console.Error.WriteLine("usage: natter plugins store rollback <plugin-id> [--root <dir>]");
            return CliExitCodes.Usage;
        }

        var store = PluginStoreCli.Create(invocation);
        var result = await store.RollbackAsync(pluginId, cancellationToken).ConfigureAwait(false);

        invocation.Console.Out.WriteLine(result.RolledBack
            ? $"rolled back {pluginId} to {result.ActiveVersion}; restart the host"
            : $"rollback failed for {pluginId}: {result.Reason}");

        return result.RolledBack ? CliExitCodes.Success : CliExitCodes.Failure;
    }
}

/// <summary>Uninstalls an installed plugin.</summary>
public sealed class PluginsStoreUninstallCommand : ICliCommandHandler
{
    public async ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var pluginId = invocation.GetPositional(0);
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            invocation.Console.Error.WriteLine(
                "usage: natter plugins store uninstall <plugin-id> [--delete-data] [--delete-config] [--force]");
            return CliExitCodes.Usage;
        }

        if (!Natter.Plugin.Store.PluginStoreIds.IsValid(pluginId))
        {
            invocation.Console.Error.WriteLine(
                $"error: plugin-id-invalid: '{pluginId}' must match {Natter.Plugin.Store.PluginStoreIds.Pattern}");
            return CliExitCodes.Usage;
        }

        try
        {
            var store = PluginStoreCli.Create(invocation);
            await store.UninstallAsync(
                pluginId,
                deleteData: invocation.HasFlag("delete-data"),
                deleteConfig: invocation.HasFlag("delete-config"),
                force: invocation.HasFlag("force"),
                cancellationToken).ConfigureAwait(false);

            invocation.Console.Out.WriteLine($"uninstalled {pluginId}; restart the host to apply");
            return CliExitCodes.Success;
        }
        catch (PluginStoreException ex)
        {
            invocation.Console.Error.WriteLine(ex.Message);
            return CliExitCodes.Failure;
        }
    }
}

/// <summary>Removes staging/trash leftovers.</summary>
public sealed class PluginsStoreCleanupCommand : ICliCommandHandler
{
    public async ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var store = PluginStoreCli.Create(invocation);
        await store.CleanupAsync(cancellationToken).ConfigureAwait(false);
        invocation.Console.Out.WriteLine("plugin store staging/trash cleaned.");
        return CliExitCodes.Success;
    }
}
