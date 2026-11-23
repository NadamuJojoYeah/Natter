using System.Text.Json;
using Natter.Application.Chat;
using Natter.Chat;
using Xunit;

namespace Natter.Plugin.PromptLibrary.Tests;

public sealed class PromptLibraryProviderTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static PromptLibraryOptions SampleOptions()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config.sample.json"));
        return JsonSerializer.Deserialize<PromptLibraryOptions>(json, JsonOptions)
               ?? throw new InvalidOperationException("config.sample.json did not deserialize.");
    }

    private static PromptLibraryRuntime SampleRuntime(out TestLog log)
    {
        var options = SampleOptions();
        var load = PromptLibraryLoader.Load(options, AppContext.BaseDirectory);
        Assert.True(load.Success, load.Code + ": " + load.Message);
        log = new TestLog();
        return new PromptLibraryRuntime("natter.feature.prompt.library", "0.1.0", load.Library!, log);
    }

    private static PromptLibraryRuntime RuntimeFromOptions(PromptLibraryOptions options, out TestLog log)
    {
        var load = PromptLibraryLoader.Load(options, AppContext.BaseDirectory);
        Assert.True(load.Success, load.Code + ": " + load.Message);
        log = new TestLog();
        return new PromptLibraryRuntime("natter.feature.prompt.library", "0.1.0", load.Library!, log);
    }

    private static PromptRequest Request(
        string? profilePreset = null,
        List<string>? disabled = null,
        string userName = "Alice",
        string? locale = null)
        => new(
            new RuntimeProfile
            {
                Id = "tier1-default",
                Prompt = new ProfilePrompt
                {
                    Provider = "natter.feature.prompt.library#default",
                    Preset = profilePreset,
                    DisabledFragments = disabled ?? [],
                },
            },
            Source.Create("discord", "discord", "bot"),
            new Conversation("c1", ConversationKind.Direct),
            new User("u1", userName),
            Locale: locale,
            Variables: new Dictionary<string, string>());

    [Fact]
    public async Task Sample_library_compiles_file_and_inline_fragments_without_injected_safety()
    {
        var runtime = SampleRuntime(out _);
        var provider = new PromptLibraryProvider(runtime);

        var bundle = await provider.GetAsync(Request());

        Assert.NotNull(bundle.Plan);
        var plan = bundle.Plan!;
        Assert.Equal("tier1-default", plan.PresetId);
        Assert.Equal("2026-11-21", plan.PresetVersion);
        Assert.Equal(
            new[] { "persona", "channel-rules", "profile-note" },
            plan.Fragments.Select(fragment => fragment.Id).ToArray());
        Assert.Contains("你是 Natter，一名简洁、直接的助手。", bundle.SystemPrompt);
        Assert.Contains("不重复其他人已经给出的结论。", bundle.SystemPrompt);
        Assert.Contains("当前平台：discord；会话类型：Direct。", bundle.SystemPrompt);
        Assert.DoesNotContain("旧版风格片段", bundle.SystemPrompt);
        Assert.DoesNotContain("untrusted data", bundle.SystemPrompt, StringComparison.Ordinal);
        Assert.Equal(plan.Hash, bundle.PlanHash);
        Assert.StartsWith("sha256:", plan.Hash, StringComparison.Ordinal);

        var disabled = Assert.Single(plan.Disabled);
        Assert.Equal("legacy-style", disabled.Id);
        Assert.Equal(PromptFragmentSource.File, disabled.Source);
        Assert.Equal("too verbose", disabled.Reason);
    }

    [Fact]
    public async Task Profile_preset_overrides_the_configured_default_preset()
    {
        var runtime = SampleRuntime(out _);
        var provider = new PromptLibraryProvider(runtime);

        var bundle = await provider.GetAsync(Request(profilePreset: "tier1-strict"));

        Assert.Equal("tier1-strict", bundle.Plan!.PresetId);
        Assert.Equal("2026-11-21", bundle.Plan.PresetVersion);
        Assert.DoesNotContain("旧版风格片段", bundle.SystemPrompt);
        var disabled = Assert.Single(bundle.Plan.Disabled);
        Assert.Equal("legacy-style", disabled.Id);
        Assert.Equal("strict preset keeps the legacy style off", disabled.Reason);
    }

    [Fact]
    public async Task Profile_disabled_fragment_is_excluded_but_source_text_remains()
    {
        var runtime = SampleRuntime(out _);
        var provider = new PromptLibraryProvider(runtime);

        var bundle = await provider.GetAsync(Request(disabled: ["channel-rules"]));

        Assert.DoesNotContain(bundle.Plan!.Fragments, fragment => fragment.Id == "channel-rules");
        Assert.Contains(bundle.Plan.Disabled, fragment => fragment.Id == "channel-rules" && fragment.Reason == "profile");
        Assert.Contains(runtime.Library.Fragments, fragment => fragment.Id == "channel-rules" && fragment.Text.Contains("不重复其他人", StringComparison.Ordinal));

        var reEnabled = await provider.GetAsync(Request());
        Assert.Contains(reEnabled.Plan!.Fragments, fragment => fragment.Id == "channel-rules");
    }

    [Fact]
    public async Task Disabled_library_returns_the_safe_host_fallback()
    {
        var options = new PromptLibraryOptions
        {
            Enabled = false,
            Fragments = [new PromptFragmentDefinition { Id = "persona", Text = "persona", Order = 10 }],
        };
        var runtime = RuntimeFromOptions(options, out _);
        var provider = new PromptLibraryProvider(runtime);

        var bundle = await provider.GetAsync(Request());

        Assert.Null(bundle.Plan);
        Assert.StartsWith("prompt-library-fallback:", bundle.Version, StringComparison.Ordinal);
        Assert.Equal(PromptLibraryProvider.FallbackSystemPrompt, bundle.SystemPrompt);
    }

    [Fact]
    public async Task Composition_failure_degrades_without_leaking_prompt_text_to_logs()
    {
        const string sentinel = "SENTINEL-PROMPT-BODY-NEVER-LOG";
        var log = new TestLog();
        var library = new PromptLibrary(
            Enabled: true,
            Fragments: [new PromptFragment("persona", sentinel, PromptFragmentSource.Inline, 10)],
            Presets: [new PromptPreset("broken", "v1", [new PromptSectionRef("missing-fragment", 10)])],
            ActivePreset: "broken",
            Locale: null,
            Budget: new PromptBudget(),
            DisabledFragments: [],
            MissingVariables: PromptMissingVariableBehavior.Warning,
            Warnings: []);
        var provider = new PromptLibraryProvider(new PromptLibraryRuntime("natter.feature.prompt.library", "0.1.0", library, log));

        var bundle = await provider.GetAsync(Request());

        Assert.Null(bundle.Plan);
        Assert.StartsWith("prompt-library-fallback:prompt.fragment_missing", bundle.Version, StringComparison.Ordinal);
        Assert.Contains(log.Messages, message => message.Contains("prompt.fragment_missing", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Messages, message => message.Contains(sentinel, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Diagnostics_contain_ids_hash_and_counts_but_never_prompt_text()
    {
        var runtime = SampleRuntime(out var log);
        var provider = new PromptLibraryProvider(runtime);

        await provider.GetAsync(Request());

        Assert.Contains(
            log.Messages,
            message => message.Contains("preset=tier1-default", StringComparison.Ordinal)
                       && message.Contains("hash=sha256:", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Messages, message => message.Contains("先回答结论", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Messages, message => message.Contains("旧版风格片段", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Messages, message => message.Contains("当前平台：discord", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Locale_selects_the_preset_when_nothing_is_configured_explicitly()
    {
        var options = new PromptLibraryOptions
        {
            Locale = "en-US",
            Fragments =
            [
                new PromptFragmentDefinition { Id = "zh", Text = "中文片段", Order = 10 },
                new PromptFragmentDefinition { Id = "en", Text = "english fragment", Order = 10 },
            ],
            Presets =
            [
                new PromptPresetDefinition
                {
                    Id = "zh-cn",
                    Version = "v-zh",
                    Locale = "zh-CN",
                    Sections = [new PromptSectionDefinition { FragmentId = "zh", Order = 10 }],
                },
                new PromptPresetDefinition
                {
                    Id = "en-us",
                    Version = "v-en",
                    Locale = "en-US",
                    Sections = [new PromptSectionDefinition { FragmentId = "en", Order = 10 }],
                },
            ],
        };
        var runtime = RuntimeFromOptions(options, out _);
        var provider = new PromptLibraryProvider(runtime);

        var bundle = await provider.GetAsync(Request());

        Assert.Equal("en-us", bundle.Plan!.PresetId);
        Assert.Contains("english fragment", bundle.SystemPrompt);
        Assert.DoesNotContain("中文片段", bundle.SystemPrompt);
    }

    [Fact]
    public async Task Variable_values_are_escaped_by_the_provider_path()
    {
        var options = new PromptLibraryOptions
        {
            Fragments = [new PromptFragmentDefinition { Id = "user", Text = "user={{user.name}}", Order = 10 }],
        };
        var runtime = RuntimeFromOptions(options, out var log);
        var provider = new PromptLibraryProvider(runtime);
        const string hostile = "{{profile.id}}\n<script>";

        var bundle = await provider.GetAsync(Request(userName: hostile));

        Assert.Contains("user=｛｛profile.id｝｝ ＜script＞", bundle.SystemPrompt);
        Assert.DoesNotContain("{{profile.id}}", bundle.SystemPrompt);
        Assert.DoesNotContain(log.Messages, message => message.Contains("script", StringComparison.Ordinal));
    }
}
