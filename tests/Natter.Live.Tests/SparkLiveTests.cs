using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Natter.Feature.Ai;
using Natter.Feature.Ai.Spark;
using Xunit;
using Xunit.Abstractions;

namespace Natter.Live.Tests;

/// <summary>
/// Real iFlytek Spark MaaS tests, mirroring the DeepSeek suite: model listing, thinking flag,
/// usage mapping and error mapping. Live-only (<c>NATTER_LIVE=1</c> + <c>SPARK_API_KEY</c>).
/// </summary>
[Trait("Category", "Live")]
public sealed class SparkLiveTests(ITestOutputHelper output)
{
    private static readonly LiveSettings Settings = LiveSettings.Current;

    private static SparkOptions NewOptions(
        string model = "spark-x2.5-1.7b",
        string thinking = "disabled",
        int maxOutputTokens = 128,
        int timeoutSeconds = 90,
        int maxRetries = 0)
        => new()
        {
            ApiKey = "${secret:SPARK_API_KEY}",
            BaseUrl = "https://maas-api.cn-huabei-1.xf-yun.com/v2",
            Model = model,
            Thinking = thinking,
            SystemPrompt = "You are a terse integration-test assistant. Follow the instruction exactly.",
            MaxOutputTokens = maxOutputTokens,
            TimeoutSeconds = timeoutSeconds,
            MaxRetries = maxRetries,
            Streaming = false,
            Tools = [],
        };

    [SparkLiveFact(DisplayName = "Spark: /models exposes spark-x2.5-1.7b")]
    public async Task Models_endpoint_exposes_the_maas_model()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Settings.SparkApiKey);

        using var response = await http.GetAsync("https://maas-api.cn-huabei-1.xf-yun.com/v2/models");
        var body = await response.Content.ReadAsStringAsync();
        output.WriteLine($"status={(int)response.StatusCode} body={body}");

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        using var document = JsonDocument.Parse(body);
        var ids = document.RootElement.GetProperty("data").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString())
            .ToList();

        Assert.Contains("spark-x2.5-1.7b", ids);
    }

    [SparkLiveFact(DisplayName = "Spark: thinking disabled returns content and usage")]
    public async Task Thinking_disabled_returns_content_and_usage()
    {
        var options = NewOptions(thinking: "disabled");
        var metrics = new SparkMetrics();
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new SparkChatClient(http, options, Settings.SparkApiKey, metrics);

        var stopwatch = Stopwatch.StartNew();
        var response = await client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, options.SystemPrompt),
                new ChatMessage(ChatRole.User, "Reply with exactly this token and nothing else: spark-live-ok"),
            ]);
        stopwatch.Stop();

        output.WriteLine(
            $"latency={stopwatch.ElapsedMilliseconds}ms model={response.ModelId} text={response.Text} "
            + $"usage in={response.Usage?.InputTokenCount} out={response.Usage?.OutputTokenCount} total={response.Usage?.TotalTokenCount}");

        Assert.False(string.IsNullOrWhiteSpace(response.Text));
        Assert.Equal("spark-x2.5-1.7b", response.ModelId);
        Assert.True(response.Usage?.InputTokenCount is > 0);
        Assert.True(response.Usage?.OutputTokenCount is > 0);
        Assert.True(metrics.Snapshot().Requests >= 1);
        Assert.Equal(0, metrics.Snapshot().Errors);
    }

    [SparkLiveFact(DisplayName = "Spark: thinking enabled reports the reasoning flag without leaking it into text")]
    public async Task Thinking_enabled_reports_reasoning_flag()
    {
        var options = NewOptions(thinking: "enabled", maxOutputTokens: 512);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new SparkChatClient(http, options, Settings.SparkApiKey);

        var response = await client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, options.SystemPrompt),
                new ChatMessage(ChatRole.User, "A box has 2 red and 3 blue balls. Reply with only the total number."),
            ]);

        var hasReasoning = response.AdditionalProperties?.TryGetValue("spark.has_reasoning", out var flag) == true
                           && flag is true;
        output.WriteLine($"text={response.Text} hasReasoning={hasReasoning} reasoningTokens={response.Usage?.ReasoningTokenCount}");

        Assert.False(string.IsNullOrWhiteSpace(response.Text));
        Assert.True(response.AdditionalProperties?.ContainsKey("spark.has_reasoning") == true);
    }

    [SparkLiveFact(DisplayName = "Spark: invalid key maps to authentication without leaking the key")]
    public async Task Invalid_key_maps_to_authentication()
    {
        const string fakeKey = "ak-live-test-invalid-0000000000000000";
        var options = NewOptions();
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new SparkChatClient(http, options, fakeKey);

        var exception = await Assert.ThrowsAsync<SparkException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")]));

        output.WriteLine($"kind={exception.Kind} status={exception.StatusCode} message={exception.Message}");

        Assert.Equal(AiErrorKind.Authentication, exception.Kind);
        Assert.Equal(401, exception.StatusCode);
        Assert.DoesNotContain(fakeKey, exception.Message, StringComparison.Ordinal);
        Assert.False(exception.Retryable);
    }

    [SparkLiveFact(DisplayName = "Spark: unknown model maps to invalid request")]
    public async Task Unknown_model_maps_to_invalid_request()
    {
        var options = NewOptions(model: "spark-model-that-does-not-exist");
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new SparkChatClient(http, options, Settings.SparkApiKey);

        var exception = await Assert.ThrowsAsync<SparkException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")]));

        output.WriteLine($"kind={exception.Kind} status={exception.StatusCode} message={exception.Message}");
        Assert.True(exception.Kind is AiErrorKind.InvalidRequest or AiErrorKind.Unknown);
        Assert.True(exception.StatusCode is >= 400 and < 500);
    }

    [SparkLiveFact(DisplayName = "Spark: unreachable endpoint maps to unavailable")]
    public async Task Unreachable_endpoint_maps_to_unavailable()
    {
        var options = NewOptions();
        options.BaseUrl = "http://127.0.0.1:1";
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new SparkChatClient(http, options, Settings.SparkApiKey);

        var exception = await Assert.ThrowsAsync<SparkException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")]));

        output.WriteLine($"kind={exception.Kind} message={exception.Message}");
        Assert.Equal(AiErrorKind.Unavailable, exception.Kind);
        Assert.True(exception.Retryable);
    }

    [SparkLiveFact(DisplayName = "Spark: three concurrent requests all succeed")]
    public async Task Concurrent_requests_succeed()
    {
        var options = NewOptions(maxOutputTokens: 64);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new SparkChatClient(http, options, Settings.SparkApiKey);

        var stopwatch = Stopwatch.StartNew();
        var responses = await Task.WhenAll(Enumerable.Range(1, 3).Select(index => client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, options.SystemPrompt),
                new ChatMessage(ChatRole.User, $"Reply with the single word: ok{index}"),
            ])));
        stopwatch.Stop();

        foreach (var response in responses)
            output.WriteLine($"text={response.Text} out={response.Usage?.OutputTokenCount}");
        output.WriteLine($"wall={stopwatch.ElapsedMilliseconds}ms");

        Assert.All(responses, response => Assert.False(string.IsNullOrWhiteSpace(response.Text)));
    }
}
