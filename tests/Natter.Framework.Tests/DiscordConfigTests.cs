using System.Text;
using Natter.Infrastructure.Plugins.Nps;
using Natter.Plugin.Abstractions;
using Natter.Sample.Plugin.Discord;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class DiscordConfigTests
{
    [Fact]
    public void Sample_config_schema_accepts_the_documented_secret_only_configuration()
    {
        using var temp = new TempDirectory();
        var schema = ReadEmbedded("Natter.Sample.Plugin.Discord.config.schema.json");
        File.WriteAllText(Path.Combine(temp.Path, "config.schema.json"), schema);

        var manifest = new PluginManifest
        {
            SchemaVersion = 1,
            Id = "natter.transport.messaging.discord",
            Name = "Discord",
            Version = "0.1.0",
            Kind = PluginKind.Transport,
            HostApi = ">=0.1.0 <1.0.0",
            ConfigSchema = "config.schema.json",
            Secrets = [new PluginSecret { Name = "DISCORD_BOT_TOKEN", Required = true }],
        };

        File.WriteAllText(
            Path.Combine(temp.Path, "config.json"),
            """
            {
              "token": "${secret:DISCORD_BOT_TOKEN}",
              "selfId": "123456789012345678",
              "intents": ["Guilds", "GuildMessages", "DirectMessages", "MessageContent"],
              "guilds": ["123456789012345678"],
              "requireMention": true,
              "slashCommands": false,
              "maxMessageLength": 1900
            }
            """);

        var result = NpsPluginConfigLoader.Load(manifest, temp.Path);
        Assert.Empty(result.Issues);
        Assert.True(result.Valid);
        Assert.Equal("123456789012345678", result.Effective.GetProperty("selfId").GetString());
    }

    [Fact]
    public void Sample_config_schema_rejects_a_literal_token_and_missing_self_id()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.schema.json"),
            ReadEmbedded("Natter.Sample.Plugin.Discord.config.schema.json"));

        var manifest = new PluginManifest
        {
            SchemaVersion = 1,
            Id = "natter.transport.messaging.discord",
            Name = "Discord",
            Version = "0.1.0",
            Kind = PluginKind.Transport,
            HostApi = ">=0.1.0 <1.0.0",
            ConfigSchema = "config.schema.json",
        };

        File.WriteAllText(
            Path.Combine(temp.Path, "config.json"),
            """
            {
              "token": "literal-token",
              "intents": ["Guilds"]
            }
            """);

        var result = NpsPluginConfigLoader.Load(manifest, temp.Path);
        Assert.NotEmpty(result.Issues);
    }

    [Fact]
    public void Embedded_manifest_has_the_required_discord_permissions_and_secret()
    {
        var manifest = DiscordPlugin.Manifest;
        Assert.Contains(manifest.Permissions!, permission => permission == "network");
        Assert.Contains(manifest.Permissions!, permission => permission == "chat.read");
        Assert.Contains(manifest.Permissions!, permission => permission == "chat.send");
        Assert.Contains(manifest.Permissions!, permission => permission == "chat.edit");
        Assert.Contains(manifest.Permissions!, permission => permission == "chat.delete");
        Assert.Contains(manifest.Permissions!, permission => permission == "chat.commands");
        Assert.Contains(manifest.Permissions!, permission => permission == "chat.interactions");
        Assert.Contains(manifest.Secrets!, secret => secret.Name == "DISCORD_BOT_TOKEN" && secret.Required);
        Assert.Equal("config.schema.json", manifest.ConfigSchema);
    }

    private static string ReadEmbedded(string name)
    {
        using var stream = typeof(DiscordPlugin).Assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"Missing resource {name}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "natter-discord-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

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
