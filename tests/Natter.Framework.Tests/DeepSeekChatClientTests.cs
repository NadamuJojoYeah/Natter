using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Natter.Feature.Ai.DeepSeek;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class DeepSeekChatClientTests
{
    [Fact]
    public async Task Request_disables_thinking_by_default_and_parses_content_and_usage()
    {
        string? capturedBody = null;
        string? authorization = null;
        var handler = new RecordingHttpMessageHandler((request, body) =>
        {
            capturedBody = body;
            authorization = request.Headers.Authorization?.ToString();
            return DeepSeekTestData.Json("""
                {
                  "choices": [{
                    "message": { "content": "hello", "reasoning_content": "hidden chain" },
                    "finish_reason": "stop"
                  }],
                  "usage": {
                    "prompt_tokens": 10,
                    "completion_tokens": 5,
                    "total_tokens": 15,
                    "prompt_cache_hit_tokens": 4,
                    "prompt_cache_miss_tokens": 6,
                    "completion_token_details": { "reasoning_tokens": 2 }
                  }
                }
                """);
        });

        var options = new DeepSeekOptions { Model = "deepseek-flash", TimeoutSeconds = 5 };
        using var http = new HttpClient(handler);
        using var client = new DeepSeekChatClient(http, options, DeepSeekApiKey.From("super-secret"));

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal("hello", response.Text);
        Assert.DoesNotContain("hidden chain", response.Text, StringComparison.Ordinal);
        Assert.Equal(10, response.Usage?.InputTokenCount);
        Assert.Equal(5, response.Usage?.OutputTokenCount);
        Assert.Equal(4, response.Usage?.CachedInputTokenCount);
        Assert.NotNull(response.AdditionalProperties);
        Assert.True(response.AdditionalProperties!.ContainsKey("deepseek.usage"));
        Assert.DoesNotContain("super-secret", capturedBody!, StringComparison.Ordinal);
        Assert.Equal("Bearer super-secret", authorization);

        using var bodyDocument = JsonDocument.Parse(capturedBody!);
        var root = bodyDocument.RootElement;
        Assert.Equal("deepseek-flash", root.GetProperty("model").GetString());
        Assert.Equal("disabled", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("reasoning_effort", out _));
        Assert.True(root.TryGetProperty("temperature", out _));
        Assert.Equal(1024, root.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task Enabled_thinking_sets_reasoning_effort_and_omits_temperature()
    {
        string? capturedBody = null;
        var handler = new RecordingHttpMessageHandler((_, body) =>
        {
            capturedBody = body;
            return DeepSeekTestData.Json("""{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}]}""");
        });

        var options = new DeepSeekOptions
        {
            Thinking = "enabled",
            ReasoningEffort = "high",
            TimeoutSeconds = 5,
        };

        using var http = new HttpClient(handler);
        using var client = new DeepSeekChatClient(http, options, DeepSeekApiKey.From("key"));
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "think")]);

        using var document = JsonDocument.Parse(capturedBody!);
        var root = document.RootElement;
        Assert.Equal("enabled", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("high", root.GetProperty("reasoning_effort").GetString());
        Assert.False(root.TryGetProperty("temperature", out _));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, DeepSeekErrorKind.Authentication)]
    [InlineData(HttpStatusCode.PaymentRequired, DeepSeekErrorKind.InsufficientBalance)]
    [InlineData(HttpStatusCode.TooManyRequests, DeepSeekErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, DeepSeekErrorKind.Unavailable)]
    public async Task Http_errors_are_mapped_and_do_not_expose_the_api_key(
        HttpStatusCode status,
        DeepSeekErrorKind expectedKind)
    {
        var handler = new RecordingHttpMessageHandler((_, _) =>
            DeepSeekTestData.Json("""{"error":{"message":"provider detail","code":"some_code"}}""", status));

        using var http = new HttpClient(handler);
        using var client = new DeepSeekChatClient(
            http,
            new DeepSeekOptions { TimeoutSeconds = 5 },
            DeepSeekApiKey.From("do-not-leak"));

        var exception = await Assert.ThrowsAsync<DeepSeekException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        Assert.Equal(expectedKind, exception.Kind);
        Assert.DoesNotContain("do-not-leak", exception.ToString(), StringComparison.Ordinal);
        Assert.Equal((int)status, exception.StatusCode);
    }

    [Fact]
    public async Task Timeout_is_reported_as_a_structured_deepseek_error()
    {
        using var http = new HttpClient(new DelayedHandler(TimeSpan.FromSeconds(3)));
        using var client = new DeepSeekChatClient(
            http,
            new DeepSeekOptions { TimeoutSeconds = 1 },
            DeepSeekApiKey.From("key"));

        var exception = await Assert.ThrowsAsync<DeepSeekException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        Assert.Equal(DeepSeekErrorKind.Timeout, exception.Kind);
        Assert.True(exception.Retryable);
    }

    private sealed class DelayedHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return DeepSeekTestData.Json("""{"choices":[{"message":{"content":"late"}}]}""");
        }
    }

    [Fact]
    public void Metadata_and_unknown_service_resolution_are_safe()
    {
        using var http = new HttpClient(new RecordingHttpMessageHandler((_, _) =>
            DeepSeekTestData.Json("""{"choices":[{"message":{"content":"ok"}}]}""")));
        using var client = new DeepSeekChatClient(
            http,
            new DeepSeekOptions { Model = "deepseek-flash" },
            DeepSeekApiKey.From("key"));

        Assert.Equal("deepseek-flash", client.Metadata.DefaultModelId);
        Assert.Same(client, client.GetService(typeof(IChatClient)));
        Assert.Null(client.GetService(typeof(string)));
    }
}
