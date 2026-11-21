using System.Text.Json;
using Microsoft.Extensions.AI;
using Natter.Application.Chat;
using Natter.Chat;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// Prompt Compiler P1 contract tests: fragment enable/disable, mandatory host safety, stable
/// ordering, preset inheritance/locale selection, whitelisted variables, budgets, plan hash and
/// the no-regression guarantee for the default <c>host#static</c> profile.
/// </summary>
public sealed class PromptCompilerTests
{
    private static RuntimeProfile Profile(
        string id = "tier1-default",
        string? preset = null,
        string? locale = null,
        List<string>? disabled = null,
        string? systemPrompt = "You are Natter, a concise assistant.",
        string version = "default")
        => new()
        {
            Id = id,
            Prompt = new ProfilePrompt
            {
                Provider = "host#static",
                Preset = preset,
                Locale = locale,
                DisabledFragments = disabled ?? [],
                SystemPrompt = systemPrompt,
                Version = version,
            },
        };

    private static PromptRequest Request(RuntimeProfile profile, IReadOnlyDictionary<string, string>? variables = null)
        => new(
            profile,
            Source.Create("discord", "discord", "bot"),
            new Conversation("c1", ConversationKind.Direct),
            new User("u1", "Alice"),
            Locale: null,
            Variables: variables ?? new Dictionary<string, string>());

    private static PromptFragment Fragment(
        string id,
        string text,
        int order = 10,
        bool enabledByDefault = true,
        bool canDisable = true,
        bool mandatory = false,
        int maxCharacters = 8000,
        PromptFragmentSource source = PromptFragmentSource.Inline)
        => new(id, text, source, order, enabledByDefault, canDisable, mandatory, maxCharacters);

    private static PromptPreset Preset(params PromptSectionRef[] sections)
        => new("default", "v1", sections);

    private static PromptPlan Compose(
        IReadOnlyList<PromptFragment> fragments,
        IReadOnlyList<PromptPreset>? presets = null,
        string? presetId = null,
        PromptBudget? budget = null,
        RuntimeProfile? profile = null,
        IReadOnlyList<string>? disabled = null,
        IReadOnlyDictionary<string, string>? variables = null,
        PromptMissingVariableBehavior missing = PromptMissingVariableBehavior.Warning,
        string? locale = null,
        string? userName = "Alice")
        => new PromptComposer().Compose(new PromptComposeRequest(
            profile ?? Profile(),
            presets ?? [Preset(fragments.Select(fragment => new PromptSectionRef(fragment.Id, fragment.Order)).ToArray())],
            fragments,
            PresetId: presetId,
            Budget: budget,
            Locale: locale,
            Platform: "discord",
            Protocol: "discord",
            ConversationKind: "Direct",
            UserName: userName,
            Variables: variables,
            DisabledFragmentIds: disabled,
            MissingVariables: missing));

    [Fact]
    public async Task Host_static_prompt_compiles_to_the_legacy_text_plus_mandatory_safety()
    {
        var profile = Profile(systemPrompt: "system prompt", version: "2026-11-21");
        var bundle = await new HostStaticPromptProvider().GetAsync(Request(profile));

        Assert.Equal(
            "system prompt" + HostPromptSafety.Suffix,
            bundle.SystemPrompt);
        Assert.Equal("2026-11-21", bundle.Version);
        Assert.True(bundle.SafetyAppended);
        Assert.NotNull(bundle.PlanHash);
        Assert.Equal(PromptCompilerDefaults.StaticPresetId, bundle.Plan!.PresetId);
        Assert.Equal(2, bundle.Sections!.Count);
        Assert.Equal(HostPromptSafety.MandatoryFragmentId, bundle.Sections[^1].Id);
    }

    [Fact]
    public async Task Host_static_does_not_truncate_a_long_profile_prompt()
    {
        var longPrompt = new string('x', 10000);
        var bundle = await new HostStaticPromptProvider().GetAsync(Request(Profile(systemPrompt: longPrompt)));

        Assert.Equal(longPrompt, bundle.Plan!.Fragments[0].Text);
        Assert.True(bundle.SystemPrompt.Length > 10000, $"length={bundle.SystemPrompt.Length}");
    }

    [Fact]
    public void Disabling_a_fragment_keeps_the_source_text_and_records_diagnostics_only()
    {
        const string sentinel = "SOFTWARE-SECRET-TEXT-可以关闭但不删除";
        var fragments = new[]
        {
            Fragment("persona", sentinel, order: 10),
            Fragment("style", "answer briefly", order: 20),
        };
        var preset = Preset(
            new PromptSectionRef("persona", 10),
            new PromptSectionRef("style", 20, Enabled: false, Reason: "too verbose"));

        var plan = Compose(fragments, [preset]);

        Assert.Contains(plan.Fragments, fragment => fragment.Id == "persona");
        Assert.DoesNotContain(plan.Fragments, fragment => fragment.Id == "style");
        var disabled = Assert.Single(plan.Disabled);
        Assert.Equal("style", disabled.Id);
        Assert.Equal(PromptFragmentSource.Inline, disabled.Source);
        Assert.Equal("too verbose", disabled.Reason);
        Assert.Equal(PromptHasher.ShortHash("answer briefly"), disabled.Hash);
        // Neither the preset nor the plan deletes source text: re-enabling needs no re-read.
        Assert.Contains(fragments, fragment => fragment.Text == sentinel);
        Assert.Equal(sentinel, plan.Fragments.Single(fragment => fragment.Id == "persona").Text);
    }

    [Fact]
    public void Re_enabling_restores_the_fragment_and_the_original_hash()
    {
        var fragments = new[]
        {
            Fragment("persona", "persona text"),
            Fragment("style", "style text", order: 20),
        };
        var enabled = Compose(fragments, [Preset(
            new PromptSectionRef("persona", 10),
            new PromptSectionRef("style", 20, Enabled: true))]);
        var disabledPreset = Compose(fragments, [Preset(
            new PromptSectionRef("persona", 10),
            new PromptSectionRef("style", 20, Enabled: false))]);

        Assert.Contains("style text", enabled.Fragments.Select(fragment => fragment.Text));
        Assert.DoesNotContain(disabledPreset.Fragments, fragment => fragment.Id == "style");
        Assert.NotEqual(enabled.Hash, disabledPreset.Hash);

        var reEnabled = Compose(fragments, [Preset(
            new PromptSectionRef("persona", 10),
            new PromptSectionRef("style", 20, Enabled: true))]);
        Assert.Equal(enabled.Hash, reEnabled.Hash);
    }

    [Fact]
    public void Enabled_by_default_false_is_a_disable_that_changes_the_hash()
    {
        var fragments = new[]
        {
            Fragment("persona", "persona", order: 10),
            Fragment("legacy", "legacy style", order: 20, enabledByDefault: false),
        };
        var preset = Preset(
            new PromptSectionRef("persona", 10),
            new PromptSectionRef("legacy", 20));

        var disabledPlan = Compose(fragments, [preset]);
        Assert.DoesNotContain(disabledPlan.Fragments, fragment => fragment.Id == "legacy");
        var disabled = Assert.Single(disabledPlan.Disabled);
        Assert.Equal("legacy", disabled.Id);
        Assert.Equal("default", disabled.Reason);

        var enabledPlan = Compose(
            [fragments[0], fragments[1] with { EnabledByDefault = true }],
            [preset]);
        Assert.Contains(enabledPlan.Fragments, fragment => fragment.Id == "legacy");
        Assert.NotEqual(disabledPlan.Hash, enabledPlan.Hash);
    }

    [Fact]
    public void Mandatory_host_fragment_cannot_be_disabled_by_preset_or_profile()
    {
        var fragments = new[] { Fragment("style", "style") };
        var preset = Preset(
            new PromptSectionRef("style", 10),
            new PromptSectionRef(HostPromptSafety.MandatoryFragmentId, Enabled: false));

        var plan = Compose(
            fragments,
            [preset],
            disabled: [HostPromptSafety.MandatoryFragmentId]);

        var mandatory = plan.Fragments[^1];
        Assert.Equal(HostPromptSafety.MandatoryFragmentId, mandatory.Id);
        Assert.Equal(HostPromptSafety.MandatoryText, mandatory.Text);
        Assert.Contains(plan.Warnings, warning => warning.StartsWith(PromptCompilerCodes.MandatoryIgnored, StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Disabled, fragment => fragment.Id == HostPromptSafety.MandatoryFragmentId);
    }

    [Fact]
    public void Mandatory_safety_is_appended_even_when_the_preset_omits_it()
    {
        var plan = Compose([Fragment("style", "style", order: 1)]);

        Assert.Equal(HostPromptSafety.MandatoryFragmentId, plan.Fragments[^1].Id);
        Assert.Equal(2, plan.Fragments.Count);
    }

    [Fact]
    public void Can_disable_false_fragment_cannot_be_disabled()
    {
        var fragments = new[] { Fragment("locked", "locked text", canDisable: false) };
        var error = Assert.Throws<PromptCompilationException>(() => Compose(
            fragments,
            [Preset(new PromptSectionRef("locked", 10, Enabled: false))]));

        Assert.Equal(PromptCompilerCodes.FragmentNotDisableable, error.Code);
    }

    [Fact]
    public void Profile_level_disable_is_honored()
    {
        var fragments = new[] { Fragment("style", "style", order: 10), Fragment("extra", "extra", order: 20) };
        var plan = Compose(fragments, disabled: ["extra"]);

        Assert.DoesNotContain(plan.Fragments, fragment => fragment.Id == "extra");
        var disabled = Assert.Single(plan.Disabled);
        Assert.Equal("extra", disabled.Id);
        Assert.Equal("profile", disabled.Reason);
    }

    [Fact]
    public void Duplicate_fragment_id_fails()
    {
        var error = Assert.Throws<PromptCompilationException>(() => Compose(
            [Fragment("dup", "a", 10), Fragment("dup", "b", 20)]));
        Assert.Equal(PromptCompilerCodes.FragmentDuplicate, error.Code);
    }

    [Fact]
    public void Missing_fragment_fails()
    {
        var error = Assert.Throws<PromptCompilationException>(() => Compose(
            [Fragment("style", "style")],
            [Preset(new PromptSectionRef("nope", 10))]));
        Assert.Equal(PromptCompilerCodes.FragmentMissing, error.Code);
    }

    [Fact]
    public void Duplicate_preset_id_fails()
    {
        var error = Assert.Throws<PromptCompilationException>(() => Compose(
            [Fragment("style", "style")],
            [Preset(new PromptSectionRef("style")), Preset(new PromptSectionRef("style"))]));
        Assert.Equal(PromptCompilerCodes.PresetDuplicate, error.Code);
    }

    [Fact]
    public void Missing_requested_preset_fails()
    {
        var error = Assert.Throws<PromptCompilationException>(() => Compose(
            [Fragment("style", "style")],
            [Preset(new PromptSectionRef("style"))],
            profile: Profile(preset: "does-not-exist")));
        Assert.Equal(PromptCompilerCodes.PresetMissing, error.Code);
    }

    [Fact]
    public void Preset_inheritance_merges_base_sections_and_lets_the_child_override()
    {
        var fragments = new[]
        {
            Fragment("persona", "base persona", order: 10),
            Fragment("style", "base style", order: 20),
            Fragment("channel", "child channel", order: 30),
        };
        var presets = new[]
        {
            new PromptPreset("base", "v1", [new PromptSectionRef("persona", 10), new PromptSectionRef("style", 20)],
                Variables: new Dictionary<string, string> { ["platform"] = "base-platform" }),
            new PromptPreset("child", "v2", [new PromptSectionRef("style", 5), new PromptSectionRef("channel", 30)],
                Extends: "base",
                Variables: new Dictionary<string, string> { ["platform"] = "child-platform" }),
        };

        var plan = Compose(fragments, presets, presetId: "child");

        Assert.Equal("child", plan.PresetId);
        Assert.Equal("v2", plan.PresetVersion);
        Assert.Equal(
            new[] { "style", "persona", "channel", HostPromptSafety.MandatoryFragmentId },
            plan.Fragments.Select(fragment => fragment.Id).ToArray());
    }

    [Fact]
    public void Preset_inheritance_cycle_fails()
    {
        var presets = new[]
        {
            new PromptPreset("a", "v1", [], Extends: "b"),
            new PromptPreset("b", "v1", [], Extends: "a"),
        };
        var error = Assert.Throws<PromptCompilationException>(() =>
            Compose([Fragment("style", "style")], presets, presetId: "a"));
        Assert.Equal(PromptCompilerCodes.PresetCycle, error.Code);
    }

    [Fact]
    public void Preset_inheritance_with_missing_base_fails()
    {
        var presets = new[] { new PromptPreset("a", "v1", [], Extends: "nope") };
        var error = Assert.Throws<PromptCompilationException>(() =>
            Compose([Fragment("style", "style")], presets, presetId: "a"));
        Assert.Equal(PromptCompilerCodes.PresetMissing, error.Code);
    }

    [Fact]
    public void Sort_is_stable_by_order_then_fragment_id_with_mandatory_last()
    {
        var fragments = new[]
        {
            Fragment("b", "b", order: 10),
            Fragment("a", "a", order: 10),
            Fragment("first", "first", order: 1),
        };
        var plan = Compose(fragments);

        Assert.Equal(
            ["first", "a", "b", HostPromptSafety.MandatoryFragmentId],
            plan.Fragments.Select(fragment => fragment.Id).ToArray());
    }

    [Fact]
    public void Locale_selects_the_matching_preset()
    {
        var fragments = new[] { Fragment("zh", "zh text", 10), Fragment("en", "en text", 10) };
        var presets = new[]
        {
            new PromptPreset("zh-cn", "v-zh", [new PromptSectionRef("zh", 10)], Locale: "zh-CN"),
            new PromptPreset("en-us", "v-en", [new PromptSectionRef("en", 10)], Locale: "en-US"),
        };

        var zh = Compose(fragments, presets, locale: "zh-CN");
        var zhLanguage = Compose(fragments, presets, locale: "zh");
        var en = Compose(fragments, presets, locale: "en-US");

        Assert.Equal("zh-cn", zh.PresetId);
        Assert.Equal("zh-cn", zhLanguage.PresetId);
        Assert.Equal("en-us", en.PresetId);
    }

    [Fact]
    public void Profile_preset_selection_wins_over_locale()
    {
        var fragments = new[] { Fragment("zh", "zh text", 10), Fragment("en", "en text", 10) };
        var presets = new[]
        {
            new PromptPreset("zh-cn", "v-zh", [new PromptSectionRef("zh", 10)], Locale: "zh-CN"),
            new PromptPreset("en-us", "v-en", [new PromptSectionRef("en", 10)], Locale: "en-US"),
        };

        var plan = Compose(fragments, presets, profile: Profile(preset: "en-us"), locale: "zh-CN");
        Assert.Equal("en-us", plan.PresetId);
    }

    [Fact]
    public void Builtin_fallback_preset_is_used_when_no_preset_is_declared()
    {
        var plan = Compose(
            [Fragment("a", "a", 10), Fragment("b", "b", 20)],
            presets: []);

        Assert.Equal(PromptCompilerDefaults.FallbackPresetId, plan.PresetId);
        Assert.Equal("fallback", plan.PresetVersion);
        Assert.Equal(
            new[] { "a", "b", HostPromptSafety.MandatoryFragmentId },
            plan.Fragments.Select(fragment => fragment.Id).ToArray());
    }

    [Fact]
    public void Whitelisted_variables_are_substituted_from_runtime_facts()
    {
        var plan = Compose(
            [Fragment("v", "profile={{profile.id}} platform={{platform}} locale={{locale}} kind={{conversation.kind}} user={{user.name}}", 10)],
            locale: "zh-CN");

        var text = plan.Fragments[0].Text;
        Assert.Equal("profile=tier1-default platform=discord locale=zh-CN kind=Direct user=Alice", text);
    }

    [Fact]
    public void Variable_values_are_escaped_and_cannot_re_expand()
    {
        var plan = Compose(
            [Fragment("v", "user={{user.name}}", 10)],
            userName: "{{profile.id}}\n<script>");

        var text = plan.Fragments[0].Text;
        Assert.Equal("user=｛｛profile.id｝｝ ＜script＞", text);
        Assert.DoesNotContain("{{profile.id}}", text);
        Assert.DoesNotContain("\n", text);
    }

    [Fact]
    public void Unknown_variables_stay_literal_with_a_warning()
    {
        var plan = Compose([Fragment("v", "mail={{user.email}}", 10)]);

        Assert.Equal("mail={{user.email}}", plan.Fragments[0].Text);
        Assert.Contains($"{PromptCompilerCodes.VariableNotAllowed}:user.email", plan.Warnings);
    }

    [Fact]
    public void Missing_variable_warns_by_default_and_can_fail_in_strict_mode()
    {
        var fragments = new[] { Fragment("v", "locale={{locale}}", 10) };
        var plan = Compose(fragments, locale: null);
        Assert.Equal("locale=", plan.Fragments[0].Text);
        Assert.Contains($"{PromptCompilerCodes.VariableMissing}:locale", plan.Warnings);

        var error = Assert.Throws<PromptCompilationException>(() =>
            Compose(fragments, locale: null, missing: PromptMissingVariableBehavior.Error));
        Assert.Equal(PromptCompilerCodes.VariableMissing, error.Code);
    }

    [Fact]
    public void Preset_variables_are_available_as_defaults()
    {
        var preset = new PromptPreset("default", "v1", [new PromptSectionRef("v", 10)],
            Variables: new Dictionary<string, string> { ["locale"] = "zh-CN" });
        var plan = Compose([Fragment("v", "locale={{locale}}", 10)], [preset]);

        Assert.Equal("locale=zh-CN", plan.Fragments[0].Text);
    }

    [Fact]
    public void Hash_uses_variable_names_not_values()
    {
        var fragments = new[] { Fragment("v", "user={{user.name}}", 10) };
        var alice = Compose(fragments, userName: "Alice");
        var bob = Compose(fragments, userName: "Bob");

        Assert.Equal(alice.Hash, bob.Hash);

        var twoVariables = Compose([Fragment("v", "user={{user.name}} platform={{platform}}", 10)], userName: "Alice");
        Assert.NotEqual(alice.Hash, twoVariables.Hash);
    }

    [Fact]
    public void Per_fragment_limit_truncates_with_a_warning()
    {
        var plan = Compose([Fragment("long", new string('x', 40), 10, maxCharacters: 10)]);

        Assert.Equal(10, plan.Fragments[0].Text.Length);
        Assert.Contains($"{PromptCompilerCodes.FragmentTruncated}:long", plan.Warnings);
    }

    [Fact]
    public void Total_budget_keeps_mandatory_safety_and_truncates_optional_fragments()
    {
        var plan = Compose(
            [Fragment("big", new string('x', 4000), 10)],
            budget: new PromptBudget(MaxCharacters: 200, MaxFragmentCharacters: 4000));

        var bundle = new PromptComposer().ToBundle(plan);
        Assert.True(bundle.SystemPrompt.Length <= 200, $"length={bundle.SystemPrompt.Length}");
        Assert.EndsWith(HostPromptSafety.MandatoryText, bundle.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains(plan.Warnings, warning => warning.StartsWith(PromptCompilerCodes.BudgetExceeded, StringComparison.Ordinal));
    }

    [Fact]
    public void Plugin_fragments_cannot_be_mandatory_or_impersonate_host_builtins()
    {
        var mandatory = Assert.Throws<PromptCompilationException>(() => Compose(
            [Fragment("plugin.mandatory", "x", 10, mandatory: true, source: PromptFragmentSource.Plugin)]));
        Assert.Equal(PromptCompilerCodes.PluginMandatoryForbidden, mandatory.Code);

        var impersonation = Assert.Throws<PromptCompilationException>(() => Compose(
            [Fragment("plugin.host", "x", 10, source: PromptFragmentSource.HostBuiltin)]));
        Assert.Equal(PromptCompilerCodes.FragmentSourceForbidden, impersonation.Code);

        var reserved = Assert.Throws<PromptCompilationException>(() => Compose(
            [Fragment(HostPromptSafety.MandatoryFragmentId, "spoofed", 10, source: PromptFragmentSource.Plugin)]));
        Assert.Equal(PromptCompilerCodes.FragmentReserved, reserved.Code);
    }

    [Fact]
    public void Fragment_merge_enforces_plugin_permissions_and_limits()
    {
        var host = new[] { Fragment("host.a", "a", 10) };
        var plugin = new[] { Fragment("plugin.b", "b", 20, source: PromptFragmentSource.Plugin) };
        var merged = PromptFragmentMerge.Merge(host, plugin);
        Assert.Equal(new[] { "host.a", "plugin.b" }, merged.Select(fragment => fragment.Id).ToArray());

        var mandatory = Assert.Throws<PromptCompilationException>(() => PromptFragmentMerge.Merge(
            host,
            [Fragment("plugin.m", "m", mandatory: true, source: PromptFragmentSource.Plugin)]));
        Assert.Equal(PromptCompilerCodes.PluginMandatoryForbidden, mandatory.Code);

        var duplicate = Assert.Throws<PromptCompilationException>(() => PromptFragmentMerge.Merge(
            host,
            [Fragment("host.a", "overwrite", source: PromptFragmentSource.Plugin)]));
        Assert.Equal(PromptCompilerCodes.FragmentDuplicate, duplicate.Code);

        var tooMany = Enumerable.Range(0, PromptCompilerDefaults.PluginMaxFragments + 1)
            .Select(index => Fragment($"plugin.{index}", "x", source: PromptFragmentSource.Plugin))
            .ToArray();
        var limit = Assert.Throws<PromptCompilationException>(() => PromptFragmentMerge.Merge(host, tooMany));
        Assert.Equal(PromptCompilerCodes.PluginFragmentLimit, limit.Code);
    }

    [Fact]
    public void Plan_explain_contains_no_prompt_text_or_variable_values()
    {
        const string body = "PROMPT-BODY-SHOULD-NEVER-BE-LOGGED";
        var plan = Compose([Fragment("v", body + " {{user.name}}", 10)], userName: "Alice-Secret");

        Assert.Contains(plan.Hash, plan.Explain);
        Assert.Contains("fragments=2", plan.Explain);
        Assert.DoesNotContain(body, plan.Explain);
        Assert.DoesNotContain("Alice-Secret", plan.Explain);
    }

    [Fact]
    public async Task Context_assembler_appends_safety_once_for_compiled_prompts()
    {
        var profile = Profile(systemPrompt: "system prompt");
        var prompt = await new HostStaticPromptProvider().GetAsync(Request(profile));
        var bundle = await new HostRecentWindowContextAssembler().AssembleAsync(new ContextRequest(
            profile,
            prompt,
            Source.Create("discord", "discord", "bot"),
            new Conversation("c1", ConversationKind.Direct),
            new User("u1", "Alice"),
            "hello",
            [],
            [],
            null,
            new ContextBudget(4000, 8, 1200, 20, 24000)));

        var system = bundle.Messages[0].Text!;
        Assert.Equal(1, CountOccurrences(system, HostPromptSafety.MandatoryText));
        Assert.Equal(prompt.PlanHash, bundle.Diagnostics.PromptPlanHash);
    }

    [Fact]
    public async Task Legacy_prompt_bundle_still_receives_mandatory_safety()
    {
        var profile = Profile();
        var bundle = await new HostRecentWindowContextAssembler().AssembleAsync(new ContextRequest(
            profile,
            new PromptBundle("legacy", null, "1"),
            Source.Create("discord", "discord", "bot"),
            new Conversation("c1", ConversationKind.Direct),
            new User("u1", "Alice"),
            "hello",
            [],
            [],
            null,
            new ContextBudget(4000, 8, 1200, 20, 24000)));

        Assert.Equal(1, CountOccurrences(bundle.Messages[0].Text!, HostPromptSafety.MandatoryText));
    }

    [Fact]
    public async Task Safety_suffix_survives_system_prompt_truncation()
    {
        var profile = Profile();
        var prompt = new PromptBundle(new string('x', 5000) + HostPromptSafety.Suffix, null, "1")
        {
            SafetyAppended = true,
        };
        var bundle = await new HostRecentWindowContextAssembler().AssembleAsync(new ContextRequest(
            profile,
            prompt,
            Source.Create("discord", "discord", "bot"),
            new Conversation("c1", ConversationKind.Direct),
            new User("u1", "Alice"),
            "hello",
            [],
            [],
            null,
            new ContextBudget(200, 8, 1200, 20, 24000)));

        var system = bundle.Messages[0].Text!;
        Assert.EndsWith(HostPromptSafety.MandatoryText, system, StringComparison.Ordinal);
        Assert.True(system.Length <= 200, $"length={system.Length}");
    }

    [Fact]
    public async Task Host_static_profile_level_disable_leaves_only_mandatory_safety()
    {
        var profile = Profile(disabled: [HostStaticPromptProvider.SystemFragmentId]);
        var bundle = await new HostStaticPromptProvider().GetAsync(Request(profile));

        Assert.Equal(HostPromptSafety.MandatoryText, bundle.SystemPrompt);
        Assert.Single(bundle.Plan!.Fragments);
        Assert.Equal(HostStaticPromptProvider.SystemFragmentId, Assert.Single(bundle.Plan.Disabled).Id);
    }

    [Fact]
    public void Plan_hash_is_stable_across_repeated_compositions()
    {
        var fragments = new[] { Fragment("a", "a", 10), Fragment("b", "b", 20) };
        var first = Compose(fragments);
        var second = Compose(fragments);

        Assert.Equal(first.Hash, second.Hash);
        Assert.StartsWith("sha256:", first.Hash, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_profiles_keep_host_static_without_preset_migration()
    {
        var defaults = new RuntimeProfileStore(null).Current;
        Assert.All(defaults.Profiles, profile => Assert.Equal("host#static", profile.Prompt.Provider));

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Natter.slnx")))
            root = root.Parent;
        Assert.NotNull(root);

        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root!.FullName, "config", "profiles.json")));
        foreach (var profile in document.RootElement.GetProperty("profiles").EnumerateArray())
            Assert.Equal("host#static", profile.GetProperty("prompt").GetProperty("provider").GetString());
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
