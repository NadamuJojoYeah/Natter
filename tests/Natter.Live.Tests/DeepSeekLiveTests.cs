using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Natter.Feature.Ai.DeepSeek;
using Xunit;
using Xunit.Abstractions;

namespace Natter.Live.Tests;

/// <summary>
/// Layer 1: the DeepSeek adapter against the real Chat Completions API. These tests validate
/// the provider-specific contract (thinking flag, reasoning isolation, usage mapping, error
/// mapping) rather than Natter's orchestration.
/// </summary>
[Trait("Category", "Live")]
public sealed class DeepSeekLiveTests(ITestOutputHelper output)
{
    private static readonly LiveSettings Settings = LiveSettings.Current;

    private static DeepSeekOptions NewOptions(
        string model = "deepseek-flash",
        string thinking = "disabled",
        string effort = "none",
        int maxOutputTokens = 160,
        int timeoutSeconds = 90)
        => new()
        {
            ApiKey = "${secret:DEEPSEEK_API_KEY}",
            BaseUrl = "https://api.deepseek.com",
            Model = model,
            Thinking = thinking,
            ReasoningEffort = effort,
            SystemPrompt = "You are a terse integration-test assistant. Follow the instruction exactly.",
            Temperature = 0.2,
            MaxOutputTokens = maxOutputTokens,
            TimeoutSeconds = timeoutSeconds,
            Streaming = false,
            Tools = [],
        };

    [DeepSeekLiveFact(DisplayName = "DeepSeek: /models exposes the configured model and limits")]
    public async Task Models_endpoint_exposes_current_models()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Settings.ApiKey);

        using var response = await http.GetAsync("https://api.deepseek.com/models");
        var body = await response.Content.ReadAsStringAsync();
        output.WriteLine($"status={(int)response.StatusCode} bytes={body.Length}");

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        using var document = JsonDocument.Parse(body);
        var models = document.RootElement.GetProperty("data").EnumerateArray().ToList();
        var ids = models.Select(model => model.GetProperty("id").GetString()).ToList();
        output.WriteLine("models=" + string.Join(",", ids));

        var flash = models.Single(model => model.GetProperty("id").GetString() == "deepseek-flash");
        var context = flash.GetProperty("context_window").GetInt32();
        var maxOutput = flash.GetProperty("max_output_tokens").GetInt32();

        output.WriteLine($"flash context={context} maxOutput={maxOutput}");
        Assert.Equal(1_048_576, context);
        Assert.Equal(393_216, maxOutput);
    }

    [DeepSeekLiveFact(DisplayName = "DeepSeek: thinking disabled returns content, usage and no reasoning tokens")]
    public async Task Thinking_disabled_returns_content_without_reasoning()
    {
        var options = NewOptions(thinking: "disabled", effort: "none");
        var metrics = new DeepSeekMetrics();
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new DeepSeekChatClient(http, options, DeepSeekApiKey.From(Settings.ApiKey), metrics);

        var stopwatch = Stopwatch.StartNew();
        var response = await WithUpstreamRetryAsync(() => client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, options.SystemPrompt),
                new ChatMessage(ChatRole.User, "Reply with exactly this token and nothing else: natter-live-ok"),
            ]));
        stopwatch.Stop();

        output.WriteLine($"latency={stopwatch.ElapsedMilliseconds}ms model={response.ModelId} finish={response.FinishReason} text={response.Text}");
        output.WriteLine(
            $"usage in={response.Usage?.InputTokenCount} out={response.Usage?.OutputTokenCount} "
            + $"total={response.Usage?.TotalTokenCount} cached={response.Usage?.CachedInputTokenCount} "
            + $"reasoning={response.Usage?.ReasoningTokenCount}");

        Assert.False(string.IsNullOrWhiteSpace(response.Text));
        Assert.Equal("deepseek-flash", response.ModelId);
        Assert.True(response.Usage?.InputTokenCount is > 0);
        Assert.True(response.Usage?.OutputTokenCount is > 0);
        Assert.True(response.Usage?.TotalTokenCount >= response.Usage?.InputTokenCount);
        Assert.True(response.Usage?.ReasoningTokenCount is null or 0);
        Assert.True(response.AdditionalProperties?.ContainsKey("deepseek.usage") == true);
        Assert.Equal(1, metrics.Snapshot().Requests);
        Assert.Equal(0, metrics.Snapshot().Errors);
    }

    [DeepSeekLiveFact(DisplayName = "DeepSeek: thinking enabled keeps reasoning_content out of ChatResponse.Text")]
    public async Task Thinking_enabled_isolates_reasoning_content()
    {
        var options = NewOptions(thinking: "enabled", effort: "low", maxOutputTokens: 1024);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new DeepSeekChatClient(http, options, DeepSeekApiKey.From(Settings.ApiKey));

        const string prompt = "A box has 3 red balls and 5 blue balls. Reply with only the total number of balls.";
        var response = await WithUpstreamRetryAsync(() => client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, options.SystemPrompt),
                new ChatMessage(ChatRole.User, prompt),
            ]));

        var raw = await WithUpstreamRetryAsync(() => CallRawAsync(options, prompt));
        var reasoning = raw.ReasoningContent;
        var content = raw.Content;

        output.WriteLine($"raw.content={Truncate(content, 200)}");
        output.WriteLine($"raw.reasoningChars={reasoning?.Length ?? 0} reasoning={Truncate(reasoning, 200)}");
        output.WriteLine($"adapter.text={Truncate(response.Text, 200)} reasoningTokens={response.Usage?.ReasoningTokenCount}");

        Assert.False(string.IsNullOrWhiteSpace(response.Text));
        Assert.Equal(content?.Trim(), response.Text?.Trim());

        if (!string.IsNullOrWhiteSpace(reasoning))
        {
            Assert.DoesNotContain(reasoning.Trim(), response.Text!, StringComparison.Ordinal);
            Assert.NotEqual((reasoning + content).Trim(), response.Text!.Trim());
        }
        else
        {
            output.WriteLine("Note: the model returned an empty reasoning_content for this prompt; isolation could not be compared byte-for-byte.");
        }

        Assert.True(response.Usage?.ReasoningTokenCount is >= 0);
    }

    [DeepSeekLiveFact(DisplayName = "DeepSeek: invalid API key maps to authentication error without leaking the key")]
    public async Task Invalid_key_maps_to_authentication_error()
    {
        const string fakeKey = "sk-live-test-invalid-0000000000000000";
        var options = NewOptions();
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new DeepSeekChatClient(http, options, DeepSeekApiKey.From(fakeKey));

        var exception = await Assert.ThrowsAsync<DeepSeekException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")]));

        output.WriteLine($"kind={exception.Kind} status={exception.StatusCode} code={exception.ErrorCode} message={exception.Message}");

        Assert.Equal(DeepSeekErrorKind.Authentication, exception.Kind);
        Assert.Equal(401, exception.StatusCode);
        Assert.DoesNotContain(fakeKey, exception.Message, StringComparison.Ordinal);
        Assert.False(exception.Retryable);
    }

    [DeepSeekLiveFact(DisplayName = "DeepSeek: unknown model maps to invalid request")]
    public async Task Unknown_model_maps_to_invalid_request()
    {
        var options = NewOptions(model: "natter-model-that-does-not-exist");
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new DeepSeekChatClient(http, options, DeepSeekApiKey.From(Settings.ApiKey));

        var exception = await Assert.ThrowsAsync<DeepSeekException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")]));

        output.WriteLine($"kind={exception.Kind} status={exception.StatusCode} code={exception.ErrorCode} message={exception.Message}");

        Assert.Equal(DeepSeekErrorKind.InvalidRequest, exception.Kind);
        Assert.Equal(400, exception.StatusCode);
    }

    [DeepSeekLiveFact(DisplayName = "DeepSeek: unreachable endpoint maps to unavailable")]
    public async Task Unreachable_endpoint_maps_to_unavailable()
    {
        var options = NewOptions();
        options.BaseUrl = "http://127.0.0.1:1";
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new DeepSeekChatClient(http, options, DeepSeekApiKey.From(Settings.ApiKey));

        var exception = await Assert.ThrowsAsync<DeepSeekException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")]));

        output.WriteLine($"kind={exception.Kind} message={exception.Message}");
        Assert.Equal(DeepSeekErrorKind.Unavailable, exception.Kind);
        Assert.True(exception.Retryable);
    }

    [DeepSeekLiveFact(DisplayName = "DeepSeek: three concurrent requests all succeed")]
    public async Task Concurrent_requests_succeed()
    {
        var options = NewOptions(maxOutputTokens: 96);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new DeepSeekChatClient(http, options, DeepSeekApiKey.From(Settings.ApiKey));

        var stopwatch = Stopwatch.StartNew();
        var responses = await Task.WhenAll(Enumerable.Range(1, 3).Select(index => WithUpstreamRetryAsync(
            () => client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, options.SystemPrompt),
                new ChatMessage(ChatRole.User, $"Reply with the single word: ok{index}"),
            ]))));
        stopwatch.Stop();

        foreach (var response in responses)
            output.WriteLine($"text={Truncate(response.Text, 80)} out={response.Usage?.OutputTokenCount}");

        output.WriteLine($"wall={stopwatch.ElapsedMilliseconds}ms");
        Assert.All(responses, response => Assert.False(string.IsNullOrWhiteSpace(response.Text)));
        Assert.Equal(3, responses.Select(response => response.Text).Distinct().Count());
    }

    [DeepSeekLiveFact(DisplayName = "DeepSeek: usage cache counters are reported on a repeated prompt")]
    public async Task Usage_cache_counters_are_reported()
    {
        var options = NewOptions(thinking: "disabled");
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new DeepSeekChatClient(http, options, DeepSeekApiKey.From(Settings.ApiKey));

        var prompt = new string('x', 4000);
        var first = await WithUpstreamRetryAsync(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt + " Reply with: a")]));
        var second = await WithUpstreamRetryAsync(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt + " Reply with: b")]));

        var firstUsage = (DeepSeekUsage)first.AdditionalProperties!["deepseek.usage"]!;
        var secondUsage = (DeepSeekUsage)second.AdditionalProperties!["deepseek.usage"]!;

        output.WriteLine(
            $"first hit={firstUsage.PromptCacheHitTokens} miss={firstUsage.PromptCacheMissTokens} prompt={firstUsage.PromptTokens}; "
            + $"second hit={secondUsage.PromptCacheHitTokens} miss={secondUsage.PromptCacheMissTokens} prompt={secondUsage.PromptTokens}");

        Assert.True(firstUsage.PromptTokens > 0);
        Assert.True(secondUsage.PromptTokens > 0);
        Assert.True(secondUsage.PromptCacheHitTokens >= 0);
    }

    /// <summary>
    /// Real APIs return transient 5xx (a live run observed DeepSeek "550 service unavailable").
    /// Retries are limited to errors the adapter itself marks retryable; error mapping has its
    /// own dedicated tests, so this does not hide a defect.
    /// </summary>
    private static async Task<T> WithUpstreamRetryAsync<T>(Func<Task<T>> action, int attempts = 3)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception exception) when (attempt < attempts && IsTransient(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt));
            }
        }
    }

    private static bool IsTransient(Exception exception)
        => exception is DeepSeekException { Retryable: true } || exception is HttpRequestException;

    private static async Task<(string? Content, string? ReasoningContent)> CallRawAsync(
        DeepSeekOptions options,
        string prompt)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Settings.ApiKey);

        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["messages"] = new[]
            {
                new Dictionary<string, object?> { ["role"] = "system", ["content"] = options.SystemPrompt },
                new Dictionary<string, object?> { ["role"] = "user", ["content"] = prompt },
            },
            ["stream"] = false,
            ["thinking"] = new Dictionary<string, object?> { ["type"] = "enabled" },
            ["reasoning_effort"] = "low",
            ["max_tokens"] = 1024,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.deepseek.com/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        using var document = JsonDocument.Parse(body);
        var message = document.RootElement.GetProperty("choices")[0].GetProperty("message");
        var content = message.TryGetProperty("content", out var c) ? c.GetString() : null;
        var reasoning = message.TryGetProperty("reasoning_content", out var r) ? r.GetString() : null;
        return (content, reasoning);
    }

    private static string? Truncate(string? value, int max)
        => value is null ? null : value.Length <= max ? value : value[..max] + "...";
}
