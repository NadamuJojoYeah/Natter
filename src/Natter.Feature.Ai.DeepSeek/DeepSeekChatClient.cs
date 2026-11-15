using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Natter.Feature.Ai.DeepSeek;

/// <summary>
/// Custom DeepSeek Chat Completions client. It maps the provider-specific thinking and
/// reasoning fields rather than pretending DeepSeek is a generic OpenAI endpoint.
/// </summary>
public sealed class DeepSeekChatClient : IChatClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient;
    private readonly DeepSeekOptions _options;
    private readonly DeepSeekApiKey _apiKey;
    private readonly DeepSeekMetrics _metrics;
    private readonly ChatClientMetadata _metadata;
    private readonly Uri _endpoint;
    private bool _disposed;

    public DeepSeekChatClient(
        HttpClient httpClient,
        DeepSeekOptions options,
        DeepSeekApiKey apiKey,
        DeepSeekMetrics? metrics = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _metrics = metrics ?? new DeepSeekMetrics();

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("DeepSeek baseUrl must be an absolute http(s) URL.", nameof(options));
        }

        var baseText = options.BaseUrl.TrimEnd('/');
        _endpoint = baseText.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? new Uri(baseText, UriKind.Absolute)
            : new Uri(baseText + "/chat/completions", UriKind.Absolute);

        _metadata = new ChatClientMetadata("deepseek", baseUri, options.Model);
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
        var request = BuildRequest(messages, options, model);
        var json = JsonSerializer.Serialize(request, JsonOptions);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey.Value);

            using var response = await _httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var retryAfter = GetRetryAfter(response);
                throw DeepSeekException.FromHttp((int)response.StatusCode, body, retryAfter);
            }

            var parsed = ParseResponse(body, model);
            stopwatch.Stop();
            _metrics.RecordSuccess(parsed.Usage, (int)stopwatch.ElapsedMilliseconds);
            return parsed.Response;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            _metrics.RecordError(DeepSeekErrorKind.Timeout, "request timed out");
            throw DeepSeekException.Timeout("DeepSeek request timed out.", ex);
        }
        catch (DeepSeekException ex)
        {
            stopwatch.Stop();
            _metrics.RecordError(ex.Kind, ex.Message);
            throw;
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            _metrics.RecordError(DeepSeekErrorKind.Unavailable, ex.Message);
            throw new DeepSeekException(DeepSeekErrorKind.Unavailable, "DeepSeek is temporarily unavailable.", innerException: ex);
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Task.Yield();
        throw new NotSupportedException("DeepSeek streaming is disabled in this Natter version.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
            return null;

        if (serviceType == typeof(ChatClientMetadata))
            return _metadata;

        return serviceType == typeof(IChatClient) ? this : null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
    }

    private Dictionary<string, object?> BuildRequest(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        string model)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = model,
            ["messages"] = messages.Select(message => new Dictionary<string, object?>
            {
                ["role"] = ToRole(message.Role),
                ["content"] = message.Text ?? string.Empty,
            }).ToArray(),
            ["stream"] = false,
            ["thinking"] = new Dictionary<string, object?>
            {
                ["type"] = _options.IsThinkingEnabled ? "enabled" : "disabled",
            },
            ["max_tokens"] = options?.MaxOutputTokens ?? _options.MaxOutputTokens,
        };

        if (!_options.IsThinkingEnabled)
        {
            payload["temperature"] = options?.Temperature ?? (float?)_options.Temperature;
        }
        else if (!string.Equals(_options.ReasoningEffort, "none", StringComparison.OrdinalIgnoreCase))
        {
            payload["reasoning_effort"] = _options.ReasoningEffort;
        }

        if (options?.Tools is { Count: > 0 })
            throw new NotSupportedException("DeepSeek tools are not enabled in this Natter version.");

        return payload;
    }

    private (ChatResponse Response, DeepSeekUsage Usage) ParseResponse(string body, string model)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            throw new DeepSeekException(DeepSeekErrorKind.Unknown, "DeepSeek returned no choices.");
        }

        var choice = choices[0];
        var message = choice.TryGetProperty("message", out var m) ? m : default;
        var content = message.ValueKind == JsonValueKind.Object
                      && message.TryGetProperty("content", out var c)
                      && c.ValueKind == JsonValueKind.String
            ? c.GetString() ?? string.Empty
            : string.Empty;

        var finishReason = choice.TryGetProperty("finish_reason", out var finish)
                           && finish.ValueKind == JsonValueKind.String
            ? finish.GetString() ?? "stop"
            : "stop";

        var usage = ParseUsage(root);
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, content))
        {
            ModelId = model,
            FinishReason = new ChatFinishReason(finishReason),
            Usage = new UsageDetails
            {
                InputTokenCount = usage.PromptTokens,
                OutputTokenCount = usage.CompletionTokens,
                TotalTokenCount = usage.TotalTokens,
                CachedInputTokenCount = usage.PromptCacheHitTokens,
                ReasoningTokenCount = usage.ReasoningTokens,
            },
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["deepseek.usage"] = usage,
            },
        };

        return (response, usage);
    }

    private static DeepSeekUsage ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return DeepSeekUsage.Empty;

        static long Read(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;

        var reasoning = 0L;
        if (usage.TryGetProperty("completion_tokens_details", out var details)
            && details.ValueKind == JsonValueKind.Object)
        {
            reasoning = Read(details, "reasoning_tokens");
        }

        return new DeepSeekUsage(
            Read(usage, "prompt_tokens"),
            Read(usage, "completion_tokens"),
            Read(usage, "total_tokens"),
            Read(usage, "prompt_cache_hit_tokens"),
            Read(usage, "prompt_cache_miss_tokens"),
            reasoning);
    }

    private static string ToRole(ChatRole role)
    {
        if (role == ChatRole.System)
            return "system";
        if (role == ChatRole.User)
            return "user";
        if (role == ChatRole.Assistant)
            return "assistant";
        if (role == ChatRole.Tool)
            return "tool";

        return role.Value ?? "user";
    }

    private static int? GetRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return (int)Math.Max(0, delta.TotalMilliseconds);

        if (response.Headers.RetryAfter?.Date is { } date)
            return (int)Math.Max(0, (date - DateTimeOffset.UtcNow).TotalMilliseconds);

        return null;
    }
}

public sealed record DeepSeekUsage(
    long PromptTokens,
    long CompletionTokens,
    long TotalTokens,
    long PromptCacheHitTokens,
    long PromptCacheMissTokens,
    long ReasoningTokens)
{
    public static DeepSeekUsage Empty { get; } = new(0, 0, 0, 0, 0, 0);
}
