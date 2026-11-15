using Android.Content;
using Natter.Cli.Core;
using Natter.Infrastructure.Composition;
using Natter.Plugin.Abstractions;
using Natter.Plugin.Store;
#if !NATTER_DYNAMIC_PLUGINS
using Natter.Sample.Plugin.Greetings;
using Natter.Sample.Plugin.Metrics;
#endif

namespace Natter.Hosting.Android;

/// <summary>Android NPS host profile: embedded by default, app-private dynamic store in extended builds.</summary>
internal static class AndroidPluginHost
{
    public static bool IsDynamic { get; } = IsDynamicBuild();

    private static bool IsDynamicBuild()
    {
#if NATTER_DYNAMIC_PLUGINS
        return true;
#else
        return false;
#endif
    }

    public static PluginStore CreateStore(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var root = Path.Combine(context.FilesDir!.AbsolutePath, "plugins");

        var trustedKeys = LoadTrustedKeys(context);
        var options = new PluginStoreOptions
        {
            RootDirectory = root,
            Platform = "android",
            TrustedPublicKeys = trustedKeys,
#if DEBUG
            AllowUnsigned = true,
#else
            AllowUnsigned = false,
#endif
        };

        return new PluginStore(options);
    }

    public static void Configure(
        Context context,
        NatterCliOptions cliOptions,
        NatterApplicationBuilder builder,
        PluginStore? store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cliOptions);
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigurePlugins(options =>
        {
            options.SecretProvider = new AndroidKeystoreSecretProvider(context);
        });

        if (store is not null)
        {
            cliOptions.LoadPlugins = true;
            foreach (var directory in store.GetActivePluginDirectories())
                cliOptions.PluginDirectories.Add(directory);

            builder.ConfigureOptions(options =>
                options.PluginConfigDirectory = store.ConfigDirectory);

            cliOptions.OnHostStartedAsync = async services =>
            {
                var diagnostics = services.GetService(typeof(IPluginRuntimeDiagnostics)) as IPluginRuntimeDiagnostics;
                if (diagnostics is not null)
                    await store.ReconcileAsync(diagnostics).ConfigureAwait(false);
            };

            return;
        }

        cliOptions.LoadPlugins = false;
#if !NATTER_DYNAMIC_PLUGINS
        builder.AddEmbeddedNpsPlugin(GreetingsNpsPlugin.Manifest, new GreetingsNpsPlugin());
        builder.AddEmbeddedNpsPlugin(MetricsNpsPlugin.Manifest, new MetricsNpsPlugin());
#endif
    }

    /// <summary>
    /// Debug/sideload entry: install every .natter-plugin.zip from FilesDir/plugin-inbox.
    /// Production UI should call <see cref="PluginStore.InstallAsync"/> after user confirmation.
    /// </summary>
    public static async Task InstallInboxAsync(PluginStore store, Context context, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(log);

        var inbox = Path.Combine(context.FilesDir!.AbsolutePath, "plugin-inbox");
        var processed = Path.Combine(inbox, "processed");
        Directory.CreateDirectory(inbox);
        Directory.CreateDirectory(processed);

        foreach (var package in Directory.EnumerateFiles(inbox, "*.natter-plugin.zip"))
        {
            try
            {
                var result = await store.InstallAsync(package, allowUpdate: true).ConfigureAwait(false);
                log($"installed {result.Record.Id} {result.Record.ActiveVersion} ({result.Record.Trust})");
                File.Move(package, Path.Combine(processed, Path.GetFileName(package)), overwrite: true);
            }
            catch (Exception ex)
            {
                log($"install failed for {Path.GetFileName(package)}: {ex.Message}");
            }
        }
    }

    public static IReadOnlyList<string> LoadTrustedKeys(Context context)
    {
        // Production builds should embed publisher keys in the APK. For extended/sideload
        // builds the key list can be provisioned into app-private storage.
        var path = Path.Combine(context.FilesDir!.AbsolutePath, "plugin-trusted-keys.txt");
        if (!File.Exists(path))
            return [];

        return File.ReadAllLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();
    }
}
