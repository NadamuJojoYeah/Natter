using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
using Natter.Application.Chat;
using Natter.Infrastructure.Composition;
using Natter.Infrastructure.Plugins.Nps;
using Natter.Plugin.Abstractions;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// P6.2 embedding/rerank ports: contribution registration/permissions, batching, truncation,
/// cache behavior and all-or-nothing provider failure semantics.
/// </summary>
public sealed class EmbeddingPortTests
{
    // ---------------------------------------------------------------- contribution lifecycle

    [Fact]
    public async Task Embedding_and_rerank_contributions_register_under_owner_and_stop_withdraws_them()
    {
        var manifest = Manifest(
            "natter.test.embedding.host",
            permissions: [PluginPermissions.Network, PluginPermissions.Secret("TEST_KEY")],
            contributions:
            [
                new PluginContribution { Type = PluginContributionTypes.EmbeddingProvider, Id = "embed" },
                new PluginContribution { Type = PluginContributionTypes.RerankProvider, Id = "rerank" },
            ]);

        await using var host = await StartAsync(manifest, new EmbeddingAndRerankPlugin());
        var embeddingCatalog = host.Services.GetRequiredService<IEmbeddingProviderCatalog>();
        var rerankCatalog = host.Services.GetRequiredService<IRerankProviderCatalog>();

        var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);
        Assert.True(embeddingCatalog.TryGet("natter.test.embedding.host#embed", out var embedding), descriptor.Error ?? descriptor.ErrorDetail?.Message);
        Assert.Equal("fake-embedding", embedding!.Id);
        Assert.True(rerankCatalog.TryGet("natter.test.embedding.host#rerank", out var rerank));
        Assert.Equal("fake-rerank", rerank!.Id);

        await host.StopAsync();

        Assert.False(embeddingCatalog.TryGet("natter.test.embedding.host#embed", out _));
        Assert.False(rerankCatalog.TryGet("natter.test.embedding.host#rerank", out _));
    }

    [Fact]
    public async Task Embedding_provider_requires_network_and_a_secret_permission()
    {
        var noPermissions = Manifest(
            "natter.test.embedding.denied",
            permissions: [],
            contributions: [new PluginContribution { Type = PluginContributionTypes.EmbeddingProvider, Id = "embed" }]);
        await using (var denied = await StartAsync(noPermissions, new EmbeddingOnlyPlugin()))
        {
            AssertFailedWithPermission(denied, "embedding.provider/embed");
        }

        var networkOnly = Manifest(
            "natter.test.embedding.network-only",
            permissions: [PluginPermissions.Network],
            contributions: [new PluginContribution { Type = PluginContributionTypes.EmbeddingProvider, Id = "embed" }]);
        await using (var denied = await StartAsync(networkOnly, new EmbeddingOnlyPlugin()))
        {
            AssertFailedWithPermission(denied, "embedding.provider/embed", "secrets:");
        }
    }

    [Fact]
    public async Task Rerank_provider_also_enforces_permissions()
    {
        var manifest = Manifest(
            "natter.test.rerank.denied",
            permissions: [PluginPermissions.Network],
            contributions: [new PluginContribution { Type = PluginContributionTypes.RerankProvider, Id = "rerank" }]);

        await using var host = await StartAsync(manifest, new RerankOnlyPlugin());

        AssertFailedWithPermission(host, "rerank.provider/rerank", "secrets:");
    }

    [Fact]
    public async Task Undeclared_plugin_is_not_reported_as_missing_but_declared_unregistered_is()
    {
        var noContribution = Manifest(
            "natter.test.embedding.undeclared",
            permissions: [PluginPermissions.Network, PluginPermissions.Secret("TEST_KEY")],
            contributions: []);
        await using (var host = await StartAsync(noContribution, new NoopPlugin(), typeof(NoopPlugin)))
        {
            var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);
            Assert.Equal(PluginState.Activated, descriptor.State);
        }

        var declared = Manifest(
            "natter.test.embedding.missing",
            permissions: [PluginPermissions.Network, PluginPermissions.Secret("TEST_KEY")],
            contributions: [new PluginContribution { Type = PluginContributionTypes.EmbeddingProvider, Id = "embed" }]);
        await using (var host = await StartAsync(declared, new NoopPlugin(), typeof(NoopPlugin)))
        {
            var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);
            Assert.Equal(PluginState.Failed, descriptor.State);
            Assert.Equal("configure", descriptor.ErrorDetail!.Phase);
            Assert.Equal("missing-contribution", descriptor.ErrorDetail.Code);
            Assert.Contains("embedding.provider/embed", descriptor.Error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Activation_failure_rolls_back_the_provider_registration()
    {
        var manifest = Manifest(
            "natter.test.embedding.rollback",
            permissions: [PluginPermissions.Network, PluginPermissions.Secret("TEST_KEY")],
            contributions: [new PluginContribution { Type = PluginContributionTypes.EmbeddingProvider, Id = "embed" }],
            entryType: typeof(FailingEmbeddingPlugin));

        await using var host = await StartAsync(manifest, new FailingEmbeddingPlugin());
        var catalog = host.Services.GetRequiredService<IEmbeddingProviderCatalog>();
        var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);

        Assert.Equal(PluginState.Failed, descriptor.State);
        Assert.Equal("activate", descriptor.ErrorDetail!.Phase);
        Assert.False(catalog.TryGet("natter.test.embedding.rollback#embed", out _));
    }

    [Fact]
    public async Task Stopping_then_disposing_does_not_leave_provider_catalog_entries()
    {
        var manifest = Manifest(
            "natter.test.embedding.dispose",
            permissions: [PluginPermissions.Network, PluginPermissions.Secret("TEST_KEY")],
            contributions: [new PluginContribution { Type = PluginContributionTypes.EmbeddingProvider, Id = "embed" }]);
        var host = await StartAsync(manifest, new EmbeddingOnlyPlugin());
        var catalog = host.Services.GetRequiredService<IEmbeddingProviderCatalog>();
        Assert.True(catalog.TryGet("natter.test.embedding.dispose#embed", out _));

        await host.DisposeAsync();

        Assert.False(catalog.TryGet("natter.test.embedding.dispose#embed", out _));
    }

    // ---------------------------------------------------------------- batching / truncation / cache

    [Fact]
    public async Task One_hundred_inputs_are_split_into_four_ordered_batches()
    {
        var provider = new RecordingEmbeddingProvider(maxBatchSize: 25, maxInputTokens: 1000);
        var service = new EmbeddingService(provider);
        var inputs = Enumerable.Range(0, 100).Select(index => $"text-{index}").ToArray();

        var result = await service.EmbedAsync(new EmbeddingRequest(inputs, Model: "m", Dimensions: 4));

        Assert.Equal([25, 25, 25, 25], result.ProviderBatchSizes);
        Assert.Equal(4, provider.Calls);
        Assert.Equal(100, result.Result.Vectors.Count);
        for (var index = 0; index < inputs.Length; index++)
            Assert.Equal(index, (int)result.Result.Vectors[index].Span[0]);
    }

    [Fact]
    public async Task Truncation_keeps_order_and_records_the_affected_indices()
    {
        var provider = new RecordingEmbeddingProvider(maxBatchSize: 8, maxInputTokens: 8);
        var service = new EmbeddingService(provider);
        var longInput = new string('x', 2000);

        var result = await service.EmbedAsync(new EmbeddingRequest(
            ["short", longInput, "also-short"],
            Model: "m",
            Dimensions: 2));

        Assert.Equal([1], result.TruncatedInputIndexes);
        Assert.Equal(3, result.Result.Vectors.Count);
        Assert.Equal(1, provider.Calls);
        Assert.All(provider.Requests[0], text => Assert.True(text.Length < longInput.Length));
        Assert.Equal("short", provider.Requests[0][0]);
    }

    [Fact]
    public async Task Any_batch_failure_aborts_without_a_partial_result_or_cache_write()
    {
        var provider = new RecordingEmbeddingProvider(
            maxBatchSize: 2,
            maxInputTokens: 1000,
            failOnCall: 2);
        var cache = new InMemoryEmbeddingCache();
        var service = new EmbeddingService(provider, cache);

        var exception = await Assert.ThrowsAsync<EmbeddingProviderException>(async () =>
            await service.EmbedAsync(new EmbeddingRequest(
                ["a", "b", "c", "d"],
                Model: "m",
                Dimensions: 2)));

        Assert.Equal("embedding.test.failed", exception.Code);
        Assert.Equal(2, provider.Calls);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task Cache_hits_do_not_repeat_provider_calls_and_model_or_dimensions_do_not_collide()
    {
        var provider = new RecordingEmbeddingProvider(maxBatchSize: 8, maxInputTokens: 1000);
        var cache = new InMemoryEmbeddingCache();
        var service = new EmbeddingService(provider, cache);

        var request = new EmbeddingRequest(["a", "b"], Model: "m", Dimensions: 3);
        var first = await service.EmbedAsync(request);
        var second = await service.EmbedAsync(request);

        Assert.Equal(1, provider.Calls);
        Assert.Equal(first.Result.Vectors.Count, second.Result.Vectors.Count);
        Assert.Empty(second.ProviderBatchSizes);

        await service.EmbedAsync(new EmbeddingRequest(["a", "b"], Model: "other", Dimensions: 3));
        Assert.Equal(2, provider.Calls);

        await service.EmbedAsync(new EmbeddingRequest(["a", "b"], Model: "m", Dimensions: 4));
        Assert.Equal(3, provider.Calls);
    }

    [Fact]
    public async Task Provider_timeout_code_is_propagated_and_not_retried()
    {
        var provider = new RecordingEmbeddingProvider(
            maxBatchSize: 8,
            maxInputTokens: 1000,
            timeout: true);
        var service = new EmbeddingService(provider);

        var exception = await Assert.ThrowsAsync<EmbeddingProviderException>(async () =>
            await service.EmbedAsync(new EmbeddingRequest(["a"], Model: "m", Dimensions: 2)));

        Assert.Equal(EmbeddingErrorKind.Timeout, exception.Kind);
        Assert.Equal("embedding.test.timeout", exception.Code);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Rerank_rejects_more_candidates_than_the_provider_limit()
    {
        var provider = new FakeRerankProvider { MaxCandidates = 2 };
        var service = new RerankService();

        var exception = await Assert.ThrowsAsync<RerankProviderException>(async () =>
            await service.RerankAsync(
                provider,
                "q",
                [new RerankCandidate("a", "A"), new RerankCandidate("b", "B"), new RerankCandidate("c", "C")]));

        Assert.Equal("rerank.too_many_candidates", exception.Code);
        Assert.Equal(0, provider.Calls);
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<TestHost> StartAsync(PluginManifest manifest, INatterPlugin plugin, Type? entryType = null)
    {
        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddEmbeddedNpsPlugin(manifest, plugin);

        var host = builder.Build();
        await host.StartAsync();
        return new TestHost(host);
    }

    private static void AssertFailedWithPermission(TestHost host, string contribution, string? expectedPermission = null)
    {
        var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);
        Assert.Equal(PluginState.Failed, descriptor.State);
        Assert.Equal("permission", descriptor.ErrorDetail!.Phase);
        Assert.Equal("permission-denied", descriptor.ErrorDetail.Code);
        Assert.Contains(contribution, descriptor.Error, StringComparison.Ordinal);
        if (expectedPermission is not null)
            Assert.Contains(expectedPermission, descriptor.Error, StringComparison.Ordinal);
    }

    private static PluginManifest Manifest(
        string id,
        string[] permissions,
        IReadOnlyList<PluginContribution> contributions,
        Type? entryType = null)
        => new()
        {
            SchemaVersion = 1,
            Id = id,
            Name = id,
            Version = "0.1.0",
            Kind = PluginKind.Feature,
            HostApi = ">=0.1.0 <1.0.0",
            Entry = new PluginEntry
            {
                Assembly = "Natter.Framework.Tests.dll",
                Type = (entryType ?? typeof(EmbeddingAndRerankPlugin)).AssemblyQualifiedName!,
            },
            Permissions = permissions.ToList(),
            Secrets = permissions.Any(permission => permission.StartsWith("secrets:", StringComparison.Ordinal))
                ? [new PluginSecret { Name = "TEST_KEY", Required = false }]
                : null,
            Contributions = contributions.ToList(),
        };

    private sealed class TestHost(Natter.Infrastructure.Composition.NatterHost host) : IAsyncDisposable
    {
        public IServiceProvider Services => host.Services;

        public ValueTask StopAsync() => host.StopAsync();

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

    public sealed class EmbeddingAndRerankPlugin : INatterPlugin
    {
        public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            context.Services.Collection.AddSingleton<FakeEmbeddingProvider>();
            context.Services.Collection.AddSingleton<FakeRerankProvider>();
            context.Contributions.AddEmbeddingProvider<FakeEmbeddingProvider>("embed");
            context.Contributions.AddRerankProvider<FakeRerankProvider>("rerank");
            return ValueTask.CompletedTask;
        }

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    public sealed class EmbeddingOnlyPlugin : INatterPlugin
    {
        public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            context.Services.Collection.AddSingleton<FakeEmbeddingProvider>();
            context.Contributions.AddEmbeddingProvider<FakeEmbeddingProvider>("embed");
            return ValueTask.CompletedTask;
        }

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    public sealed class RerankOnlyPlugin : INatterPlugin
    {
        public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            context.Services.Collection.AddSingleton<FakeRerankProvider>();
            context.Contributions.AddRerankProvider<FakeRerankProvider>("rerank");
            return ValueTask.CompletedTask;
        }

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    public sealed class FailingEmbeddingPlugin : INatterPlugin
    {
        public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            context.Services.Collection.AddSingleton<FakeEmbeddingProvider>();
            context.Contributions.AddEmbeddingProvider<FakeEmbeddingProvider>("embed");
            return ValueTask.CompletedTask;
        }

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("activation failed for test");

        public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    public sealed class NoopPlugin : INatterPlugin
    {
        public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    public sealed class FakeEmbeddingProvider : IEmbeddingProvider
    {
        public string Id => "fake-embedding";

        public int MaxBatchSize => 8;

        public int MaxInputTokens => 1000;

        public ValueTask<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken ct = default)
            => ValueTask.FromResult(new EmbeddingResult(
                request.Inputs.Select(_ => new ReadOnlyMemory<float>([1f])).ToArray(),
                "fake",
                1,
                null));
    }

    public sealed class FakeRerankProvider : IRerankProvider
    {
        public string Id => "fake-rerank";

        public int MaxCandidates { get; set; } = 10;

        public int Calls { get; private set; }

        public ValueTask<RerankResult> RerankAsync(
            string query,
            IReadOnlyList<RerankCandidate> candidates,
            CancellationToken ct = default)
        {
            Calls++;
            return ValueTask.FromResult(new RerankResult(
                candidates.Select(candidate => (candidate.Id, 1d)).ToArray()));
        }
    }

    public sealed class RecordingEmbeddingProvider(
        int maxBatchSize,
        int maxInputTokens,
        int? failOnCall = null,
        bool timeout = false) : IEmbeddingProvider
    {
        public string Id => "recording";

        public int MaxBatchSize => maxBatchSize;

        public int MaxInputTokens => maxInputTokens;

        public int Calls { get; private set; }

        public List<IReadOnlyList<string>> Requests { get; } = [];

        public ValueTask<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken ct = default)
        {
            Calls++;
            Requests.Add(request.Inputs);

            if (timeout)
            {
                throw new EmbeddingProviderException(
                    EmbeddingErrorKind.Timeout,
                    "embedding.test.timeout",
                    "timed out");
            }

            if (failOnCall == Calls)
            {
                throw new EmbeddingProviderException(
                    EmbeddingErrorKind.Unavailable,
                    "embedding.test.failed",
                    "provider failed");
            }

            var dimensions = request.Dimensions ?? 2;
            var vectors = request.Inputs
                .Select((text, index) =>
                {
                    var inputIndex = text.StartsWith("text-", StringComparison.Ordinal)
                                     && int.TryParse(text.AsSpan(5), out var parsed)
                        ? parsed
                        : index;
                    var vector = new float[dimensions];
                    if (dimensions > 0)
                        vector[0] = inputIndex;
                    if (dimensions > 1)
                        vector[1] = Calls;
                    return new ReadOnlyMemory<float>(vector);
                })
                .ToArray();
            return ValueTask.FromResult(new EmbeddingResult(vectors, "recording", dimensions, 7));
        }
    }
}
