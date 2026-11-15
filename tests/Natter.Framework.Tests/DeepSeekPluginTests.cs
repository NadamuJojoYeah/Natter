using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
using Natter.Chat;
using Natter.Cli.Abstractions;
using Natter.Cli.Core;
using Natter.Feature.Ai.DeepSeek;
using Natter.Infrastructure.Chat;
using Natter.Infrastructure.Composition;
using Natter.Infrastructure.Plugins.Nps;
using Natter.Plugin.Abstractions;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class DeepSeekPluginTests
{
    [Fact]
    public void Manifest_declares_provider_permissions_and_responder_priority()
    {
        var manifest = DeepSeekPlugin.Manifest;

        Assert.Equal("natter.feature.ai.deepseek", manifest.Id);
        Assert.Equal(PluginKind.Feature, manifest.Kind);
        Assert.Contains(manifest.Permissions!, permission => permission == "network");
        Assert.Contains(manifest.Permissions!, permission => permission == "secrets:DEEPSEEK_API_KEY");
        Assert.Contains(manifest.Permissions!, permission => permission == "chat.read");
        Assert.Contains(manifest.Permissions!, permission => permission == "chat.send");
        Assert.Contains(manifest.Permissions!, permission => permission == "chat.interactions");
        Assert.Contains(manifest.Secrets!, secret => secret.Name == "DEEPSEEK_API_KEY" && secret.Required);
        Assert.Contains(
            manifest.Contributions!,
            contribution => contribution.Type == "chat.responder"
                            && contribution.Id == "deepseek"
                            && contribution.Priority == 10);
        Assert.Equal("config.schema.json", manifest.ConfigSchema);
    }

    [Fact]
    public void Config_schema_accepts_thinking_disabled_and_rejects_a_literal_key()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.schema.json"),
            ReadEmbedded("Natter.Feature.Ai.DeepSeek.config.schema.json"));

        var manifest = DeepSeekPlugin.Manifest;
        File.WriteAllText(
            Path.Combine(temp.Path, "config.json"),
            """
            {
              "apiKey": "${secret:DEEPSEEK_API_KEY}",
              "model": "deepseek-flash",
              "thinking": "disabled",
              "reasoningEffort": "none",
              "tools": []
            }
            """);

        var valid = NpsPluginConfigLoader.Load(manifest, temp.Path);
        Assert.True(valid.Valid);
        Assert.Empty(valid.Issues);

        File.WriteAllText(
            Path.Combine(temp.Path, "config.json"),
            """{ "apiKey": "literal-key", "thinking": "disabled" }""");

        var invalid = NpsPluginConfigLoader.Load(manifest, temp.Path);
        Assert.False(invalid.Valid);
        Assert.NotEmpty(invalid.Issues);
    }

    [Fact]
    public async Task DeepSeek_plugin_loads_and_responds_through_the_nps_host_loop()
    {
        using var temp = new TempDirectory();
        var pluginDirectory = temp.Combine("plugin");
        var configDirectory = temp.Combine("config");
        Directory.CreateDirectory(pluginDirectory);
        Directory.CreateDirectory(configDirectory);

        File.WriteAllText(
            Path.Combine(pluginDirectory, "config.schema.json"),
            ReadEmbedded("Natter.Feature.Ai.DeepSeek.config.schema.json"));
        File.WriteAllText(
            Path.Combine(configDirectory, "natter.feature.ai.deepseek.json"),
            """
            {
              "apiKey": "${secret:DEEPSEEK_API_KEY}",
              "model": "deepseek-flash",
              "thinking": "disabled",
              "tools": [],
              "historyMaxMessages": 4,
              "fallbackText": "unavailable"
            }
            """);

        var chat = new FakeChatClient();
        var secrets = new TestSecrets(new Dictionary<string, string>
        {
            ["DEEPSEEK_API_KEY"] = "test-key",
        });

        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddModule<CliCoreCompositionModule>()
            .ConfigureOptions(options => options.PluginConfigDirectory = configDirectory)
            .ConfigurePlugins(options => options.SecretProvider = secrets)
            .AddEmbeddedNpsPlugin(DeepSeekPlugin.Manifest, new DeepSeekPlugin(chat), pluginDirectory);

        await using var host = builder.Build();
        await host.StartAsync();

        var catalog = host.Services.GetRequiredService<IPluginCatalog>();
        var descriptor = Assert.Single(catalog.Plugins);
        Assert.True(
            descriptor.State == PluginState.Activated,
            $"state={descriptor.State} error={descriptor.Error} phase={descriptor.ErrorDetail?.Phase}");

        var source = ChatTestData.Source();
        var upstream = new InMemoryChatUpstream(source, Capabilities.TextOnly, new TestClock());
        host.Services.GetRequiredService<IChatUpstreamRegistry>().Register(upstream);
        await upstream.ConnectAsync();

        var conversation = ChatTestData.Conversation("dm", ConversationKind.Direct);
        host.Services.GetRequiredService<IChatEventPublisher>().Publish(ChatTestData.CreatedEvent(
            source,
            ChatTestData.Message(source, id: "m1", conversation: conversation, text: "hello"),
            "evt:ai-1"));

        var sent = await WaitForAsync(() => upstream.Messages.Count > 0 ? upstream.Messages : null);
        var reply = Assert.Single(sent);
        Assert.Equal("answer", reply.Content.Text);
        Assert.Equal("m1", reply.ReplyTo);
        Assert.DoesNotContain("test-key", reply.Content.Text);

        var commands = host.Services.GetRequiredService<CliCommandRegistry>();
        Assert.True(commands.TryGet("ai status", out var statusCommand));
        var console = new StringConsole();
        var exitCode = await statusCommand.HandlerFactory(host.Services).ExecuteAsync(new CliInvocation(
            "ai status",
            new Dictionary<string, string?> { ["json"] = null },
            [],
            host.Services,
            console));
        Assert.Equal(0, exitCode);
        Assert.Contains("\"provider\":\"deepseek\"", console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("test-key", console.Output, StringComparison.Ordinal);

        await host.StopAsync();
    }

    private static async Task<T> WaitForAsync<T>(Func<T?> probe) where T : class
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (probe() is { } value)
                return value;

            await Task.Delay(20);
        }

        throw new TimeoutException("Timed out waiting for the AI response.");
    }

    private static string ReadEmbedded(string name)
    {
        using var stream = typeof(DeepSeekPlugin).Assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"Missing resource {name}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
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

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "natter-ai-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
