using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Natter.Application.Chat;
using Natter.Plugin.Abstractions;
using Xunit;

namespace Natter.Plugin.AiEmbedding.OpenAi.Tests;

public sealed class OpenAiEmbeddingProviderTests
{
    [Fact]
    public async Task Request_uses_openai_embeddings_shape_and_preserves_index_order()
    {
        using var handler = new RecordingHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://example.invalid/v1/embeddings", request.RequestUri!.ToString());
            Assert.Equal("Bearer test-key", request.Headers.Authorization!.ToString());

            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = body.RootElement;
            Assert.Equal("test-model", root.GetProperty("model").GetString());
            Assert.Equal(1536, root.GetProperty("dimensions").GetInt32());
            Assert.Equal(2, root.GetProperty("input").GetArrayLength());
            Assert.Equal("first", root.GetProperty("input")[0].GetString());
            Assert.Equal("second", root.GetProperty("input")[1].GetString());

            return JsonResponse("""
                {
                  "model": "test-model",
                  "data": [
                    { "index": 1, "embedding": [0.3, 0.4] },
                    { "index": 0, "embedding": [0.1, 0.2] }
                  ],
                  "usage": { "prompt_tokens": 5 }
                }
                """);
        });
        var provider = CreateProvider(handler);

        var result = await provider.EmbedAsync(new EmbeddingRequest(
            ["first", "second"],
            Model: "test-model",
            Dimensions: 1536));

        Assert.Equal(2, result.Vectors.Count);
        Assert.Equal([0.1f, 0.2f], result.Vectors[0].ToArray());
        Assert.Equal([0.3f, 0.4f], result.Vectors[1].ToArray());
        Assert.Equal("test-model", result.Model);
        Assert.Equal(2, result.Dimensions);
        Assert.Equal(5, result.PromptTokens);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Dimensions_are_omitted_when_not_configured()
    {
        using var handler = new RecordingHandler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.False(body.RootElement.TryGetProperty("dimensions", out _));
            return JsonResponse("""{"data":[{"index":0,"embedding":[1.0]}],"model":"m"}""");
        });
        var options = new OpenAiEmbeddingOptions
        {
            Endpoint = "https://example.invalid/v1",
            Model = "m",
            ApiKey = "${secret:OPENAI_COMPAT_API_KEY}",
            MaxBatchSize = 8,
            MaxInputTokens = 100,
            TimeoutSeconds = 5,
        };
        var provider = new OpenAiEmbeddingProvider(options, "test-key", new HttpClient(handler));

        var result = await provider.EmbedAsync(new EmbeddingRequest(["x"]));

        Assert.Equal(1, result.Dimensions);
        Assert.Null(result.PromptTokens);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, EmbeddingErrorKind.Authentication, "embedding.openai.authentication")]
    [InlineData(HttpStatusCode.TooManyRequests, EmbeddingErrorKind.RateLimited, "embedding.openai.rate_limited")]
    public async Task Http_errors_map_to_stable_codes_without_retry(
        HttpStatusCode status,
        EmbeddingErrorKind expectedKind,
        string expectedCode)
    {
        using var handler = new RecordingHandler(_ => Task.FromResult(new HttpResponseMessage(status)));
        var provider = CreateProvider(handler);

        var exception = await Assert.ThrowsAsync<EmbeddingProviderException>(async () =>
            await provider.EmbedAsync(new EmbeddingRequest(["x"])));

        Assert.Equal(expectedKind, exception.Kind);
        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Timeout_maps_to_a_stable_code_without_retry()
    {
        using var handler = new RecordingHandler(_ => throw new TaskCanceledException("timeout"));
        var provider = CreateProvider(handler);

        var exception = await Assert.ThrowsAsync<EmbeddingProviderException>(async () =>
            await provider.EmbedAsync(new EmbeddingRequest(["x"])));

        Assert.Equal(EmbeddingErrorKind.Timeout, exception.Kind);
        Assert.Equal("embedding.openai.timeout", exception.Code);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_a_provider_timeout()
    {
        using var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return JsonResponse("""{"data":[]}""");
        });
        var provider = CreateProvider(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await provider.EmbedAsync(new EmbeddingRequest(["x"]), cancellation.Token));
    }

    [Fact]
    public void Manifest_declares_embedding_contribution_and_network_secret_permissions()
    {
        var manifest = OpenAiEmbeddingPlugin.Manifest;

        Assert.Equal("natter.feature.ai.embedding-openai", manifest.Id);
        Assert.Contains(manifest.Permissions!, permission => permission == PluginPermissions.Network);
        Assert.Contains(manifest.Permissions!, permission => permission.StartsWith(PluginPermissions.SecretsPrefix, StringComparison.Ordinal));
        Assert.Contains(manifest.Contributions!, contribution =>
            contribution.Type == PluginContributionTypes.EmbeddingProvider
            && contribution.Id == OpenAiEmbeddingPlugin.ContributionId);
        Assert.EndsWith("Natter.Plugin.AiEmbedding.OpenAi.dll", manifest.Entry!.Assembly);
    }

    [Fact]
    public void Embedded_manifest_validates_against_the_current_host_contract()
    {
        var validation = PluginManifestReader.ValidateManifest(OpenAiEmbeddingPlugin.Manifest);

        Assert.Empty(validation.Errors);
    }

    [Fact]
    public void Config_sample_matches_the_embedded_schema()
    {
        using var schemaStream = typeof(OpenAiEmbeddingPlugin).Assembly.GetManifestResourceStream(
            "Natter.Plugin.AiEmbedding.OpenAi.config.schema.json");
        Assert.NotNull(schemaStream);
        using var schema = JsonDocument.Parse(schemaStream);
        using var config = JsonDocument.Parse("""
            {
              "endpoint": "https://api.openai.com/v1",
              "model": "text-embedding-3-small",
              "dimensions": 1536,
              "apiKey": "${secret:OPENAI_COMPAT_API_KEY}",
              "maxBatchSize": 64,
              "maxInputTokens": 8192,
              "timeoutSeconds": 60
            }
            """);

        var issues = PluginConfigSchema.Validate(schema.RootElement, config.RootElement);

        Assert.Empty(issues);
    }

    [Fact]
    public async Task Configure_registers_provider_and_does_not_log_the_secret()
    {
        const string secretValue = "sk-never-log-me";
        var context = new FakePluginContext(secretValue);
        var plugin = new OpenAiEmbeddingPlugin(new HttpClient(new RecordingHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))));

        await plugin.ConfigureAsync(context);

        var contributions = Assert.IsType<FakeContributions>(context.Contributions);
        var log = Assert.IsType<FakeLog>(context.Log);
        Assert.Equal(OpenAiEmbeddingPlugin.ContributionId, contributions.EmbeddingProviderId);
        Assert.Contains(log.Entries, entry => entry.Contains("embedding provider configured", StringComparison.Ordinal));
        Assert.DoesNotContain(secretValue, string.Join("\n", log.Entries), StringComparison.Ordinal);
        Assert.True(context.ProviderServiceCount > 0);
    }

    [Fact]
    public async Task Provider_rejects_oversized_batch_before_sending()
    {
        using var handler = new RecordingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var provider = CreateProvider(handler, maxBatchSize: 2);

        var exception = await Assert.ThrowsAsync<EmbeddingProviderException>(async () =>
            await provider.EmbedAsync(new EmbeddingRequest(["a", "b", "c"])));

        Assert.Equal("embedding.openai.batch_too_large", exception.Code);
        Assert.Equal(0, handler.Calls);
    }

    private static OpenAiEmbeddingProvider CreateProvider(RecordingHandler handler, int maxBatchSize = 8)
        => new(
            new OpenAiEmbeddingOptions
            {
                Endpoint = "https://example.invalid/v1",
                Model = "test-model",
                Dimensions = 1536,
                ApiKey = "${secret:OPENAI_COMPAT_API_KEY}",
                MaxBatchSize = maxBatchSize,
                MaxInputTokens = 100,
                TimeoutSeconds = 5,
            },
            "test-key",
            new HttpClient(handler));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
            : this((request, _) => responder(request))
        {
        }

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
            => _responder = responder;

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return _responder(request, cancellationToken);
        }
    }

    private sealed class FakePluginContext(string secretValue) : IPluginContext
    {
        public PluginManifest Manifest => OpenAiEmbeddingPlugin.Manifest;

        public IPluginConfig Config { get; } = new FakeConfig();

        public IPluginSecrets Secrets { get; } = new FakeSecrets(secretValue);

        public IPluginLog Log { get; } = new FakeLog();

        public IPluginLifetime Lifetime { get; } = new FakeLifetime();

        public IPluginServices Services { get; } = new FakeServices();

        public IContributionRegistry Contributions { get; } = new FakeContributions();

        public IPluginDependencies Dependencies { get; } = new FakeDependencies();

        public int ProviderServiceCount => Services.Collection.Count;
    }

    private sealed class FakeConfig : IPluginConfig
    {
        private readonly OpenAiEmbeddingOptions _options = new()
        {
            Endpoint = "https://example.invalid/v1",
            Model = "test-model",
            Dimensions = 1536,
            ApiKey = "${secret:OPENAI_COMPAT_API_KEY}",
            MaxBatchSize = 8,
            MaxInputTokens = 100,
            TimeoutSeconds = 5,
        };

        public JsonElement Raw => JsonDocument.Parse("{}").RootElement;

        public T Get<T>() where T : class
            => (T)(object)_options;

        public bool TryGet<T>(out T? value) where T : class
        {
            value = _options as T;
            return value is not null;
        }

        public bool TryGetSecretReference(string jsonPointer, out SecretReference? reference)
        {
            reference = null;
            return false;
        }
    }

    private sealed class FakeSecrets(string secretValue) : IPluginSecrets
    {
        public string GetRequired(string name) => secretValue;

        public string? Get(string name) => secretValue;
    }

    private sealed class FakeLog : IPluginLog
    {
        public List<string> Entries { get; } = [];

        public void Debug(string message, params object?[] args) => Entries.Add(message);
        public void Information(string message, params object?[] args) => Entries.Add(message);
        public void Warning(string message, params object?[] args) => Entries.Add(message);
        public void Error(Exception? exception, string message, params object?[] args) => Entries.Add(message);
    }

    private sealed class FakeLifetime : IPluginLifetime
    {
        public CancellationToken Stopping => CancellationToken.None;

        public IDisposable OnStop(Func<CancellationToken, ValueTask> callback) => new NoopDisposable();
    }

    private sealed class FakeServices : IPluginServices
    {
        public IServiceCollection Collection { get; } = new ServiceCollection();

        public IServiceProvider? Provider => null;
    }

    private sealed class FakeDependencies : IPluginDependencies
    {
        public bool IsLoaded(string pluginId) => false;
        public string? GetVersion(string pluginId) => null;
    }

    private sealed class FakeContributions : IContributionRegistry
    {
        public string? EmbeddingProviderId { get; private set; }

        public void AddEmbeddingProvider<TProvider>(string id) where TProvider : class, IEmbeddingProvider
            => EmbeddingProviderId = id;

        public void AddChatUpstream<TUpstream>(string id) where TUpstream : class, Natter.Chat.IChatUpstream
            => throw new NotSupportedException();
        public void AddChatResponder<TResponder>(string id, int priority = 0) where TResponder : class, Natter.Application.Chat.IChatResponder
            => throw new NotSupportedException();
        public void AddChatEventSubscriber<TSubscriber>(string id) where TSubscriber : class, Natter.Chat.IChatEventSubscriber
            => throw new NotSupportedException();
        public void AddCliCommand(Natter.Cli.Abstractions.CliCommandDescriptor descriptor) => throw new NotSupportedException();
        public void AddPortAdapter<TService, TImplementation>(string id, ServiceLifetime lifetime = ServiceLifetime.Singleton)
            where TService : class
            where TImplementation : class, TService => throw new NotSupportedException();
        public void AddPromptProvider<TProvider>(string id) where TProvider : class, IPromptProvider => throw new NotSupportedException();
        public void AddContextAssembler<TAssembler>(string id) where TAssembler : class, IContextAssembler => throw new NotSupportedException();
        public void AddMemoryStore<TStore>(string id) where TStore : class, IMemoryEntryStore => throw new NotSupportedException();
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
