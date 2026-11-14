using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Natter.Chat;
using Natter.Cli.Abstractions;
using Natter.Cli.Core;
using Natter.Infrastructure.Chat;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class ChatCliTests
{
    [Fact]
    public async Task Upstreams_json_reports_sources_and_capabilities()
    {
        var clock = new TestClock();
        var source = ChatTestData.Source(platform: "inmemory", selfId: "bot-9");
        var registry = new ChatUpstreamRegistry();
        registry.Register(new InMemoryChatUpstream(source, Capabilities.TextOnly with { Edit = true }, clock));

        var console = new StringConsole();
        var exitCode = await NatterCli.RunAsync(
            ["upstreams", "--json"],
            console,
            NoPlugins(),
            builder => builder.ConfigureServices(services => services.AddSingleton<IChatUpstreamRegistry>(registry)));

        Assert.Equal(CliExitCodes.Success, exitCode);

        using var document = JsonDocument.Parse(console.Output);
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.Equal("satori", root[0].GetProperty("source").GetProperty("protocol").GetString());
        Assert.Equal("inmemory", root[0].GetProperty("source").GetProperty("platform").GetString());
        Assert.Equal("bot-9", root[0].GetProperty("source").GetProperty("selfId").GetString());
        Assert.True(root[0].GetProperty("capabilities").GetProperty("edit").GetBoolean());
    }

    [Fact]
    public async Task Upstreams_reports_an_empty_registry_without_failing()
    {
        var console = new StringConsole();

        var exitCode = await NatterCli.RunAsync(["upstreams"], console, NoPlugins());

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("No NLMP chat upstreams registered", console.Output);
    }

    [Fact]
    public async Task Satori_status_previews_capabilities_and_routes_as_json()
    {
        var console = new StringConsole();

        var exitCode = await NatterCli.RunAsync(
            ["satori", "status", "--json", "--features", "message.update,message.delete,guild.plain"],
            console,
            NoPlugins());

        Assert.Equal(CliExitCodes.Success, exitCode);

        using var document = JsonDocument.Parse(console.Output);
        var root = document.RootElement;

        Assert.Equal("satori", root.GetProperty("protocolId").GetString());
        Assert.Equal("0.1", root.GetProperty("nlmpVersion").GetString());
        Assert.True(root.GetProperty("capabilities").GetProperty("edit").GetBoolean());
        Assert.True(root.GetProperty("capabilities").GetProperty("delete").GetBoolean());
        Assert.False(root.GetProperty("capabilities").GetProperty("markdown").GetBoolean());

        var supported = root.GetProperty("supportedCommandTypes")
            .EnumerateArray()
            .Select(element => element.GetString())
            .ToList();

        Assert.Contains(ChatCommandTypes.MessageSend, supported);
        Assert.Contains(ChatCommandTypes.MessageUpdate, supported);
        Assert.Contains(ChatCommandTypes.MessageDelete, supported);
    }

    [Fact]
    public async Task Satori_status_text_lists_routes()
    {
        var console = new StringConsole();

        var exitCode = await NatterCli.RunAsync(["satori", "status"], console, NoPlugins());

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("/v1/message.create", console.Output);
        Assert.Contains("markdown", console.Output);
    }

    [Fact]
    public async Task Doctor_exposes_nlmp_summary()
    {
        var console = new StringConsole();

        var exitCode = await NatterCli.RunAsync(["doctor", "--json"], console, NoPlugins());

        Assert.Equal(CliExitCodes.Success, exitCode);

        using var document = JsonDocument.Parse(console.Output);
        var chat = document.RootElement.GetProperty("chat");

        Assert.Equal(0, chat.GetProperty("upstreams").GetInt32());
        Assert.True(chat.TryGetProperty("eventsPublished", out _));
        Assert.True(chat.TryGetProperty("subscribers", out _));
    }

    private static NatterCliOptions NoPlugins() => new() { LoadPlugins = false };
}
