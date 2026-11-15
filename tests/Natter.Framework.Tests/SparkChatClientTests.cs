using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Natter.Feature.Ai;
using Natter.Feature.Ai.Spark;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class SparkChatClientTests
{
    [Fact]
    public void Spark_manifest_declares_provider_and_shared_responder()
    {
        var manifest = Natter.Feature.Ai.Spark.SparkPlugin.Manifest;
        Assert.Equal("natter.feature.ai.spark", manifest.Id);
        Assert.Contains(manifest.Permissions!, permission => permission == "secrets:SPARK_API_KEY");
        Assert.Contains(manifest.Secrets!, secret => secret.Name == "SPARK_API_KEY" && secret.Required);
        Assert.Contains(
            manifest.Contributions!,
            contribution => contribution.Type == "chat.responder"
                            && contribution.Id == "spark"
                            && contribution.Priority == 20);
    }

    [Fact]
    public async Task Request_uses_openai_compatible_endpoint_and_bearer_password()
    {
        string? body = null;
        string? authorization = null;
        var handler = new RecordingHttpMessageHandler((request, payload) =>
        {
            body = payload;
            authorization = request.Headers.Authorization?.ToString();
            return DeepSeekTestData.Json("""
                {
                  "code": 0,
                  "message": "Success",
                  "choices": [{ "message": { "role": "assistant", "content": "你好" } }],
                  "usage": { "prompt_tokens": 6, "completion_tokens": 4, "total_tokens": 10 }
                }
                """);
        });

        using var http = new HttpClient(handler);
        using var client = new SparkChatClient(
            http,
            new SparkOptions { BaseUrl = "https://spark-api-open.xf-yun.com/v1", Model = "generalv3.5" },
            "api-password");

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal("你好", response.Text);
        Assert.Equal(6, response.Usage?.InputTokenCount);
        Assert.Equal(4, response.Usage?.OutputTokenCount);
        Assert.Equal(10, response.Usage?.TotalTokenCount);
        Assert.Equal("Bearer api-password", authorization);
        Assert.DoesNotContain("api-password", body!, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(body!);
        var root = document.RootElement;
        Assert.Equal("generalv3.5", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal("user", root.GetProperty("messages")[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task Spark_provider_codes_and_http_errors_are_classified()
    {
        using var http = new HttpClient(new RecordingHttpMessageHandler((_, _) =>
            DeepSeekTestData.Json("""{ "code": 10013, "message": "input content audit failed" }""")));
        using var client = new SparkChatClient(
            http,
            new SparkOptions { MaxRetries = 0 },
            "key");

        var contentFilter = await Assert.ThrowsAsync<SparkException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "bad")]));
        Assert.Equal(AiErrorKind.ContentFilter, contentFilter.Kind);

        using var authHttp = new HttpClient(new RecordingHttpMessageHandler((_, _) =>
            DeepSeekTestData.Json("""{ "error": { "message": "invalid user", "type": "api_error" } }""", HttpStatusCode.Unauthorized)));
        using var authClient = new SparkChatClient(authHttp, new SparkOptions { MaxRetries = 0 }, "secret-key");

        var auth = await Assert.ThrowsAsync<SparkException>(
            () => authClient.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));
        Assert.Equal(AiErrorKind.Authentication, auth.Kind);
        Assert.DoesNotContain("secret-key", auth.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transient_5xx_is_retried_and_rate_limit_is_retryable()
    {
        var attempts = 0;
        using var http = new HttpClient(new RecordingHttpMessageHandler((_, _) =>
        {
            attempts++;
            return attempts < 2
                ? DeepSeekTestData.Json("""{ "code": 500, "message": "busy" }""", HttpStatusCode.InternalServerError)
                : DeepSeekTestData.Json("""{ "code": 0, "choices": [{ "message": { "content": "ok" } }] }""");
        }));
        using var client = new SparkChatClient(
            http,
            new SparkOptions { MaxRetries = 2, RetryBaseDelayMs = 1, RetryMaxDelayMs = 5 },
            "key");

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "retry")]);
        Assert.Equal("ok", response.Text);
        Assert.Equal(2, attempts);
    }
}
