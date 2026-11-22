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

            // Release builds only accept signed packages at install time, so activation enforces the
            // same rule; Debug keeps the development escape hatch.
            builder.ConfigurePluginTrust(trust =>
            {
#if DEBUG
                trust.RequireSignedPlugins = false;
#else
                trust.RequireSignedPlugins = true;
#endif
            });

            // S1-12: carry the installer's trust verdict and the startup integrity result with the
            // directory. A dynamic host cannot re-verify the signature itself, so anything that is
            // not a verified, integrity-clean signed package is explicitly registered as unverified
            // (and rejected when the trust policy requires signatures).
            var candidates = store.GetActivePluginCandidates();
            foreach (var candidate in candidates)
            {
                builder.AddPluginDirectory(new PluginTrustRegistration(
                    candidate.Directory,
                    Signature: candidate.Integrity switch
                    {
                        InstalledPluginIntegrity.Tampered => PluginSignatureState.Tampered,
                        InstalledPluginIntegrity.Verified when candidate.Signed => PluginSignatureState.Verified,
                        _ => PluginSignatureState.Unverified,
                    },
                    SignerKeyId: candidate.SignerKeyId,
                    PluginId: candidate.PluginId,
                    Detail: candidate.IntegrityDetail ?? candidate.IntegrityCode));

                if (!cliOptions.PluginDirectories.Contains(candidate.Directory, StringComparer.OrdinalIgnoreCase))
                    cliOptions.PluginDirectories.Add(candidate.Directory);
            }

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

    /// <summary>
    /// Publisher trust anchors are embedded in the APK assembly (<c>trusted-keys.txt</c>), never
    /// read from app-writable storage. A process that can write <c>FilesDir</c> must not be able to
    /// replace the root of trust; changing the anchors now requires rebuilding the APK.
    /// </summary>
    public static IReadOnlyList<string> LoadTrustedKeys(Context context)
    {
        _ = context; // retained for call-site compatibility

        using var stream = typeof(AndroidPluginHost).Assembly.GetManifestResourceStream(EmbeddedTrustedKeys);
        if (stream is null)
            return [];

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd()
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();
    }

    private const string EmbeddedTrustedKeys = "Natter.Hosting.Android.trusted-keys.txt";
}
