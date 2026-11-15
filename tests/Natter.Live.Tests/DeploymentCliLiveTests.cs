using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Natter.Live.Tests;

/// <summary>
/// Layer 4: the shipped CLI process, plugin staging layout and secret provider. Nothing is
/// loaded in-process here: the test checks what a real operator sees when the built artifacts
/// are dropped into a plugin directory and started with environment-provided secrets.
/// </summary>
[Trait("Category", "Live")]
public sealed class DeploymentCliLiveTests(ITestOutputHelper output)
{
    private static readonly LiveSettings Settings = LiveSettings.Current;
    private static readonly TimeSpan CliTimeout = TimeSpan.FromSeconds(120);

    private static Dictionary<string, string?> RealSecrets => new(StringComparer.Ordinal)
    {
        ["DISCORD_BOT_TOKEN"] = Settings.BotToken,
        ["DEEPSEEK_API_KEY"] = Settings.ApiKey,
    };

    [DiscordLiveFact(DisplayName = "CLI: both file-system plugins load from the staged layout")]
    public async Task Plugins_load_from_the_file_layout()
    {
        LiveDeployment.Prepare(LiveDeployment.PluginRoot, output);

        var (exitCode, stdout, stderr) = await LiveDeployment.RunCliAsync(
            LiveDeployment.PluginRoot,
            RealSecrets,
            CliTimeout,
            "plugins", "list", "--json");

        var text = LiveDeployment.WithoutSecrets(stdout + stderr, Settings.BotToken, Settings.ApiKey);
        output.WriteLine(LiveDeployment.Tail(text));

        Assert.Equal(0, exitCode);

        using var document = JsonDocument.Parse(stdout);
        var plugins = document.RootElement.EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("id").GetString()!,
                item => item,
                StringComparer.Ordinal);

        foreach (var id in new[] { "natter.transport.discord", "natter.feature.ai.deepseek" })
        {
            Assert.True(plugins.ContainsKey(id), $"Plugin '{id}' was not discovered. Output: {LiveDeployment.Tail(text)}");
            var state = plugins[id].GetProperty("state").GetString();
            var error = plugins[id].GetProperty("error").ValueKind == JsonValueKind.Null
                ? null
                : plugins[id].GetProperty("error").GetString();
            output.WriteLine($"{id} state={state} error={error}");
            Assert.Equal("activated", state);
        }
    }

    [DiscordLiveFact(DisplayName = "CLI: upstreams reports a connected real Discord gateway")]
    public async Task Upstreams_reports_connected_gateway()
    {
        LiveDeployment.Prepare(LiveDeployment.PluginRoot, output);

        var (exitCode, stdout, stderr) = await LiveDeployment.RunCliAsync(
            LiveDeployment.PluginRoot,
            RealSecrets,
            CliTimeout,
            "upstreams", "--json");

        var text = LiveDeployment.WithoutSecrets(stdout + stderr, Settings.BotToken, Settings.ApiKey);
        output.WriteLine(LiveDeployment.Tail(text));
        Assert.Equal(0, exitCode);

        using var document = JsonDocument.Parse(stdout);
        var upstream = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("discord", upstream.GetProperty("source").GetProperty("platform").GetString());
        Assert.Equal(Settings.BotUserId, upstream.GetProperty("source").GetProperty("selfId").GetString());
        Assert.True(upstream.GetProperty("connected").GetBoolean());
    }

    [DiscordLiveFact(DisplayName = "CLI: discord status reports the real guild and capability set")]
    public async Task Discord_status_reports_real_metrics()
    {
        LiveDeployment.Prepare(LiveDeployment.PluginRoot, output);

        var (exitCode, stdout, stderr) = await LiveDeployment.RunCliAsync(
            LiveDeployment.PluginRoot,
            RealSecrets,
            CliTimeout,
            "discord", "status", "--json");

        var text = LiveDeployment.WithoutSecrets(stdout + stderr, Settings.BotToken, Settings.ApiKey);
        output.WriteLine(LiveDeployment.Tail(text));
        Assert.Equal(0, exitCode);

        using var document = JsonDocument.Parse(stdout);
        var status = document.RootElement;
        output.WriteLine(
            $"connected={status.GetProperty("connected")} guilds={status.GetProperty("guildCount")} "
            + $"channels={status.GetProperty("channelCount")} latency={status.GetProperty("latencyMs")}");

        Assert.True(status.GetProperty("connected").GetBoolean());
        Assert.True(status.GetProperty("guildCount").GetInt32() >= 1);
        Assert.True(status.GetProperty("latencyMs").GetInt32() >= 0);
        Assert.True(status.GetProperty("capabilities").GetProperty("edit").GetBoolean());
        Assert.True(status.GetProperty("capabilities").GetProperty("delete").GetBoolean());
    }

    [DeepSeekLiveFact(DisplayName = "CLI: ai status reports the real provider without a network call")]
    public async Task Ai_status_reports_provider()
    {
        LiveDeployment.Prepare(LiveDeployment.PluginRoot, output);

        var (exitCode, stdout, stderr) = await LiveDeployment.RunCliAsync(
            LiveDeployment.PluginRoot,
            RealSecrets,
            CliTimeout,
            "ai", "status", "--json");

        var text = LiveDeployment.WithoutSecrets(stdout + stderr, Settings.BotToken, Settings.ApiKey);
        output.WriteLine(LiveDeployment.Tail(text));
        Assert.Equal(0, exitCode);

        using var document = JsonDocument.Parse(stdout);
        Assert.Equal("deepseek", document.RootElement.GetProperty("provider").GetString());
        Assert.Equal("deepseek-flash", document.RootElement.GetProperty("model").GetString());
        Assert.Equal("disabled", document.RootElement.GetProperty("thinking").GetString());
        Assert.DoesNotContain(Settings.ApiKey, stdout, StringComparison.Ordinal);
    }

    [DiscordLiveFact(DisplayName = "CLI: missing secrets fail the plugins with a clear diagnostic, not a crash")]
    public async Task Missing_secrets_fail_plugins_with_a_diagnostic()
    {
        LiveDeployment.Prepare(LiveDeployment.PluginRoot, output);

        var (exitCode, stdout, stderr) = await LiveDeployment.RunCliAsync(
            LiveDeployment.PluginRoot,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["DISCORD_BOT_TOKEN"] = null,
                ["DEEPSEEK_API_KEY"] = null,
            },
            CliTimeout,
            "plugins", "list", "--detail");

        output.WriteLine(LiveDeployment.Tail(stdout + stderr));

        Assert.Equal(0, exitCode);
        Assert.Contains("missing-secret", stdout, StringComparison.Ordinal);
        Assert.Contains("Required secret 'DISCORD_BOT_TOKEN'", stdout, StringComparison.Ordinal);
        Assert.Contains("Required secret 'DEEPSEEK_API_KEY'", stdout, StringComparison.Ordinal);
    }
}
