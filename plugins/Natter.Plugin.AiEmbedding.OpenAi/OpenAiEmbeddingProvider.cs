using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Natter.Application.Chat;

namespace Natter.Plugin.AiEmbedding.OpenAi;

/// <summary>OpenAI-compatible <c>/v1/embeddings</c> provider (DashScope compatible mode included).</summary>
public sealed class OpenAiEmbeddingProvider : IEmbeddingProvider
{
    public const string ProviderId = "openai-compatible";
    public const string ProviderCodePrefix = "embedding.openai";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly OpenAiEmbeddingOptions _options;

    public OpenAiEmbeddingProvider(OpenAiEmbeddingOptions options, string apiKey, HttpClient httpClient)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _apiKey = string.IsNullOrWhiteSpace(apiKey)
            ? throw new ArgumentException("An API key is required.", nameof(apiKey))
            : apiKey;
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public string Id => ProviderId;

    public int MaxBatchSize => _options.MaxBatchSize;

    public int MaxInputTokens => _options.MaxInputTokens;

    public async ValueTask<EmbeddingResult> EmbedAsync(
        EmbeddingRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Inputs);

        if (request.Inputs.Count == 0)
        {
            return new EmbeddingResult(
                [],
                request.Model ?? _options.Model,
                request.Dimensions ?? _options.Dimensions ?? 0,
                0);
        }

        if (request.Inputs.Count > MaxBatchSize)
        {
            throw new EmbeddingProviderException(
                EmbeddingErrorKind.InvalidRequest,
                $"{ProviderCodePrefix}.batch_too_large",
                $"Provider accepts at most {MaxBatchSize} inputs per call, got {request.Inputs.Count}.");
        }

        foreach (var input in request.Inputs)
        {
            if (string.IsNullOrEmpty(input))
            {
                throw new EmbeddingProviderException(
                    EmbeddingErrorKind.InvalidRequest,
                    $"{ProviderCodePrefix}.empty_input",
                    "Embedding input must not be empty.");
            }

            if (EmbeddingPlanner.EstimateTokens(input) > MaxInputTokens)
            {
                throw new EmbeddingProviderException(
                    EmbeddingErrorKind.InvalidRequest,
                    $"{ProviderCodePrefix}.input_too_long",
                    $"An embedding input exceeds the configured {MaxInputTokens} token limit; the host must truncate it.");
            }
        }

        var model = request.Model ?? _options.Model;
        var dimensions = request.Dimensions ?? _options.Dimensions;
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["input"] = request.Inputs,
        };
        if (dimensions is > 0)
            payload["dimensions"] = dimensions;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint())
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions),
                Encoding.UTF8,
                "application/json"),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new EmbeddingProviderException(
                EmbeddingErrorKind.Timeout,
                $"{ProviderCodePrefix}.timeout",
                $"Embedding request timed out after {_options.TimeoutSeconds}s.");
        }
        catch (HttpRequestException exception)
        {
            throw new EmbeddingProviderException(
                EmbeddingErrorKind.Unavailable,
                $"{ProviderCodePrefix}.unavailable",
                $"Embedding endpoint is unavailable ({exception.GetType().Name}).",
                innerException: exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw MapStatus(response.StatusCode);

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                return Parse(document.RootElement, request.Inputs.Count, model);
            }
            catch (EmbeddingProviderException)
            {
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
            {
                throw new EmbeddingProviderException(
                    EmbeddingErrorKind.InvalidResponse,
                    $"{ProviderCodePrefix}.invalid_response",
                    "Embedding endpoint returned an invalid response.",
                    innerException: exception);
            }
        }
    }

    private Uri BuildEndpoint()
    {
        var builder = new UriBuilder(new Uri(_options.Endpoint, UriKind.Absolute));
        var path = builder.Path.TrimEnd('/');
        if (!path.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
            path += "/embeddings";
        builder.Path = path;
        return builder.Uri;
    }

    private static EmbeddingResult Parse(JsonElement root, int inputCount, string requestedModel)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new EmbeddingProviderException(
                EmbeddingErrorKind.InvalidResponse,
                $"{ProviderCodePrefix}.invalid_response",
                "Embedding response is missing the 'data' array.");

        var entries = new List<(int Index, float[] Vector)>();
        var position = 0;
        foreach (var item in data.EnumerateArray())
        {
            var index = item.TryGetProperty("index", out var indexElement)
                ? indexElement.GetInt32()
                : position;
            position++;

            if (index < 0 || index >= inputCount
                || entries.Any(entry => entry.Index == index)
                || !item.TryGetProperty("embedding", out var embeddingElement)
                || embeddingElement.ValueKind != JsonValueKind.Array)
            {
                throw new EmbeddingProviderException(
                    EmbeddingErrorKind.InvalidResponse,
                    $"{ProviderCodePrefix}.invalid_response",
                    "Embedding response contains an invalid index or vector.");
            }

            var vector = embeddingElement.EnumerateArray().Select(value => value.GetSingle()).ToArray();
            if (vector.Length == 0)
            {
                throw new EmbeddingProviderException(
                    EmbeddingErrorKind.InvalidResponse,
                    $"{ProviderCodePrefix}.invalid_response",
                    "Embedding response contains an empty vector.");
            }

            entries.Add((index, vector));
        }

        if (entries.Count != inputCount)
        {
            throw new EmbeddingProviderException(
                EmbeddingErrorKind.InvalidResponse,
                $"{ProviderCodePrefix}.invalid_response",
                $"Embedding response returned {entries.Count} vectors for {inputCount} inputs.");
        }

        entries.Sort((left, right) => left.Index.CompareTo(right.Index));
        var dimensions = entries[0].Vector.Length;
        if (entries.Any(entry => entry.Vector.Length != dimensions))
        {
            throw new EmbeddingProviderException(
                EmbeddingErrorKind.InvalidResponse,
                $"{ProviderCodePrefix}.invalid_response",
                "Embedding response contains inconsistent vector dimensions.");
        }

        var model = root.TryGetProperty("model", out var modelElement)
                    && modelElement.ValueKind == JsonValueKind.String
            ? modelElement.GetString() ?? requestedModel
            : requestedModel;
        int? promptTokens = null;
        if (root.TryGetProperty("usage", out var usage)
            && usage.TryGetProperty("prompt_tokens", out var promptTokenElement)
            && promptTokenElement.TryGetInt32(out var promptTokenCount))
        {
            promptTokens = promptTokenCount;
        }

        return new EmbeddingResult(
            entries.Select(entry => new ReadOnlyMemory<float>(entry.Vector)).ToArray(),
            model,
            dimensions,
            promptTokens);
    }

    private static EmbeddingProviderException MapStatus(HttpStatusCode statusCode)
        => statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new EmbeddingProviderException(
                    EmbeddingErrorKind.Authentication,
                    $"{ProviderCodePrefix}.authentication",
                    "Embedding endpoint rejected the API key.",
                    (int)statusCode),
            HttpStatusCode.TooManyRequests =>
                new EmbeddingProviderException(
                    EmbeddingErrorKind.RateLimited,
                    $"{ProviderCodePrefix}.rate_limited",
                    "Embedding endpoint rate-limited the request.",
                    (int)statusCode),
            HttpStatusCode.RequestTimeout =>
                new EmbeddingProviderException(
                    EmbeddingErrorKind.Timeout,
                    $"{ProviderCodePrefix}.timeout",
                    "Embedding endpoint timed out.",
                    (int)statusCode),
            HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity =>
                new EmbeddingProviderException(
                    EmbeddingErrorKind.InvalidRequest,
                    $"{ProviderCodePrefix}.invalid_request",
                    "Embedding endpoint rejected the request.",
                    (int)statusCode),
            >= HttpStatusCode.InternalServerError =>
                new EmbeddingProviderException(
                    EmbeddingErrorKind.Unavailable,
                    $"{ProviderCodePrefix}.unavailable",
                    "Embedding endpoint is unavailable.",
                    (int)statusCode),
            _ => new EmbeddingProviderException(
                EmbeddingErrorKind.Unknown,
                $"{ProviderCodePrefix}.unknown",
                $"Embedding endpoint returned HTTP {(int)statusCode}.",
                (int)statusCode),
        };
}
