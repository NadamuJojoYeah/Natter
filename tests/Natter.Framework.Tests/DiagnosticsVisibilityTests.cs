using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Natter.Application.Chat;
using Natter.Cli.Abstractions;
using Natter.Feature.Ai.DeepSeek;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>D-01: ai status must show effective parameters and where they came from.</summary>
public sealed class DiagnosticsVisibilityTests
{
    [Fact]
    public async Task Ai_status_lists_effective_parameters_with_default_sources()
    {
        var text = await Status(new DeepSeekOptions());
        foreach (var name in new[] { "maxOutputTokens", "maxInputCharacters", "temperature", "timeoutSeconds", "maxRetries" })
        {
            Assert.Contains($"{name}: ", text, StringComparison.Ordinal);
            Assert.Contains($"{name}: ", text, StringComparison.Ordinal);
        }

        Assert.Contains("maxOutputTokens: 4096 (default)", text, StringComparison.Ordinal);
        Assert.Contains("maxInputCharacters: 12000 (default)", text, StringComparison.Ordinal);
        Assert.Contains("temperature: 0.7 (default)", text, StringComparison.Ordinal);
        Assert.Contains("timeoutSeconds: 60 (default)", text, StringComparison.Ordinal);
        Assert.Contains("maxRetries: 2 (default)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ai_status_marks_config_overrides()
    {
        var text = await Status(new DeepSeekOptions
        {
            MaxOutputTokens = 400,
            MaxInputCharacters = 5000,
            Temperature = 0.2,
            TimeoutSeconds = 15,
            MaxRetries = 0,
        });

        Assert.Contains("maxOutputTokens: 400 (config)", text, StringComparison.Ordinal);
        Assert.Contains("maxInputCharacters: 5000 (config)", text, StringComparison.Ordinal);
        Assert.Contains("temperature: 0.2 (config)", text, StringComparison.Ordinal);
        Assert.Contains("timeoutSeconds: 15 (config)", text, StringComparison.Ordinal);
        Assert.Contains("maxRetries: 0 (config)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ai_status_json_contains_effective_parameters()
    {
        var handler = new DeepSeekStatusCommandHandler(
            new DeepSeekOptions { MaxOutputTokens = 400 },
            new DeepSeekMetrics(),
            new InMemoryConversationStore());
        var console = new StringConsole();
        var invocation = new CliInvocation(
            "ai status",
            new Dictionary<string, string?> { ["json"] = "true" },
            [],
            new ServiceCollection().BuildServiceProvider(),
            console);
        await handler.ExecuteAsync(invocation);

        using var document = JsonDocument.Parse(console.OutWriter.ToString());
        var parameters = document.RootElement.GetProperty("effective");
        Assert.Contains(parameters.EnumerateArray(), item =>
            item.GetProperty("name").GetString() == "maxOutputTokens"
            && item.GetProperty("value").GetString() == "400"
            && item.GetProperty("source").GetString() == "config");
    }

    private static async Task<string> Status(DeepSeekOptions options)
    {
        var handler = new DeepSeekStatusCommandHandler(options, new DeepSeekMetrics(), new InMemoryConversationStore());
        var console = new StringConsole();
        await handler.ExecuteAsync(new CliInvocation(
            "ai status",
            new Dictionary<string, string?>(),
            [],
            new ServiceCollection().BuildServiceProvider(),
            console));
        return console.OutWriter.ToString();
    }
}
