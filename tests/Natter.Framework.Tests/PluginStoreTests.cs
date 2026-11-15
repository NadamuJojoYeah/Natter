using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
using Natter.Abstractions.Ports;
using Natter.Cli.Abstractions;
using Natter.Cli.Core;
using Natter.Infrastructure.Composition;
using Natter.Plugin.Abstractions;
using Natter.Plugin.Store;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class PluginStoreTests
{
    [Fact]
    public async Task Valid_signed_package_installs_and_exposes_active_directory()
    {
        using var temp = new TempDirectory();
        var package = PluginPackageTestBuilder.Create(temp.Combine("packages"));
        var store = CreateStore(temp, package.PublicKey);

        var result = await store.InstallAsync(package.PackagePath);

        Assert.False(result.IsUpdate);
        Assert.Equal("signed", result.Record.Trust);
        Assert.NotNull(result.Record.SignerKeyId);
        Assert.True(Directory.Exists(Path.Combine(store.RootDirectory, package.PluginId, package.Version)));

        var active = Assert.Single(store.GetActivePluginDirectories());
        Assert.EndsWith(Path.Combine(package.PluginId, package.Version), active, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(active, "plugin.json")));
        Assert.True(File.Exists(Path.Combine(active, PluginPackageTestBuilder.DefaultEntryPath)));

        Assert.True(File.Exists(store.StatePath));
        Assert.True(File.Exists(store.PendingPath));
    }

    [Fact]
    public async Task Unsigned_packages_are_rejected_unless_development_mode_is_enabled()
    {
        using var temp = new TempDirectory();
        var package = PluginPackageTestBuilder.Create(temp.Combine("unsigned"), sign: false, omitSignature: true);

        var strict = CreateStore(temp, trustedKey: null);
        var exception = await Assert.ThrowsAsync<PluginStoreException>(() => strict.InstallAsync(package.PackagePath));
        Assert.Contains(exception.Issues, issue => issue.Code == "signature-missing");

        var development = CreateStore(temp, trustedKey: null, allowUnsigned: true);
        var result = await development.InstallAsync(package.PackagePath);
        Assert.Equal("unsigned", result.Record.Trust);
    }

    [Fact]
    public async Task Tampered_hash_or_invalid_signature_is_rejected()
    {
        using var temp = new TempDirectory();
        var signer = PluginPackageTestBuilder.CreateSigner();
        var valid = PluginPackageTestBuilder.Create(temp.Combine("valid"), signer: signer);

        var tampered = PluginPackageTestBuilder.Create(
            temp.Combine("tampered"),
            signer: signer,
            tamperHash: true);

        var store = CreateStore(temp, valid.PublicKey);
        var hashException = await Assert.ThrowsAsync<PluginStoreException>(() => store.InstallAsync(tampered.PackagePath));
        Assert.Contains(hashException.Issues, issue => issue.Code == "hash-mismatch");

        var wrongSigner = PluginPackageTestBuilder.CreateSigner();
        var badSignature = PluginPackageTestBuilder.Create(
            temp.Combine("bad-signature"),
            signer: wrongSigner);

        var signatureException = await Assert.ThrowsAsync<PluginStoreException>(() => store.InstallAsync(badSignature.PackagePath));
        Assert.Contains(signatureException.Issues, issue => issue.Code == "signature-invalid");
    }

    [Fact]
    public async Task Zip_slip_symlink_and_duplicate_entries_are_rejected()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp, PluginPackageTestBuilder.Create(temp.Combine("key")).PublicKey);

        var slip = PluginPackageTestBuilder.Create(
            temp.Combine("slip"),
            customizeArchive: archive => PluginPackageTestBuilder.AddRawEntry(archive, "../escape.txt", [1, 2, 3]));

        var slipException = await Assert.ThrowsAsync<PluginStoreException>(() => store.InstallAsync(slip.PackagePath));
        Assert.Contains(slipException.Issues, issue => issue.Code == "unsafe-path");

        var symlink = PluginPackageTestBuilder.Create(
            temp.Combine("symlink"),
            customizeArchive: archive => PluginPackageTestBuilder.AddRawEntry(archive, "link", [1], 0xA1FF << 16));

        var symlinkException = await Assert.ThrowsAsync<PluginStoreException>(() => store.InstallAsync(symlink.PackagePath));
        Assert.Contains(symlinkException.Issues, issue => issue.Code == "symlink");
    }

    [Fact]
    public async Task Native_and_non_managed_binaries_are_rejected()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp, PluginPackageTestBuilder.Create(temp.Combine("key")).PublicKey);

        var native = PluginPackageTestBuilder.Create(
            temp.Combine("native"),
            additionalFiles: new Dictionary<string, byte[]> { ["lib/native.so"] = [1, 2, 3] });

        var nativeException = await Assert.ThrowsAsync<PluginStoreException>(() => store.InstallAsync(native.PackagePath));
        Assert.Contains(nativeException.Issues, issue => issue.Code == "native-library");

        var fakeDll = Encoding.UTF8.GetBytes("this is not a managed assembly");
        var manifest = PluginPackageTestBuilder.BuildManifest(entryPath: "lib/Bad.dll");
        var nonManaged = PluginPackageTestBuilder.Create(
            temp.Combine("non-managed"),
            manifestJson: manifest,
            entryBytes: fakeDll,
            entryPath: "lib/Bad.dll");

        var managedException = await Assert.ThrowsAsync<PluginStoreException>(() => store.InstallAsync(nonManaged.PackagePath));
        Assert.Contains(managedException.Issues, issue => issue.Code == "native-library");
    }

    [Fact]
    public async Task Host_api_platform_dependency_and_config_schema_errors_are_rejected()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp, PluginPackageTestBuilder.Create(temp.Combine("key")).PublicKey);

        var hostApi = PluginPackageTestBuilder.Create(
            temp.Combine("host-api"),
            manifestJson: PluginPackageTestBuilder.BuildManifest(hostApi: ">=9.0.0 <10.0.0"));

        var hostException = await Assert.ThrowsAsync<PluginStoreException>(() => store.InstallAsync(hostApi.PackagePath));
        Assert.Contains(hostException.Issues, issue => issue.Code == "host-api");

        var platform = PluginPackageTestBuilder.Create(
            temp.Combine("platform"),
            manifestJson: PluginPackageTestBuilder.BuildManifest(platforms: ["linux"]));

        var platformException = await Assert.ThrowsAsync<PluginStoreException>(() => store.InstallAsync(platform.PackagePath));
        Assert.Contains(platformException.Issues, issue => issue.Code == "platform");

        var dependency = PluginPackageTestBuilder.Create(
            temp.Combine("dependency"),
            manifestJson: PluginPackageTestBuilder.BuildManifest(dependsOnId: "natter.missing"));

        var dependencyException = await Assert.ThrowsAsync<PluginStoreException>(() => store.InstallAsync(dependency.PackagePath));
        Assert.Contains(dependencyException.Issues, issue => issue.Code == "dependency-missing");

        var schema = PluginPackageTestBuilder.Create(
            temp.Combine("schema"),
            manifestJson: PluginPackageTestBuilder.BuildManifest(configSchema: "config.schema.json"),
            additionalFiles: new Dictionary<string, byte[]>
            {
                ["config.schema.json"] = Encoding.UTF8.GetBytes("{ invalid json"),
            });

        var schemaException = await Assert.ThrowsAsync<PluginStoreException>(() => store.InstallAsync(schema.PackagePath));
        Assert.Contains(schemaException.Issues, issue => issue.Code == "config-schema-invalid");
    }

    [Fact]
    public async Task Update_retains_previous_version_and_rollback_restores_it()
    {
        using var temp = new TempDirectory();
        var signer = PluginPackageTestBuilder.CreateSigner();
        var store = CreateStore(temp, Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()));

        var first = PluginPackageTestBuilder.Create(
            temp.Combine("v1"),
            version: "0.1.0",
            manifestJson: PluginPackageTestBuilder.BuildManifest(version: "0.1.0"),
            signer: signer);

        await store.InstallAsync(first.PackagePath);

        var second = PluginPackageTestBuilder.Create(
            temp.Combine("v2"),
            version: "0.2.0",
            manifestJson: PluginPackageTestBuilder.BuildManifest(version: "0.2.0"),
            signer: signer);

        var update = await store.InstallAsync(second.PackagePath);
        Assert.True(update.IsUpdate);
        Assert.Equal("0.2.0", update.Record.ActiveVersion);
        Assert.Equal("0.1.0", update.Record.PreviousVersion);
        Assert.True(Directory.Exists(Path.Combine(store.RootDirectory, first.PluginId, "0.1.0")));
        Assert.True(Directory.Exists(Path.Combine(store.RootDirectory, first.PluginId, "0.2.0")));

        var rollback = await store.RollbackAsync(first.PluginId);
        Assert.True(rollback.RolledBack);
        Assert.Equal("0.1.0", rollback.ActiveVersion);
        Assert.Equal("0.1.0", Assert.Single(store.GetInstalledPlugins()).ActiveVersion);
    }

    [Fact]
    public async Task Enable_disable_and_uninstall_update_active_directories()
    {
        using var temp = new TempDirectory();
        var package = PluginPackageTestBuilder.Create(temp.Combine("packages"));
        var store = CreateStore(temp, package.PublicKey);
        await store.InstallAsync(package.PackagePath);

        await store.SetEnabledAsync(package.PluginId, false);
        Assert.Empty(store.GetActivePluginDirectories());

        await store.SetEnabledAsync(package.PluginId, true);
        Assert.Single(store.GetActivePluginDirectories());

        await store.UninstallAsync(package.PluginId, deleteConfig: true, deleteData: true);
        Assert.Empty(store.GetInstalledPlugins());
        Assert.False(Directory.Exists(Path.Combine(store.RootDirectory, package.PluginId)));
    }

    [Fact]
    public async Task Reconcile_confirms_an_activated_pending_version()
    {
        using var temp = new TempDirectory();
        var signer = PluginPackageTestBuilder.CreateSigner();
        var store = CreateStore(temp, Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()));
        var package = PluginPackageTestBuilder.Create(
            temp.Combine("v1"),
            version: "0.1.0",
            manifestJson: PluginPackageTestBuilder.BuildManifest(version: "0.1.0"),
            signer: signer);

        await store.InstallAsync(package.PackagePath);

        var result = await store.ReconcileAsync(new DiagnosticsStub(PluginState.Activated));

        Assert.Contains(package.PluginId, result.Confirmed);
        Assert.Empty(result.RolledBack);
        Assert.Equal(PluginState.Activated, Assert.Single(store.GetInstalledPlugins()).LastKnownState);
    }

    [Fact]
    public async Task Reconcile_rolls_back_a_failed_pending_version_to_previous()
    {
        using var temp = new TempDirectory();
        var signer = PluginPackageTestBuilder.CreateSigner();
        var store = CreateStore(temp, Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()));
        var v1 = PluginPackageTestBuilder.Create(
            temp.Combine("v1"),
            version: "0.1.0",
            manifestJson: PluginPackageTestBuilder.BuildManifest(version: "0.1.0"),
            signer: signer);
        var v2 = PluginPackageTestBuilder.Create(
            temp.Combine("v2"),
            version: "0.2.0",
            manifestJson: PluginPackageTestBuilder.BuildManifest(version: "0.2.0"),
            signer: signer);

        await store.InstallAsync(v1.PackagePath);
        await store.InstallAsync(v2.PackagePath);

        var result = await store.ReconcileAsync(new DiagnosticsStub(PluginState.Failed));

        Assert.Contains(v1.PluginId, result.RolledBack);
        Assert.Equal("0.1.0", Assert.Single(store.GetInstalledPlugins()).ActiveVersion);
    }

    [Fact]
    public async Task Installed_plugin_can_be_loaded_by_the_nps_runtime()
    {
        using var temp = new TempDirectory();
        var package = PluginPackageTestBuilder.Create(temp.Combine("packages"));
        var store = CreateStore(temp, package.PublicKey);
        var installed = await store.InstallAsync(package.PackagePath);
        var active = Assert.Single(store.GetActivePluginDirectories());

        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddModule<CliCoreCompositionModule>()
            .AddPluginDirectory(active);

        await using var host = builder.Build();
        await host.StartAsync();

        var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);
        Assert.Equal(installed.Record.Id, descriptor.Id);
        Assert.Equal(PluginState.Activated, descriptor.State);

        var registry = host.Services.GetRequiredService<CliCommandRegistry>();
        Assert.True(registry.TryGet("greet", out _));

        var greeting = await host.Services.GetRequiredService<IGreetingPort>().GreetAsync("store");
        Assert.Contains("greetings plugin adapter", greeting);
    }

    [Fact]
    public async Task Package_writer_produces_a_package_the_store_accepts()
    {
        using var temp = new TempDirectory();
        var source = temp.Combine("source");
        Directory.CreateDirectory(Path.Combine(source, "lib"));

        var manifest = PluginPackageTestBuilder.BuildManifest();
        File.WriteAllText(Path.Combine(source, "plugin.json"), manifest);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "plugins", "greetings", "Natter.Sample.Plugin.Greetings.dll"),
            Path.Combine(source, PluginPackageTestBuilder.DefaultEntryPath));

        using var key = PluginPackageTestBuilder.CreateSigner();
        var packagePath = temp.Combine("writer.natter-plugin.zip");
        var info = await PluginPackageWriter.CreateAsync(source, packagePath, key);

        Assert.True(File.Exists(packagePath));
        Assert.Equal("natter.sample.greetings", info.Manifest.Id);

        var store = CreateStore(temp, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        var installed = await store.InstallAsync(packagePath);
        Assert.Equal("natter.sample.greetings", installed.Record.Id);
        Assert.Equal("signed", installed.Record.Trust);
    }

    [Fact]
    public async Task Plugin_store_cli_installs_lists_and_disables_packages()
    {
        using var temp = new TempDirectory();
        var package = PluginPackageTestBuilder.Create(temp.Combine("packages"));
        var storeRoot = temp.Combine("store");
        var console = new StringConsole();

        var installExit = await NatterCli.RunAsync(
            ["plugins", "store", "install", package.PackagePath, "--root", storeRoot, "--trusted-key", package.PublicKey, "--json"],
            console,
            NoPlugins());

        Assert.Equal(CliExitCodes.Success, installExit);
        Assert.True(File.Exists(Path.Combine(storeRoot, "installed.json")));

        console = new StringConsole();
        var listExit = await NatterCli.RunAsync(
            ["plugins", "store", "list", "--root", storeRoot, "--json"],
            console,
            NoPlugins());

        Assert.Equal(CliExitCodes.Success, listExit);
        using (var document = JsonDocument.Parse(console.Output))
        {
            var record = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal(package.PluginId, record.GetProperty("id").GetString());
            Assert.True(record.GetProperty("enabled").GetBoolean());
        }

        console = new StringConsole();
        var disableExit = await NatterCli.RunAsync(
            ["plugins", "store", "disable", package.PluginId, "--root", storeRoot],
            console,
            NoPlugins());

        Assert.Equal(CliExitCodes.Success, disableExit);
        Assert.False(new PluginStore(new PluginStoreOptions { RootDirectory = storeRoot }).GetInstalledPlugins()[0].Enabled);
    }

    private static NatterCliOptions NoPlugins() => new() { LoadPlugins = false };

    private static PluginStore CreateStore(
        TempDirectory temp,
        string? trustedKey,
        bool allowUnsigned = false)
        => new(new PluginStoreOptions
        {
            RootDirectory = temp.Combine("plugins"),
            Platform = "android",
            TrustedPublicKeys = trustedKey is null ? [] : [trustedKey],
            AllowUnsigned = allowUnsigned,
        });

    private sealed class DiagnosticsStub(PluginState state) : IPluginRuntimeDiagnostics
    {
        public IReadOnlyList<PluginGraphNode> Graph { get; } =
            [new PluginGraphNode("natter.sample.greetings", "0.2.0", "feature", state, [], [], [])];

        public IReadOnlyList<PluginGraphEdge> GraphEdges => [];

        public IReadOnlyList<PluginContributionInfo> Contributions => [];

        public IReadOnlyList<PluginPermissionInfo> Permissions => [];

        public PluginDoctorReport GetDoctor(string pluginId) => throw new NotSupportedException();

        public PluginConfigValidationResult ValidateConfig(string pluginId) => throw new NotSupportedException();
    }

    private sealed class TempDirectory : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "natter-store-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(_root);

        public string Combine(params string[] parts)
            => Path.Combine([_root, .. parts]);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }
    }
}
