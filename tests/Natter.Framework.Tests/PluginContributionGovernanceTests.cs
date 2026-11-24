using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Infrastructure.Composition;
using Natter.Infrastructure.Plugins.Nps;
using Natter.Plugin.Abstractions;
using Natter.Plugin.Store;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// S2-C2/C9/C14: prompt/context contributions require chat.read, installed packages are
/// integrity-checked again at activation, and Android trust anchors are compiled into the APK.
/// </summary>
public sealed class PluginContributionGovernanceTests
{
    // ---------------------------------------------------------------- S2-C2

    [Fact]
    public async Task Context_assembler_requires_chat_read()
    {
        await using var denied = await StartEmbeddedAsync<TestAssemblerPlugin>(
            "natter.test.governance.assembler.denied",
            PluginContributionTypes.ContextAssembler,
            "assembler",
            permissions: []);
        AssertPermissionDenied(denied, PluginContributionTypes.ContextAssembler, "assembler");

        await using var allowed = await StartEmbeddedAsync<TestAssemblerPlugin>(
            "natter.test.governance.assembler.allowed",
            PluginContributionTypes.ContextAssembler,
            "assembler",
            permissions: [PluginPermissions.ChatRead]);
        Assert.Equal(PluginState.Activated, allowed.Descriptor.State);
        Assert.True(allowed.Catalog.TryGetAssembler("natter.test.governance.assembler.allowed#assembler", out _));
    }

    [Fact]
    public async Task Prompt_provider_requires_chat_read()
    {
        await using var denied = await StartEmbeddedAsync<TestPromptPlugin>(
            "natter.test.governance.prompt.denied",
            PluginContributionTypes.PromptProvider,
            "prompt",
            permissions: []);
        AssertPermissionDenied(denied, PluginContributionTypes.PromptProvider, "prompt");

        await using var allowed = await StartEmbeddedAsync<TestPromptPlugin>(
            "natter.test.governance.prompt.allowed",
            PluginContributionTypes.PromptProvider,
            "prompt",
            permissions: [PluginPermissions.ChatRead]);
        Assert.Equal(PluginState.Activated, allowed.Descriptor.State);
        Assert.True(allowed.Catalog.TryGetPrompt("natter.test.governance.prompt.allowed#prompt", out _));
    }

    [Fact]
    public async Task Prompt_fragment_requires_chat_read()
    {
        await using var denied = await StartEmbeddedAsync<TestFragmentPlugin>(
            "natter.test.governance.fragment.denied",
            PluginContributionTypes.PromptFragment,
            "fragment",
            permissions: []);
        AssertPermissionDenied(denied, PluginContributionTypes.PromptFragment, "fragment");

        await using var allowed = await StartEmbeddedAsync<TestFragmentPlugin>(
            "natter.test.governance.fragment.allowed",
            PluginContributionTypes.PromptFragment,
            "fragment",
            permissions: [PluginPermissions.ChatRead]);
        Assert.Equal(PluginState.Activated, allowed.Descriptor.State);
        Assert.Contains(
            allowed.Catalog.FragmentProviders,
            item => item.Reference == "natter.test.governance.fragment.allowed#fragment");
    }

    [Fact]
    public void Required_permission_table_covers_prompt_and_context_contributions()
    {
        Assert.Equal(new[] { PluginPermissions.ChatRead }, NpsPermissionPolicy.RequiredPermissions(PluginContributionTypes.ContextAssembler));
        Assert.Equal(new[] { PluginPermissions.ChatRead }, NpsPermissionPolicy.RequiredPermissions(PluginContributionTypes.PromptProvider));
        Assert.Equal(new[] { PluginPermissions.ChatRead }, NpsPermissionPolicy.RequiredPermissions(PluginContributionTypes.PromptFragment));
    }

    [Fact]
    public void Existing_six_plugins_declare_all_contribution_permissions_under_default_policy()
    {
        var root = FindRepositoryRoot();
        var manifests = new[]
        {
            Path.Combine(root, "src", "Natter.Feature.Ai.DeepSeek", "plugin.json"),
            Path.Combine(root, "src", "Natter.Feature.Ai.Spark", "plugin.json"),
            Path.Combine(root, "samples", "Natter.Sample.Plugin.Discord", "plugin.json"),
            Path.Combine(root, "samples", "Natter.Sample.Plugin.DingTalk", "plugin.json"),
            Path.Combine(root, "plugins", "Natter.Plugin.WebConsole", "plugin.json"),
            Path.Combine(root, "plugins", "Natter.Plugin.PromptLibrary", "plugin.json"),
        };

        foreach (var path in manifests)
        {
            var read = PluginManifestReader.Read(Path.GetDirectoryName(path)!);
            Assert.True(read.IsValid, $"{path}: {string.Join("; ", read.Errors.Select(error => error.Message))}");
            var manifest = read.Manifest!;

            var required = (manifest.Contributions ?? [])
                .SelectMany(contribution => NpsPermissionPolicy.RequiredPermissions(contribution.Type))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            foreach (var permission in required)
            {
                Assert.Contains(permission, manifest.Permissions ?? []);
            }

            // Default policy (FilePluginTrustLevel=Local, AutoGrantLocalPermissions=true) must
            // grant everything these manifests need; otherwise activation would fail.
            var decision = NpsPermissionPolicy.Decide(
                manifest,
                PluginTrustLevel.Local,
                new NpsPluginOptions());
            Assert.All(required, permission => Assert.Contains(permission, decision.Granted));
        }

        var promptLibrary = PluginManifestReader.Read(
            Path.Combine(root, "plugins", "Natter.Plugin.PromptLibrary")).Manifest!;
        Assert.Contains(PluginPermissions.ChatRead, promptLibrary.Permissions!);
    }

    // ---------------------------------------------------------------- S2-C9

    [Fact]
    public async Task Installed_package_tamper_is_rejected_at_startup()
    {
        using var temp = new TempDirectory();
        var package = PluginPackageTestBuilder.Create(temp.Combine("pkg"), sign: true);
        var store = new PluginStore(new PluginStoreOptions
        {
            RootDirectory = temp.Combine("plugins"),
            Platform = "android",
            TrustedPublicKeys = [package.PublicKey],
            AllowUnsigned = false,
        });
        await store.InstallAsync(package.PackagePath);

        var active = Assert.Single(store.GetActivePluginDirectories());
        var entry = Path.Combine(active, PluginPackageTestBuilder.DefaultEntryPath.Replace('/', Path.DirectorySeparatorChar));
        await File.AppendAllTextAsync(entry, "// tampered");

        var candidate = Assert.Single(store.GetActivePluginCandidates());
        Assert.Equal(InstalledPluginIntegrity.Tampered, candidate.Integrity);

        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddPluginDirectory(new PluginTrustRegistration(
                candidate.Directory,
                PluginSignatureState.Tampered,
                candidate.SignerKeyId,
                candidate.PluginId,
                candidate.IntegrityDetail ?? candidate.IntegrityCode))
            .ConfigurePluginTrust(trust => trust.RequireSignedPlugins = false);

        await using var host = builder.Build();
        await host.StartAsync();

        var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);
        Assert.Equal(PluginState.Failed, descriptor.State);
        Assert.Equal("trust", descriptor.ErrorDetail!.Phase);
        Assert.Equal(PluginTrustCodes.Tampered, descriptor.ErrorDetail.Code);
        Assert.Contains("integrity", descriptor.Error!, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- S2-C14

    [Fact]
    public void Android_trust_anchors_are_embedded_and_not_read_from_app_writable_storage()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "Natter.Hosting.Android", "AndroidPluginHost.cs"));
        var project = File.ReadAllText(Path.Combine(root, "src", "Natter.Hosting.Android", "Natter.Hosting.Android.csproj"));

        Assert.Contains("Natter.Hosting.Android.trusted-keys.txt", source, StringComparison.Ordinal);
        Assert.Contains("GetManifestResourceStream", source, StringComparison.Ordinal);
        Assert.DoesNotContain("plugin-trusted-keys.txt", source, StringComparison.Ordinal);
        Assert.Contains("trusted-keys.txt", project, StringComparison.Ordinal);
        Assert.Contains("EmbeddedResource", project, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<GovernanceHost> StartEmbeddedAsync<TPlugin>(
        string pluginId,
        string contributionType,
        string contributionId,
        string[] permissions)
        where TPlugin : class, INatterPlugin, new()
    {
        var manifest = new PluginManifest
        {
            SchemaVersion = 1,
            Id = pluginId,
            Name = pluginId,
            Version = "0.1.0",
            Kind = PluginKind.Feature,
            HostApi = ">=0.1.0 <1.0.0",
            Entry = new PluginEntry
            {
                Assembly = "Natter.Framework.Tests.dll",
                Type = typeof(TPlugin).AssemblyQualifiedName!,
            },
            Permissions = permissions.ToList(),
            Contributions =
            [
                new PluginContribution { Type = contributionType, Id = contributionId },
            ],
        };

        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddEmbeddedNpsPlugin(manifest, new TPlugin());

        var host = builder.Build();
        await host.StartAsync();
        var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);
        var catalog = host.Services.GetRequiredService<IContextComponentCatalog>();
        return new GovernanceHost(host, descriptor, catalog);
    }

    private static void AssertPermissionDenied(
        GovernanceHost host,
        string contributionType,
        string contributionId)
    {
        Assert.Equal(PluginState.Failed, host.Descriptor.State);
        Assert.Equal("permission", host.Descriptor.ErrorDetail!.Phase);
        Assert.Equal("permission-denied", host.Descriptor.ErrorDetail.Code);
        Assert.Contains(PluginPermissions.ChatRead, host.Descriptor.Error, StringComparison.Ordinal);
        Assert.Contains($"{contributionType}/{contributionId}", host.Descriptor.Error, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Natter.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    /// <summary>Keeps the host alive until the test has inspected catalog/descriptor.</summary>
    private sealed class GovernanceHost(
        Natter.Infrastructure.Composition.NatterHost host,
        PluginDescriptor descriptor,
        IContextComponentCatalog catalog) : IAsyncDisposable
    {
        public PluginDescriptor Descriptor { get; } = descriptor;

        public IContextComponentCatalog Catalog { get; } = catalog;

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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "natter-governance-" + Guid.NewGuid().ToString("N"));
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
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public sealed class TestAssemblerPlugin : INatterPlugin
    {
        public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            context.Contributions.AddContextAssembler<NoopAssembler>("assembler");
            return ValueTask.CompletedTask;
        }

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    public sealed class TestPromptPlugin : INatterPlugin
    {
        public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            context.Contributions.AddPromptProvider<TestPromptProvider>("prompt");
            return ValueTask.CompletedTask;
        }

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    public sealed class TestFragmentPlugin : INatterPlugin
    {
        public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            context.Contributions.AddPromptFragmentProvider<TestFragmentProvider>("fragment");
            return ValueTask.CompletedTask;
        }

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    public sealed class NoopAssembler : IContextAssembler
    {
        public ValueTask<ContextBundle> AssembleAsync(ContextRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ContextBundle(
                [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, "noop")],
                [],
                0,
                "noop",
                new ContextDiagnostics(0, 0, false, 0)));
    }

    public sealed class TestPromptProvider : IPromptProvider
    {
        public ValueTask<PromptBundle> GetAsync(PromptRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new PromptBundle("noop", null, "noop"));
    }

    public sealed class TestFragmentProvider : IPromptFragmentProvider
    {
        public ValueTask<IReadOnlyList<PromptFragment>> GetFragmentsAsync(
            PromptFragmentContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<PromptFragment>>([]);
    }
}
