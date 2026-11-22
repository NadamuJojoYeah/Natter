using Microsoft.Extensions.DependencyInjection;
using Natter.Plugin.Abstractions;

namespace Natter.Plugin.AiEmbedding.OpenAi;

/// <summary>NPS plugin contributing an OpenAI-compatible embedding provider.</summary>
public sealed class OpenAiEmbeddingPlugin : INatterPlugin
{
    public const string ContributionId = "openai-compatible";

    private readonly HttpClient? _httpClient;

    public OpenAiEmbeddingPlugin()
    {
    }

    /// <summary>Test/DI seam; production creates a plugin-local HttpClient.</summary>
    public OpenAiEmbeddingPlugin(HttpClient httpClient)
        => _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public static PluginManifest Manifest { get; } = PluginManifestReader.ReadEmbedded(
        typeof(OpenAiEmbeddingPlugin).Assembly,
        "Natter.Plugin.AiEmbedding.OpenAi.plugin.json");

    public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var options = context.Config.Get<OpenAiEmbeddingOptions>();
        Validate(options);

        if (!SecretReference.TryParse(options.ApiKey, out var secret))
        {
            throw new InvalidOperationException(
                "Embedding config 'apiKey' must be a '${secret:NAME}' reference; literal keys are not accepted.");
        }

        var apiKey = context.Secrets.GetRequired(secret.Name);
        var services = context.Services.Collection;
        services.AddSingleton(options);
        services.AddSingleton(_httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
        services.AddSingleton(provider => new OpenAiEmbeddingProvider(
            options,
            apiKey,
            provider.GetRequiredService<HttpClient>()));

        context.Contributions.AddEmbeddingProvider<OpenAiEmbeddingProvider>(ContributionId);

        var endpointHost = Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var uri)
            ? uri.Host
            : "invalid";
        context.Log.Information(
            "embedding provider configured id={ProviderId} model={Model} endpointHost={EndpointHost}",
            OpenAiEmbeddingProvider.ProviderId,
            options.Model,
            endpointHost);

        return ValueTask.CompletedTask;
    }

    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    private static void Validate(OpenAiEmbeddingOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Endpoint)
            || !Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Embedding config 'endpoint' must be an absolute http(s) URL.");
        }

        if (string.IsNullOrWhiteSpace(options.Model))
            throw new InvalidOperationException("Embedding config 'model' must not be empty.");
        if (options.Dimensions is <= 0)
            throw new InvalidOperationException("Embedding config 'dimensions' must be positive when set.");
        if (options.MaxBatchSize is < 1 or > 2048)
            throw new InvalidOperationException("Embedding config 'maxBatchSize' must be between 1 and 2048.");
        if (options.MaxInputTokens is < 1 or > 200000)
            throw new InvalidOperationException("Embedding config 'maxInputTokens' must be between 1 and 200000.");
        if (options.TimeoutSeconds is < 1 or > 600)
            throw new InvalidOperationException("Embedding config 'timeoutSeconds' must be between 1 and 600.");
    }
}
