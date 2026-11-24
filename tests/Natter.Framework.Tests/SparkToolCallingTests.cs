using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
using Natter.Application.Chat;
using Natter.Cli.Core;
using Natter.Feature.Ai;
using Natter.Feature.Ai.Spark;
using Natter.Infrastructure.Composition;
using Natter.Plugin.Abstractions;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// P5.2c Spark decision: tool calling is not verified for this provider, so the adapter rejects
/// native tools explicitly and the responder declares TextOnly. The task loop therefore uses the
/// text fallback (or answers directly), with no behaviour change for plain chat.
/// </summary>
public sealed class SparkToolCallingTests
{
    private static readonly JsonElement ToolSchema = JsonDocument.Parse(
        """{"type":"object","properties":{"input":{"type":"string"}},"required":["input"]}""").RootElement.Clone();

    [Fact]
    public async Task Spark_client_rejects_native_tools_explicitly()
    {
        var handler = new RecordingHttpMessageHandler((_, _) => DeepSeekTestData.Json("""{"choices":[]}"""));
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            async () => await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "hi")],
                new ChatOptions { Tools = [AIFunctionFactory.CreateDeclaration("clock", "time", ToolSchema)] }));

        Assert.Contains("tools", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Spark_plugin_declares_text_only_and_falls_back_to_the_text_protocol()
    {
        using var workspace = new SparkWorkspace();
        var chat = new CapturingChatClient();
        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddModule<CliCoreCompositionModule>()
            .ConfigureOptions(options => options.PluginConfigDirectory = workspace.ConfigDirectory)
            .ConfigurePlugins(options => options.SecretProvider = workspace.Secrets)
            .AddEmbeddedNpsPlugin(SparkPlugin.Manifest, new SparkPlugin(chat), workspace.PluginDirectory);

        await using var host = builder.Build();
        await host.StartAsync();

        var responders = host.Services.GetRequiredService<IChatResponderRegistry>();
        Assert.True(responders.TryGet($"{SparkPlugin.Manifest.Id}#spark", out var registration));
        var responder = Assert.IsAssignableFrom<AiChatResponder>(registration!.Factory());
        Assert.Equal(ToolProtocolCapabilities.TextOnly, responder.ProtocolCapabilities);
        Assert.False(responder.ProtocolCapabilities.NativeFunctionCalling);

        // Text fallback: no tools are advertised through ChatOptions.
        var reply = await responder.CompleteStepAsync(new TaskModelStep(
            "task-1",
            "hello",
            null,
            [],
            [],
            0));
        Assert.True(reply.Ok);
        Assert.Equal("answer", reply.Text);
        Assert.Null(reply.ToolCall);
        Assert.Single(chat.Options);
        Assert.True(chat.Options[0]?.Tools is null or { Count: 0 });
    }

    [Fact]
    public void Default_profile_is_unchanged()
    {
        var profiles = new RuntimeProfileStore(null);
        var profile = Assert.Single(profiles.Current.Profiles, item => item.Id == "tier1-default");

        Assert.Equal("tier1-default", profiles.Current.DefaultProfile);
        Assert.Equal("host#static", profile.Prompt.Provider);
    }

    private static SparkChatClient CreateClient(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        var options = new SparkOptions
        {
            ApiKey = "${secret:SPARK_API_KEY}",
            BaseUrl = "https://spark.test/v1",
            Model = "spark-test",
            Thinking = "disabled",
            MaxRetries = 0,
            TimeoutSeconds = 5,
        };
        return new SparkChatClient(http, options, "test-key");
    }

    /// <summary>Temp plugin/config layout for the embedded Spark provider host test.</summary>
    private sealed class SparkWorkspace : IDisposable
    {
        public SparkWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "natter-tool-spark-" + Guid.NewGuid().ToString("N"));
            PluginDirectory = Path.Combine(Root, "plugin");
            ConfigDirectory = Path.Combine(Root, "config");
            Directory.CreateDirectory(PluginDirectory);
            Directory.CreateDirectory(ConfigDirectory);

            using (var stream = typeof(SparkPlugin).Assembly.GetManifestResourceStream(
                       "Natter.Feature.Ai.Spark.config.schema.json")!)
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                File.WriteAllText(Path.Combine(PluginDirectory, "config.schema.json"), reader.ReadToEnd());

            const string config = """
                {
                  "apiKey": "${secret:SPARK_API_KEY}",
                  "model": "spark-test",
                  "thinking": "disabled",
                  "tools": []
                }
                """;
            File.WriteAllText(Path.Combine(ConfigDirectory, "natter.feature.ai.spark.json"), config);
            Secrets = new TestSecrets(new Dictionary<string, string> { ["SPARK_API_KEY"] = "test-key" });
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

    private sealed class CapturingChatClient : IChatClient
    {
        private readonly ChatClientMetadata _metadata = new("spark", new Uri("https://example.invalid"), "spark-test");

        public List<ChatOptions?> Options { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Options.Add(options);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType == typeof(ChatClientMetadata) ? _metadata : null;

        public void Dispose()
        {
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
