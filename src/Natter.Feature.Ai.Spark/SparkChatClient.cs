using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Natter.Feature.Ai;

namespace Natter.Feature.Ai.Spark;

/// <summary>
/// iFlytek Spark OpenAI-compatible Chat Completions client. The APIPassword is sent as a
/// Bearer token and never enters logs, metrics or errors.
/// </summary>
public sealed class SparkChatClient : IChatClient
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly HttpClient _httpClient;
    private readonly SparkOptions _options;
    private readonly string _apiKey;
    private readonly SparkMetrics _metrics;
    private readonly Uri _endpoint;
    private readonly ChatClientMetadata _metadata;
    private bool _disposed;

    public SparkChatClient(HttpClient httpClient, SparkOptions options, string apiKey, SparkMetrics? metrics = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Spark API key must not be empty.", nameof(apiKey));
        _apiKey = apiKey;
        _metrics = metrics ?? new SparkMetrics();

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Spark baseUrl must be an absolute http(s) URL.", nameof(options));
        }

        var trimmed = options.BaseUrl.TrimEnd('/');
        _endpoint = trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? new Uri(trimmed, UriKind.Absolute)
            : new Uri(trimmed + "/chat/completions", UriKind.Absolute);
        _metadata = new ChatClientMetadata("iflytek-spark", baseUri, options.Model);
    }

    public ChatClientMetadata Metadata => _metadata;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(messages);

        var model = string.IsNullOrWhiteSpace(options?.ModelId) ? _options.Model : options.ModelId!;
        var payload = BuildRequest(messages, options, model);
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var maxRetries = Math.Clamp(_options.MaxRetries, 0, 5);

        for (var attempt = 0; ; attempt++)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var parsed = await SendOnceAsync(json, model, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();
                _metrics.RecordSuccess(parsed.Usage, (int)stopwatch.ElapsedMilliseconds);
                return parsed.Response;
            }
            catch (SparkException ex)
            {
                stopwatch.Stop();
                _metrics.RecordError(ex.Kind, ex.Message);
                if (attempt >= maxRetries || !ex.Retryable)
                    throw;

                await Task.Delay(GetRetryDelay(attempt, ex), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Task.Yield();
        throw new NotSupportedException("Spark streaming is disabled in this Natter version.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
            return null;
        return serviceType == typeof(ChatClientMetadata) ? _metadata
            : serviceType == typeof(IChatClient) ? this
            : null;
    }

    public void Dispose() => _disposed = true;

    private Dictionary<string, object?> BuildRequest(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        string model)
    {
        if (options?.Tools is { Count: > 0 })
            throw new NotSupportedException("Spark tools are not enabled in this Natter version.");

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = model,
            ["messages"] = messages.Select(message => new Dictionary<string, object?>
            {
                ["role"] = ToRole(message.Role),
                ["content"] = message.Text ?? string.Empty,
            }).ToArray(),
            ["stream"] = false,
            ["max_completion_tokens"] = options?.MaxOutputTokens ?? _options.MaxOutputTokens,
            ["thinking"] = new Dictionary<string, object?>
            {
                ["type"] = string.Equals(_options.Thinking, "enabled", StringComparison.OrdinalIgnoreCase)
                    ? "enabled"
                    : "disabled",
            },
        };
    }

    private async Task<(ChatResponse Response, SparkUsage Usage)> SendOnceAsync(
        string json,
        string model,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw SparkException.FromHttp((int)response.StatusCode, body, GetRetryAfter(response));

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("code", out var codeElement)
                && codeElement.TryGetInt32(out var providerCode)
                && providerCode != 0)
            {
                throw SparkException.FromHttp((int)response.StatusCode, body, GetRetryAfter(response));
            }

            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                throw new SparkException(AiErrorKind.Unknown, "Spark returned no choices.");
            }

            var message = choices[0].TryGetProperty("message", out var m) ? m : default;
            var content = message.ValueKind == JsonValueKind.Object
                          && message.TryGetProperty("content", out var c)
                          && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrEmpty(content)
                && choices[0].TryGetProperty("delta", out var delta)
                && delta.ValueKind == JsonValueKind.Object
                && delta.TryGetProperty("content", out var dc)
                && dc.ValueKind == JsonValueKind.String)
            {
                content = dc.GetString() ?? string.Empty;
            }

            var reasoning = message.ValueKind == JsonValueKind.Object
                             && message.TryGetProperty("reasoning_content", out var rc)
                             && rc.ValueKind == JsonValueKind.String
                ? rc.GetString()
                : null;
            var usage = ParseUsage(root);
            var responseObject = new ChatResponse(new ChatMessage(ChatRole.Assistant, content))
            {
                ModelId = model,
                Usage = new UsageDetails
                {
                    InputTokenCount = usage.PromptTokens,
                    OutputTokenCount = usage.CompletionTokens,
                    TotalTokenCount = usage.TotalTokens,
                },
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["spark.usage"] = usage,
                    ["spark.has_reasoning"] = reasoning is { Length: > 0 },
                },
            };

            return (responseObject, usage);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw SparkException.Timeout("Spark request timed out.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new SparkException(AiErrorKind.Unavailable, "Spark is temporarily unavailable.", innerException: ex);
        }
        catch (JsonException ex)
        {
            throw new SparkException(AiErrorKind.Unknown, "Spark returned an invalid response.", innerException: ex);
        }
    }

    private static SparkUsage ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return SparkUsage.Empty;

        static long Read(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;

        return new SparkUsage(
            Read(usage, "prompt_tokens"),
            Read(usage, "completion_tokens"),
            Read(usage, "total_tokens"));
    }

    private TimeSpan GetRetryDelay(int attempt, SparkException exception)
    {
        if (exception.RetryAfterMs is { } retryAfter && retryAfter > 0)
            return TimeSpan.FromMilliseconds(Math.Min(retryAfter, Math.Max(0, _options.RetryMaxDelayMs)));

        var exponential = Math.Max(0, _options.RetryBaseDelayMs) * Math.Pow(2, Math.Min(attempt, 10));
        var capped = Math.Min(exponential, Math.Max(0, _options.RetryMaxDelayMs));
        return TimeSpan.FromMilliseconds(capped + Random.Shared.Next(0, 101));
    }

    private static int? GetRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return (int)Math.Max(0, delta.TotalMilliseconds);
        if (response.Headers.RetryAfter?.Date is { } date)
            return (int)Math.Max(0, (date - DateTimeOffset.UtcNow).TotalMilliseconds);
        return null;
    }

    private static string ToRole(ChatRole role)
        => role == ChatRole.System ? "system"
            : role == ChatRole.User ? "user"
            : role == ChatRole.Assistant ? "assistant"
            : role == ChatRole.Tool ? "tool"
            : role.Value ?? "user";
}

public sealed record SparkUsage(
    long PromptTokens,
    long CompletionTokens,
    long TotalTokens,
    long CachedTokens = 0,
    long ReasoningTokens = 0)
{
    public static SparkUsage Empty { get; } = new(0, 0, 0);
}
