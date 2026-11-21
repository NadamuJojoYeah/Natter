using System.Text.Json;
using Natter.Plugin.Abstractions;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class P5SchemaTests
{
    [Fact]
    public void Discord_schema_accepts_all_task_commands()
    {
        using var schema = ReadSchema();
        using var config = JsonDocument.Parse("""
            {
              "token": "${secret:DISCORD_BOT_TOKEN}",
              "selfId": "123456789012345678",
              "commands": {
                "ask": true,
                "remember": true,
                "forget": true,
                "memory": true,
                "reset": true,
                "task": true,
                "status": true,
                "tasks": true,
                "cancel": true
              }
            }
            """);

        Assert.Empty(PluginConfigSchema.Validate(schema.RootElement, config.RootElement));
    }

    [Fact]
    public void Discord_schema_rejects_unknown_command()
    {
        using var schema = ReadSchema();
        using var config = JsonDocument.Parse("""
            {
              "token": "${secret:DISCORD_BOT_TOKEN}",
              "selfId": "123456789012345678",
              "commands": { "task": true, "unknown": true }
            }
            """);

        Assert.Contains(
            PluginConfigSchema.Validate(schema.RootElement, config.RootElement),
            issue => issue.Pointer.Contains("unknown", StringComparison.Ordinal));
    }

    private static JsonDocument ReadSchema()
    {
        var assembly = typeof(Natter.Sample.Plugin.Discord.DiscordPlugin).Assembly;
        using var stream = assembly.GetManifestResourceStream("Natter.Sample.Plugin.Discord.config.schema.json")
            ?? throw new InvalidOperationException("Discord schema resource is missing.");
        return JsonDocument.Parse(stream);
    }
}
