using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
using Natter.Application.Chat;
using Natter.Cli.Core;
using Natter.Feature.Ai;
using Natter.Feature.Ai.DeepSeek;
using Natter.Infrastructure.Composition;
using Natter.Plugin.Abstractions;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// P5.2c DeepSeek native tool calling: request tools/tool_choice, assistant tool_calls parsing,
/// role=tool/tool_call_id results, thinking/usage preservation, error mapping and the plugin
/// declaring NativeAndText. Real API calls are not made.
/// </summary>
public sealed class DeepSeekToolCallingTests
{
    private static readonly JsonElement ToolSchema = JsonDocument.Parse(
        """{"type":"object","properties":{"input":{"type":"string"}},"required":["input"]}""").RootElement.Clone();

    [Fact]
    public async Task Request_carries_tools_and_tool_choice()
    {
        var handler = new RecordingHttpMessageHandler((_, _) => DeepSeekTestData.Json(TextResponse("ok")));
        using var client = CreateClient(handler);

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "what time is it")],
            new ChatOptions
            {
                Tools = [AIFunctionFactory.CreateDeclaration("clock", "Returns the current time", ToolSchema)],
                ToolMode = ChatToolMode.Auto,
            });

        Assert.Equal("ok", response.Text);
        using var request = JsonDocument.Parse(handler.Requests.Single().Body);
        var root = request.RootElement;
        var tools = root.GetProperty("tools");
        var function = tools[0].GetProperty("function");
        Assert.Equal("clock", function.GetProperty("name").GetString());
        Assert.Equal("object", function.GetProperty("parameters").GetProperty("type").GetString());
        Assert.Equal("auto", root.GetProperty("tool_choice").GetString());
        // Existing thinking/usage behaviour is preserved.
        Assert.Equal("disabled", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.DoesNotContain("test-key", handler.Requests.Single().Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Assistant_tool_calls_are_mapped_to_function_content()
    {
        const string body = """
            {
              "choices": [
                {
                  "message": {
                    "role": "assistant",
                    "content": null,
                    "tool_calls": [
                      {
                        "id": "call-1",
                        "type": "function",
                        "function": { "name": "clock", "arguments": "{\"input\":\"now\"}" }
                      }
                    ]
                  },
                  "finish_reason": "tool_calls"
                }
              ],
              "usage": { "prompt_tokens": 11, "completion_tokens": 3, "total_tokens": 14,
                         "prompt_cache_hit_tokens": 2, "prompt_cache_miss_tokens": 9,
                         "completion_tokens_details": { "reasoning_tokens": 0 } }
            }
            """;
        var handler = new RecordingHttpMessageHandler((_, _) => DeepSeekTestData.Json(body));
        using var client = CreateClient(handler);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "time?")]);

        var call = Assert.Single(response.Messages[0].Contents.OfType<FunctionCallContent>());
        Assert.Equal("call-1", call.CallId);
        Assert.Equal("clock", call.Name);
        Assert.Equal("now", Assert.IsType<JsonElement>(call.Arguments!["input"]).GetString());
        Assert.Equal("tool_calls", response.FinishReason?.Value);
        Assert.Equal(11, response.Usage?.InputTokenCount);
        Assert.Equal(14, response.Usage?.TotalTokenCount);
        Assert.True(response.AdditionalProperties?.ContainsKey("deepseek.usage"));
    }

    [Fact]
    public async Task Tool_results_are_sent_back_as_role_tool_messages()
    {
        var handler = new RecordingHttpMessageHandler((_, _) => DeepSeekTestData.Json(TextResponse("done")));
        using var client = CreateClient(handler);
        var assistantCall = new ChatMessage(
            ChatRole.Assistant,
            new List<AIContent> { new FunctionCallContent("call-1", "clock", new Dictionary<string, object?> { ["input"] = "now" }) });
        var toolResult = new ChatMessage(
            ChatRole.Tool,
            new List<AIContent> { new FunctionResultContent("call-1", "tick") });

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "time?"), assistantCall, toolResult],
            new ChatOptions { Tools = [AIFunctionFactory.CreateDeclaration("clock", "Returns the current time", ToolSchema)] });

        using var request = JsonDocument.Parse(handler.Requests.Single().Body);
        var messages = request.RootElement.GetProperty("messages");
        var assistant = messages[1];
        Assert.Equal("assistant", assistant.GetProperty("role").GetString());
        var toolCall = assistant.GetProperty("tool_calls")[0];
        Assert.Equal("call-1", toolCall.GetProperty("id").GetString());
        Assert.Equal("clock", toolCall.GetProperty("function").GetProperty("name").GetString());
        Assert.Contains("input", toolCall.GetProperty("function").GetProperty("arguments").GetString()!, StringComparison.Ordinal);

        var result = messages[2];
        Assert.Equal("tool", result.GetProperty("role").GetString());
        Assert.Equal("call-1", result.GetProperty("tool_call_id").GetString());
        Assert.Equal("tick", result.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Plain_text_response_is_unchanged_without_tools()
    {
        var handler = new RecordingHttpMessageHandler((_, _) => DeepSeekTestData.Json(TextResponse("hello")));
        using var client = CreateClient(handler);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal("hello", response.Text);
        using var request = JsonDocument.Parse(handler.Requests.Single().Body);
        Assert.False(request.RootElement.TryGetProperty("tools", out _));
        Assert.False(request.RootElement.TryGetProperty("tool_choice", out _));
    }

    [Fact]
    public async Task Provider_error_is_mapped()
    {
        var handler = new RecordingHttpMessageHandler((_, _) => DeepSeekTestData.Json(
            """{"error":{"message":"rate limited","type":"rate_limit"}}""",
            HttpStatusCode.TooManyRequests));
        using var client = CreateClient(handler, maxRetries: 0);

        var exception = await Assert.ThrowsAsync<DeepSeekException>(
            async () => await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        Assert.Equal(DeepSeekErrorKind.RateLimited, exception.Kind);
    }

    [Fact]
    public async Task Provider_timeout_is_mapped()
    {
        var handler = new DelayedHandler(TimeSpan.FromSeconds(3));
        using var client = CreateClient(handler, timeoutSeconds: 1, maxRetries: 0);

        var exception = await Assert.ThrowsAsync<DeepSeekException>(
            async () => await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        Assert.Equal(DeepSeekErrorKind.Timeout, exception.Kind);
    }

    [Fact]
    public async Task Plugin_declares_native_and_text_protocols()
    {
        using var workspace = new PluginWorkspace("deepseek", "DEEPSEEK_API_KEY");
        var chat = new FakeChatClient();
        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddModule<CliCoreCompositionModule>()
            .ConfigureOptions(options => options.PluginConfigDirectory = workspace.ConfigDirectory)
            .ConfigurePlugins(options => options.SecretProvider = workspace.Secrets)
            .AddEmbeddedNpsPlugin(DeepSeekPlugin.Manifest, new DeepSeekPlugin(chat), workspace.PluginDirectory);

        await using var host = builder.Build();
        await host.StartAsync();

        var responders = host.Services.GetRequiredService<IChatResponderRegistry>();
        Assert.True(responders.TryGet($"{DeepSeekPlugin.Manifest.Id}#deepseek", out var registration));
        var responder = Assert.IsAssignableFrom<AiChatResponder>(registration!.Factory());

        Assert.True(responder.ProtocolCapabilities.NativeFunctionCalling);
        Assert.True(responder.ProtocolCapabilities.TextProtocol);
        Assert.Equal(ToolProtocolCapabilities.NativeAndText, responder.ProtocolCapabilities);
    }

    [Fact]
    public void Default_profile_is_unchanged()
    {
        var profiles = new RuntimeProfileStore(null);
        var profile = Assert.Single(profiles.Current.Profiles, item => item.Id == "tier1-default");

        Assert.Equal("tier1-default", profiles.Current.DefaultProfile);
        Assert.Equal("host#static", profile.Prompt.Provider);
    }

    private static string TextResponse(string text)
        => "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":"
           + JsonSerializer.Serialize(text)
           + "},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

    private static DeepSeekChatClient CreateClient(
        HttpMessageHandler handler,
        int maxRetries = 0,
        int timeoutSeconds = 5)
    {
        var http = new HttpClient(handler);
        var options = new DeepSeekOptions
        {
            ApiKey = "${secret:DEEPSEEK_API_KEY}",
            BaseUrl = "https://deepseek.test/v1",
            Model = "deepseek-chat",
            Thinking = "disabled",
            MaxRetries = maxRetries,
            TimeoutSeconds = timeoutSeconds,
        };
        return new DeepSeekChatClient(http, options, DeepSeekApiKey.From("test-key"));
    }

    /// <summary>Temp plugin/config/state layout for embedded-provider host tests.</summary>
    private sealed class PluginWorkspace : IDisposable
    {
        public PluginWorkspace(string provider, string secretName)
        {
            Root = Path.Combine(Path.GetTempPath(), "natter-tool-" + provider + "-" + Guid.NewGuid().ToString("N"));
            PluginDirectory = Path.Combine(Root, "plugin");
            ConfigDirectory = Path.Combine(Root, "config");
            Directory.CreateDirectory(PluginDirectory);
            Directory.CreateDirectory(ConfigDirectory);

            var assembly = provider == "deepseek"
                ? typeof(DeepSeekPlugin).Assembly
                : throw new ArgumentOutOfRangeException(nameof(provider));
            var resource = provider == "deepseek"
                ? "Natter.Feature.Ai.DeepSeek.config.schema.json"
                : "Natter.Feature.Ai.Spark.config.schema.json";
            using (var stream = assembly.GetManifestResourceStream(resource)!)
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                File.WriteAllText(Path.Combine(PluginDirectory, "config.schema.json"), reader.ReadToEnd());

            var configName = provider == "deepseek"
                ? "natter.feature.ai.deepseek.json"
                : "natter.feature.ai.spark.json";
            var config = "{ \"apiKey\": \"${secret:" + secretName
                         + "}\", \"model\": \"test-model\", \"thinking\": \"disabled\", \"tools\": [] }";
            File.WriteAllText(Path.Combine(ConfigDirectory, configName), config);
            Secrets = new TestSecrets(new Dictionary<string, string> { [secretName] = "test-key" });
        }

        public string Root { get; }

        public string PluginDirectory { get; }

        public string ConfigDirectory { get; }

        public ISecretProvider Secrets { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class DelayedHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return DeepSeekTestData.Json(TextResponse("late"));
        }
    }

    private sealed class TestSecrets(IReadOnlyDictionary<string, string> values) : ISecretProvider
    {
        public bool TryGet(string name, out string? value)
        {
            var found = values.TryGetValue(name, out var result);
            value = result;
            return found;
        }
    }
}
