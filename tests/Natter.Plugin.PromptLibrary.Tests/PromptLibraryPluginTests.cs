using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Cli.Abstractions;
using Natter.Plugin.Abstractions;
using Xunit;

namespace Natter.Plugin.PromptLibrary.Tests;

public sealed class PromptLibraryPluginTests
{
    private static string SampleConfigJson()
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config.sample.json"));

    private static (TestPluginContext Context, TestContributionRegistry Registry, IServiceCollection Services, TestLog Log)
        CreateContext(string configJson)
    {
        var services = new ServiceCollection();
        var log = new TestLog();
        var registry = new TestContributionRegistry(services);
        var context = new TestPluginContext
        {
            Manifest = PromptLibraryPlugin.Manifest,
            Config = new TestPluginConfig(configJson),
            Log = log,
            Services = new TestServices(services),
            Contributions = registry,
        };
        return (context, registry, services, log);
    }

    private static PromptRequest Request()
        => new(
            new RuntimeProfile
            {
                Id = "tier1-default",
                Prompt = new ProfilePrompt { Provider = "natter.prompt.library#default" },
            },
            Source.Create("discord", "discord", "bot"),
            new Conversation("c1", ConversationKind.Direct),
            new User("u1", "Alice"),
            Locale: null,
            Variables: new Dictionary<string, string>());

    [Fact]
    public void Manifest_declares_feature_kind_prompt_provider_and_explain_command()
    {
        var manifest = PromptLibraryPlugin.Manifest;

        Assert.Equal("natter.prompt.library", manifest.Id);
        Assert.Equal(PluginKind.Feature, manifest.Kind);
        Assert.NotNull(manifest.FindContribution(PluginContributionTypes.PromptProvider, "default"));
        Assert.NotNull(manifest.FindContribution(PluginContributionTypes.CliCommand, "prompt.explain"));
        Assert.Contains(PluginPermissions.FileSystemRead, manifest.Permissions!);
        Assert.Contains(PluginPermissions.CliCommands, manifest.Permissions!);
        Assert.Equal("config.schema.json", manifest.ConfigSchema);
        Assert.NotNull(manifest.ConfigSchema);
    }

    [Fact]
    public void Checked_in_manifest_is_valid_nps()
    {
        var read = PluginManifestReader.Read(AppContext.BaseDirectory);

        Assert.True(read.IsValid, string.Join("; ", read.Errors.Select(error => error.Path + " " + error.Message)));
        Assert.NotNull(read.Manifest);
        Assert.Equal(PromptLibraryPlugin.Manifest.Id, read.Manifest!.Id);
        Assert.Equal(PluginKind.Feature, read.Manifest.Kind);
    }

    [Fact]
    public async Task Configure_stages_prompt_provider_and_cli_command()
    {
        var (context, registry, services, log) = CreateContext(SampleConfigJson());

        await new PromptLibraryPlugin().ConfigureAsync(context);

        Assert.Contains(("prompt.provider", "default"), registry.Registrations);
        Assert.Contains(("cli.command", "prompt.explain"), registry.Registrations);
        var command = Assert.Single(registry.Commands);
        Assert.Equal("prompt explain", command.Name);
        Assert.Contains("json", command.Options.Select(option => option.Name));
        Assert.Contains(log.Messages, message => message.Contains("fragments=4", StringComparison.Ordinal));

        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<PromptLibraryRuntime>();
        Assert.Equal("natter.prompt.library", runtime.PluginId);
        var promptProvider = provider.GetRequiredService<PromptLibraryProvider>();
        var bundle = await promptProvider.GetAsync(Request());
        Assert.Contains("你是 Natter", bundle.SystemPrompt);
        Assert.EndsWith(HostPromptSafety.MandatoryText, bundle.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explain_command_prints_recipe_metadata_without_prompt_text()
    {
        var (context, registry, services, _) = CreateContext(SampleConfigJson());
        await new PromptLibraryPlugin().ConfigureAsync(context);
        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<PromptLibraryRuntime>();
        var console = new TestConsole();
        var command = Assert.Single(registry.Commands);
        var serviceProvider = new TestServiceProvider().Add(runtime);
        var handler = command.HandlerFactory(serviceProvider);

        await handler.ExecuteAsync(new CliInvocation(
            command.Name,
            new Dictionary<string, string?> { ["json"] = "false" },
            [],
            serviceProvider,
            console));

        var output = console.Out.ToString();
        Assert.Contains("preset=tier1-default", output);
        Assert.Contains("hash=sha256:", output);
        Assert.Contains("legacy-style", output);
        Assert.DoesNotContain("先回答结论", output);
        Assert.DoesNotContain("旧版风格片段", output);
    }

    [Fact]
    public async Task Explain_command_json_output_contains_ids_and_hash_only()
    {
        var (context, registry, services, _) = CreateContext(SampleConfigJson());
        await new PromptLibraryPlugin().ConfigureAsync(context);
        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<PromptLibraryRuntime>();
        var console = new TestConsole();
        var command = Assert.Single(registry.Commands);
        var serviceProvider = new TestServiceProvider().Add(runtime);
        var handler = command.HandlerFactory(serviceProvider);

        await handler.ExecuteAsync(new CliInvocation(
            command.Name,
            new Dictionary<string, string?> { ["json"] = "true" },
            [],
            serviceProvider,
            console));

        using var document = JsonDocument.Parse(console.Out.ToString());
        var root = document.RootElement;
        Assert.Equal("tier1-default", root.GetProperty("presetId").GetString());
        Assert.StartsWith("sha256:", root.GetProperty("hash").GetString(), StringComparison.Ordinal);
        Assert.Contains("persona", root.GetProperty("fragments").EnumerateArray().Select(item => item.GetString()));
        var disabled = root.GetProperty("disabled").EnumerateArray().Single();
        Assert.Equal("legacy-style", disabled.GetProperty("id").GetString());
        Assert.False(root.TryGetProperty("text", out _));
        Assert.DoesNotContain("先回答结论", console.Out.ToString());
    }

    [Fact]
    public async Task Configure_rejects_plugin_mandatory_fragment()
    {
        var (context, _, _, _) = CreateContext("""
        {
          "fragments": [
            { "id": "x", "text": "x", "mandatory": true }
          ]
        }
        """);

        var exception = await Assert.ThrowsAsync<PromptLibraryStartupException>(
            async () => await new PromptLibraryPlugin().ConfigureAsync(context));
        Assert.Equal(PromptLibraryCodes.FragmentMandatory, exception.Code);
    }

    [Fact]
    public async Task Configure_rejects_empty_library()
    {
        var (context, _, _, _) = CreateContext("{}");

        var exception = await Assert.ThrowsAsync<PromptLibraryStartupException>(
            async () => await new PromptLibraryPlugin().ConfigureAsync(context));
        Assert.Equal(PromptLibraryCodes.NotConfigured, exception.Code);
    }

    [Fact]
    public async Task Configure_wraps_invalid_config_types()
    {
        var (context, _, _, _) = CreateContext("""{ "fragments": "not-an-array" }""");

        var exception = await Assert.ThrowsAsync<PromptLibraryStartupException>(
            async () => await new PromptLibraryPlugin().ConfigureAsync(context));
        Assert.Equal(PromptLibraryCodes.ConfigInvalid, exception.Code);
    }

    [Fact]
    public async Task Configure_rejects_missing_fragment_files_with_a_code()
    {
        var (context, _, _, _) = CreateContext("""
        {
          "fragments": [
            { "id": "persona", "file": "does-not-exist.md" }
          ]
        }
        """);

        var exception = await Assert.ThrowsAsync<PromptLibraryStartupException>(
            async () => await new PromptLibraryPlugin().ConfigureAsync(context));
        Assert.Equal(PromptLibraryCodes.FragmentMissing, exception.Code);
    }
}
