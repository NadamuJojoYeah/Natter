using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
using Natter.Infrastructure.Composition;
using Natter.Infrastructure.Plugins.Nps;
using Natter.Plugin.Abstractions;
using Natter.Plugin.Store;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// S1-12: the installer's trust verdict and the startup integrity check must survive into activation.
/// Matrix: signed/unsigned × requireSigned × tampered, plus the default-configuration regression that
/// keeps plain (unverifiable) plugin directories loading.
/// </summary>
public sealed class PluginTrustTests
{
    private const string EntryType = "Natter.Sample.Plugin.Greetings.GreetingsNpsPlugin";

    [Theory]
    // signed, requireSigned=false -> verified, loaded as signed
    [InlineData(true, false, null, true, "signed")]
    // signed, requireSigned=true -> still fine
    [InlineData(true, true, null, true, "signed")]
    // unsigned, requireSigned=false -> loaded as local, explicitly unverified
    [InlineData(false, false, null, true, "local(unverified)")]
    // unsigned, requireSigned=true -> rejected before loading
    [InlineData(false, true, null, false, "untrusted")]
    public async Task Signed_and_unsigned_packages_follow_the_require_signed_policy(
        bool sign,
        bool requireSigned,
        string? _,
        bool expectedLoaded,
        string expectedTrust)
    {
        using var temp = new TempDirectory();
        var package = PluginPackageTestBuilder.Create(
            temp.Combine("pkg-" + Guid.NewGuid().ToString("N")),
            sign: sign,
            omitSignature: !sign);
        var store = CreateStore(temp, trustedKey: sign ? package.PublicKey : null, allowUnsigned: !sign);

        var result = await store.InstallAsync(package.PackagePath);
        Assert.Equal(sign ? "signed" : "unsigned", result.Record.Trust);

        var candidate = Assert.Single(store.GetActivePluginCandidates());
        Assert.Equal(InstalledPluginIntegrity.Verified, candidate.Integrity);
        Assert.Equal(sign, candidate.Signed);

        await using var host = await StartHostAsync(candidate, requireSigned);
        var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);

        if (expectedLoaded)
        {
            Assert.Equal(PluginState.Activated, descriptor.State);
            Assert.Equal(expectedTrust, descriptor.Trust);
        }
        else
        {
            Assert.Equal(PluginState.Failed, descriptor.State);
            Assert.Equal("trust", descriptor.ErrorDetail!.Phase);
            Assert.Equal(PluginTrustCodes.UnsignedRejected, descriptor.ErrorDetail.Code);
            Assert.Contains("RequireSignedPlugins", descriptor.Error!, StringComparison.Ordinal);
            Assert.DoesNotContain(package.PackagePath, descriptor.Error!, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Tampered_package_is_rejected_with_an_integrity_code(bool sign)
    {
        using var temp = new TempDirectory();
        var package = PluginPackageTestBuilder.Create(
            temp.Combine("pkg-" + Guid.NewGuid().ToString("N")),
            sign: sign,
            omitSignature: !sign);
        var store = CreateStore(temp, trustedKey: sign ? package.PublicKey : null, allowUnsigned: !sign);
        await store.InstallAsync(package.PackagePath);

        var active = Assert.Single(store.GetActivePluginDirectories());
        var entry = Path.Combine(active, PluginPackageTestBuilder.DefaultEntryPath.Replace('/', Path.DirectorySeparatorChar));
        await File.AppendAllTextAsync(entry, "// injected");

        var candidate = Assert.Single(store.GetActivePluginCandidates());
        Assert.Equal(InstalledPluginIntegrity.Tampered, candidate.Integrity);
        Assert.Equal(InstalledPluginIntegrityCodes.Mismatch, candidate.IntegrityCode);
        Assert.Contains("changed:lib/", candidate.IntegrityDetail!, StringComparison.Ordinal);

        // Tampering wins over the trust switch: RequireSignedPlugins=false must not rescue it.
        await using var host = await StartHostAsync(candidate, requireSigned: false);
        var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);

        Assert.Equal(PluginState.Failed, descriptor.State);
        Assert.Equal(PluginTrustCodes.Tampered, descriptor.ErrorDetail!.Code);
        Assert.Equal("trust", descriptor.ErrorDetail.Phase);
        Assert.Contains("integrity", descriptor.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_hash_manifest_is_reported_as_unverified_not_trusted()
    {
        using var temp = new TempDirectory();
        var package = PluginPackageTestBuilder.Create(temp.Combine("pkg"), sign: true);
        var store = CreateStore(temp, trustedKey: package.PublicKey);
        await store.InstallAsync(package.PackagePath);

        var active = Assert.Single(store.GetActivePluginDirectories());
        File.Delete(Path.Combine(active, PluginPackageConstants.HashFile));

        var candidate = Assert.Single(store.GetActivePluginCandidates());
        Assert.Equal(InstalledPluginIntegrity.Unverified, candidate.Integrity);
        Assert.Equal(InstalledPluginIntegrityCodes.ManifestMissing, candidate.IntegrityCode);
        Assert.False(candidate.Signed);

        // Default policy: still loads, but the unverified state is recorded as a warning.
        await using (var host = await StartHostAsync(candidate, requireSigned: false))
        {
            var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);
            Assert.Equal(PluginState.Activated, descriptor.State);
            Assert.Equal("local(unverified)", descriptor.Trust);

            var diagnostics = host.Services.GetRequiredService<IPluginRuntimeDiagnostics>();
            var doctor = diagnostics.GetDoctor(candidate.PluginId);
            Assert.Contains(
                doctor.Warnings,
                warning => warning.Contains(PluginTrustCodes.IntegrityUnverified, StringComparison.Ordinal));
        }

        // RequireSignedPlugins=true rejects the same package.
        await using (var strict = await StartHostAsync(candidate, requireSigned: true))
        {
            var descriptor = Assert.Single(strict.Services.GetRequiredService<IPluginCatalog>().Plugins);
            Assert.Equal(PluginState.Failed, descriptor.State);
            Assert.Equal(PluginTrustCodes.UnsignedRejected, descriptor.ErrorDetail!.Code);
        }
    }

    [Fact]
    public async Task File_plugin_trust_level_signed_rejects_unsigned_packages()
    {
        using var temp = new TempDirectory();
        var package = PluginPackageTestBuilder.Create(temp.Combine("pkg"), sign: false, omitSignature: true);
        var store = CreateStore(temp, trustedKey: null, allowUnsigned: true);
        await store.InstallAsync(package.PackagePath);

        var candidate = Assert.Single(store.GetActivePluginCandidates());

        await using var host = await StartHostAsync(
            candidate,
            requireSigned: false,
            configurePlugins: options => options.FilePluginTrustLevel = PluginTrustLevel.Signed);

        var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);
        Assert.Equal(PluginState.Failed, descriptor.State);
        Assert.Equal(PluginTrustCodes.SignatureRequired, descriptor.ErrorDetail!.Code);
        Assert.Contains("FilePluginTrustLevel", descriptor.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Default_configuration_still_loads_plain_plugin_directories()
    {
        // Regression lock for requirement 5: a plain directory has no package.sha256, so it can only
        // be "unverified" — with default options it must still load exactly as before.
        var directory = CreatePlainPluginDirectory();
        try
        {
            var builder = NatterApplicationBuilder.Create()
                .AddNatterFramework()
                .AddPluginDirectory(directory);

            await using var host = builder.Build();
            await host.StartAsync();

            var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);
            Assert.Equal(PluginState.Activated, descriptor.State);
            Assert.Equal("local(unverified)", descriptor.Trust);
            Assert.Contains("cli.commands", descriptor.GrantedPermissions ?? [], StringComparer.Ordinal);

            var doctor = host.Services.GetRequiredService<IPluginRuntimeDiagnostics>().GetDoctor(descriptor.Id);
            Assert.Contains(
                doctor.Warnings,
                warning => warning.Contains(PluginTrustCodes.IntegrityUnverified, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Untrusted_baseline_loads_without_permissions()
    {
        var directory = CreatePlainPluginDirectory();
        try
        {
            var builder = NatterApplicationBuilder.Create()
                .AddNatterFramework()
                .AddPluginDirectory(directory)
                .ConfigurePlugins(options => options.FilePluginTrustLevel = PluginTrustLevel.Untrusted);

            await using var host = builder.Build();
            await host.StartAsync();

            var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);
            Assert.Equal("untrusted", descriptor.Trust);
            Assert.Empty(descriptor.GrantedPermissions ?? []);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Unverified_integrity_can_be_refused_outright()
    {
        var directory = CreatePlainPluginDirectory();
        try
        {
            var builder = NatterApplicationBuilder.Create()
                .AddNatterFramework()
                .AddPluginDirectory(directory)
                .ConfigurePluginTrust(trust => trust.AllowUnverifiedIntegrity = false);

            await using var host = builder.Build();
            await host.StartAsync();

            var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);
            Assert.Equal(PluginState.Failed, descriptor.State);
            Assert.Equal(PluginTrustCodes.IntegrityUnverified, descriptor.ErrorDetail!.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Policy_matrix_is_pure_and_testable_without_a_host()
    {
        var options = new NpsPluginOptions();
        var trust = new PluginTrustOptions();

        // Verified signature -> Signed, independent of baseline.
        var signed = PluginTrustPolicy.Evaluate(
            options, trust, new PluginTrustRegistration("/pkg", PluginSignatureState.Verified, "abc123"));
        Assert.True(signed.Allowed);
        Assert.Equal(PluginTrustLevel.Signed, signed.Trust);
        Assert.True(signed.SignatureVerified);

        // Unverified + requireSigned -> rejected.
        var rejected = PluginTrustPolicy.Evaluate(
            options,
            new PluginTrustOptions { RequireSignedPlugins = true },
            new PluginTrustRegistration("/pkg"));
        Assert.False(rejected.Allowed);
        Assert.Equal(PluginTrustCodes.UnsignedRejected, rejected.Code);

        // Tampered always rejected, with the integrity code.
        var tampered = PluginTrustPolicy.Evaluate(
            options, trust, new PluginTrustRegistration("/pkg", PluginSignatureState.Tampered, null, null, "changed:lib/x.dll"));
        Assert.False(tampered.Allowed);
        Assert.Equal(PluginTrustCodes.Tampered, tampered.Code);
        Assert.Contains("changed:lib/x.dll", tampered.Message, StringComparison.Ordinal);

        // Unverified + default -> baseline with an explicit unverified warning (never silent).
        var local = PluginTrustPolicy.Evaluate(options, trust, new PluginTrustRegistration("/pkg"));
        Assert.True(local.Allowed);
        Assert.Equal(PluginTrustLevel.Local, local.Trust);
        Assert.False(local.SignatureVerified);
        Assert.Contains(local.Warnings, warning => warning.Contains(PluginTrustCodes.IntegrityUnverified, StringComparison.Ordinal));
        Assert.Equal("local(unverified)", PluginTrustPolicy.TrustLabel(local));
    }

    private static PluginStore CreateStore(TempDirectory temp, string? trustedKey, bool allowUnsigned = false)
        => new(new PluginStoreOptions
        {
            RootDirectory = temp.Combine("plugins"),
            Platform = "android",
            TrustedPublicKeys = trustedKey is null ? [] : [trustedKey],
            AllowUnsigned = allowUnsigned,
        });

    private static async Task<IAsyncDisposableHost> StartHostAsync(
        InstalledPluginCandidate candidate,
        bool requireSigned,
        Action<NpsPluginOptions>? configurePlugins = null)
    {
        var registration = new PluginTrustRegistration(
            candidate.Directory,
            candidate.Integrity switch
            {
                InstalledPluginIntegrity.Tampered => PluginSignatureState.Tampered,
                InstalledPluginIntegrity.Verified when candidate.Signed => PluginSignatureState.Verified,
                _ => PluginSignatureState.Unverified,
            },
            candidate.SignerKeyId,
            candidate.PluginId,
            candidate.IntegrityDetail ?? candidate.IntegrityCode);

        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddPluginDirectory(registration)
            .ConfigurePluginTrust(trust => trust.RequireSignedPlugins = requireSigned);

        if (configurePlugins is not null)
            builder.ConfigurePlugins(configurePlugins);

        var host = builder.Build();
        await host.StartAsync();
        return new IAsyncDisposableHost(host);
    }

    private static string CreatePlainPluginDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "natter-trust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var assembly = Path.Combine(AppContext.BaseDirectory, "plugins", "greetings", "Natter.Sample.Plugin.Greetings.dll");
        File.Copy(assembly, Path.Combine(directory, Path.GetFileName(assembly)));

        File.WriteAllText(Path.Combine(directory, "plugin.json"), $$"""
            {
              "schemaVersion": 1,
              "id": "natter.trust-test.greetings",
              "name": "Trust Test Greetings",
              "version": "0.1.0",
              "kind": "feature",
              "hostApi": ">=0.1.0 <1.0.0",
              "entry": { "assembly": "{{Path.GetFileName(assembly)}}", "type": "{{EntryType}}" },
              "permissions": ["cli.commands"],
              "contributions": [
                { "type": "port.adapter", "id": "greeting" },
                { "type": "cli.command", "id": "greet" },
                { "type": "cli.command", "id": "greetings.recent" }
              ]
            }
            """);

        return directory;
    }

    /// <summary>Small wrapper so tests can `await using` a host returned from a helper.</summary>
    private sealed class IAsyncDisposableHost(Natter.Infrastructure.Composition.NatterHost host) : IAsyncDisposable
    {
        public IServiceProvider Services => host.Services;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await host.StopAsync();
            }
            catch (Exception)
            {
            }

            await host.DisposeAsync();
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "natter-trust-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
