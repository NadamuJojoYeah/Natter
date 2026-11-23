using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Cli.Abstractions;
using Natter.Cli.Core;
using Natter.Infrastructure.Composition;
using Natter.Plugin.Abstractions;
using Xunit;

namespace Natter.Plugin.PromptLibrary.Tests;

/// <summary>
/// End-to-end P2 check: the Prompt Library plugin is staged as a real NPS package (<c>lib/</c>
/// layout), loaded through the plugin ALC, registers <c>natter.feature.prompt.library#default</c> and the
/// <c>prompt explain</c> command, and compiles the library preset as configured.
/// </summary>
public sealed class PromptLibraryNpsIntegrationTests
{
    [Fact]
    public async Task Prompt_library_plugin_loads_as_nps_package_and_compiles_a_profile_prompt()
    {
        var root = FindRepositoryRoot();
        var sourcePluginDirectory = Path.Combine(root, "plugins", "Natter.Plugin.PromptLibrary");
        var directory = Path.Combine(Path.GetTempPath(), "natter-prompt-library-nps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "lib"));
        Directory.CreateDirectory(Path.Combine(directory, "prompts"));

        try
        {
            var assembly = typeof(PromptLibraryPlugin).Assembly.Location;
            File.Copy(assembly, Path.Combine(directory, "lib", Path.GetFileName(assembly)));
            foreach (var file in new[] { "plugin.json", "config.schema.json", "presets.extra.json" })
                File.Copy(Path.Combine(sourcePluginDirectory, file), Path.Combine(directory, file));
            File.Copy(
                Path.Combine(sourcePluginDirectory, "config.sample.json"),
                Path.Combine(directory, "config.json"));
            foreach (var prompt in Directory.GetFiles(Path.Combine(sourcePluginDirectory, "prompts")))
                File.Copy(prompt, Path.Combine(directory, "prompts", Path.GetFileName(prompt)));

            var builder = NatterApplicationBuilder.Create()
                .AddNatterFramework()
                .AddModule<CliCoreCompositionModule>()
                .AddPluginDirectory(directory);

            await using var host = builder.Build();
            await host.StartAsync();

            var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);
            Assert.Equal(PluginState.Activated, descriptor.State);

            var components = host.Services.GetRequiredService<IContextComponentCatalog>();
            Assert.True(components.TryGetPrompt("natter.feature.prompt.library#default", out var promptProvider));

            var bundle = await promptProvider!.GetAsync(new PromptRequest(
                new RuntimeProfile
                {
                    Id = "tier1-default",
                    Prompt = new ProfilePrompt { Provider = "natter.feature.prompt.library#default" },
                },
                Source.Create("discord", "discord", "bot"),
                new Conversation("c1", ConversationKind.Direct),
                new User("u1", "Alice"),
                Locale: null,
                Variables: new Dictionary<string, string>()));

            Assert.Equal("tier1-default", bundle.Plan!.PresetId);
            Assert.Contains("你是 Natter", bundle.SystemPrompt);
            Assert.DoesNotContain("untrusted data", bundle.SystemPrompt, StringComparison.Ordinal);

            var registry = host.Services.GetRequiredService<CliCommandRegistry>();
            Assert.True(registry.TryGet("prompt explain", out var command));

            var console = new StringConsole();
            var handler = command.HandlerFactory(host.Services);
            var exitCode = await handler.ExecuteAsync(new CliInvocation(
                command.Name,
                new Dictionary<string, string?>(),
                [],
                host.Services,
                console));

            Assert.Equal(CliExitCodes.Success, exitCode);
            Assert.Contains("preset=tier1-default", console.Output);
            Assert.DoesNotContain("先回答结论", console.Output);

            await host.StopAsync();
            Assert.False(components.TryGetPrompt("natter.feature.prompt.library#default", out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Natter.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed class StringConsole : IConsole
    {
        private readonly System.Text.StringBuilder _buffer = new();

        public StringConsole()
        {
            Out = new StringWriter(_buffer);
            Error = new StringWriter(_buffer);
        }

        public TextWriter Out { get; }

        public TextWriter Error { get; }

        public bool IsInteractive => false;

        public string Output => _buffer.ToString();

        public string? ReadLine() => null;
    }
}
