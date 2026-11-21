using System.Text;
using Natter.Application.Chat;
using Xunit;

namespace Natter.Plugin.PromptLibrary.Tests;

public sealed class PromptLibraryLoaderTests
{
    private static PromptLibraryOptions Options(
        IEnumerable<PromptFragmentDefinition>? fragments = null,
        IEnumerable<PromptPresetDefinition>? presets = null,
        string? preset = null,
        string? locale = null,
        string promptDirectory = "prompts",
        string? presetFile = null,
        int maxCharacters = 12000,
        int maxFragmentCharacters = 8000,
        string missingVariables = "warn",
        IEnumerable<string>? disabled = null)
        => new()
        {
            Preset = preset,
            Locale = locale,
            PromptDirectory = promptDirectory,
            PresetFile = presetFile,
            MaxCharacters = maxCharacters,
            MaxFragmentCharacters = maxFragmentCharacters,
            MissingVariables = missingVariables,
            DisabledFragments = disabled?.ToList() ?? [],
            Fragments = fragments?.ToList() ?? [],
            Presets = presets?.ToList() ?? [],
        };

    private static PromptFragmentDefinition Inline(string id, string text, int order = 10)
        => new() { Id = id, Text = text, Order = order };

    private static PromptSectionDefinition Section(string fragmentId, int order = 10)
        => new() { FragmentId = fragmentId, Order = order };

    private static PromptPresetDefinition Preset(string id, string version, params PromptSectionDefinition[] sections)
        => new() { Id = id, Version = version, Sections = sections.ToList() };

    [Fact]
    public void Loads_inline_and_file_fragments_with_sources_orders_and_clamping()
    {
        using var temp = new TempDirectory();
        temp.Write("prompts/persona.md", "persona text\nwith second line\n");
        var options = Options(
            fragments:
            [
                new PromptFragmentDefinition
                {
                    Id = "persona",
                    File = "persona.md",
                    Order = 20,
                    Version = "1",
                    Tags = ["persona"],
                    MaxCharacters = 50000,
                },
                Inline("style", "answer briefly", order: 10),
            ],
            presets: [Preset("default", "v1", Section("persona", 20), Section("style", 10))]);

        var result = PromptLibraryLoader.Load(options, temp.Path);

        Assert.True(result.Success, result.Code + ": " + result.Message);
        var library = result.Library!;
        Assert.Equal(["persona", "style"], library.Fragments.Select(fragment => fragment.Id).ToArray());
        Assert.Equal(PromptFragmentSource.File, library.Fragments[0].Source);
        Assert.Equal("persona text\nwith second line", library.Fragments[0].Text);
        Assert.Equal(PromptFragmentSource.Inline, library.Fragments[1].Source);
        Assert.Equal(8000, library.Fragments[0].MaxCharacters);
        Assert.Equal(["persona"], library.Fragments[0].Tags);
        Assert.Equal("v1", library.Presets.Single().Version);
    }

    [Fact]
    public void External_preset_file_is_merged_with_inline_presets()
    {
        using var temp = new TempDirectory();
        temp.Write("prompts/persona.md", "persona");
        temp.Write("presets.extra.json", """
        {
          "presets": [
            { "id": "strict", "version": "v2", "extends": "base", "sections": [
              { "fragmentId": "persona", "enabled": false, "reason": "strict" } ] }
          ]
        }
        """);
        var options = Options(
            fragments: [new PromptFragmentDefinition { Id = "persona", File = "persona.md" }],
            presets:
            [
                new PromptPresetDefinition
                {
                    Id = "base",
                    Version = "v1",
                    Sections = [Section("persona")],
                },
            ],
            preset: "strict",
            presetFile: "presets.extra.json");

        var result = PromptLibraryLoader.Load(options, temp.Path);

        Assert.True(result.Success, result.Code + ": " + result.Message);
        Assert.Equal(["base", "strict"], result.Library!.Presets.Select(preset => preset.Id).ToArray());
        Assert.Equal("base", result.Library.Presets[1].Extends);
        Assert.Equal("strict", result.Library.ActivePreset);
    }

    [Fact]
    public void Fragment_must_define_exactly_one_of_text_or_file()
    {
        var both = PromptLibraryLoader.Load(
            Options(fragments: [new PromptFragmentDefinition { Id = "x", Text = "t", File = "prompts/x.md" }]),
            AppContext.BaseDirectory);
        Assert.Equal(PromptLibraryCodes.FragmentInvalid, both.Code);

        var neither = PromptLibraryLoader.Load(
            Options(fragments: [new PromptFragmentDefinition { Id = "x" }]),
            AppContext.BaseDirectory);
        Assert.Equal(PromptLibraryCodes.FragmentInvalid, neither.Code);
    }

    [Fact]
    public void Absolute_and_traversal_paths_are_rejected()
    {
        using var temp = new TempDirectory();
        var absolute = PromptLibraryLoader.Load(
            Options(fragments: [new PromptFragmentDefinition { Id = "x", File = "/etc/passwd" }]),
            temp.Path);
        Assert.Equal(PromptLibraryCodes.FragmentPathInvalid, absolute.Code);

        var traversal = PromptLibraryLoader.Load(
            Options(fragments: [new PromptFragmentDefinition { Id = "x", File = "../secret.md" }]),
            temp.Path);
        Assert.Equal(PromptLibraryCodes.FragmentPathInvalid, traversal.Code);

        var mixed = PromptLibraryLoader.Load(
            Options(fragments: [new PromptFragmentDefinition { Id = "x", File = "prompts/../../secret.md" }]),
            temp.Path);
        Assert.Equal(PromptLibraryCodes.FragmentPathInvalid, mixed.Code);
    }

    [Fact]
    public void Prompt_directory_traversal_and_absolute_paths_are_rejected()
    {
        using var temp = new TempDirectory();
        var options = Options(
            fragments: [Inline("x", "x")],
            promptDirectory: "../outside");
        var result = PromptLibraryLoader.Load(options, temp.Path);
        Assert.Equal(PromptLibraryCodes.FragmentPathInvalid, result.Code);

        var absolute = PromptLibraryLoader.Load(
            Options(fragments: [Inline("x", "x")], promptDirectory: "/tmp"),
            temp.Path);
        Assert.Equal(PromptLibraryCodes.FragmentPathInvalid, absolute.Code);
    }

    [Fact]
    public void Symlinked_fragment_files_are_rejected()
    {
        using var temp = new TempDirectory();
        var target = temp.Write("prompts/real.md", "real");
        var link = Path.Combine(temp.Path, "prompts", "link.md");
        File.CreateSymbolicLink(link, target);

        var result = PromptLibraryLoader.Load(
            Options(fragments: [new PromptFragmentDefinition { Id = "x", File = "link.md" }]),
            temp.Path);

        Assert.Equal(PromptLibraryCodes.FragmentPathInvalid, result.Code);
    }

    [Fact]
    public void Symlinked_prompt_directories_are_rejected()
    {
        using var temp = new TempDirectory();
        var outside = Path.Combine(Path.GetTempPath(), "natter-prompt-library-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.md"), "secret");
        var link = Path.Combine(temp.Path, "prompts-link");
        Directory.CreateSymbolicLink(link, outside);

        try
        {
            var result = PromptLibraryLoader.Load(
                Options(
                    fragments: [new PromptFragmentDefinition { Id = "x", File = "secret.md" }],
                    promptDirectory: "prompts-link"),
                temp.Path);
            Assert.Equal(PromptLibraryCodes.FragmentPathInvalid, result.Code);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Oversized_and_non_utf8_files_are_rejected()
    {
        using var temp = new TempDirectory();
        temp.Write("prompts/big.md", new string('x', PromptLibraryLoader.MaxFragmentFileBytes + 1));
        var big = PromptLibraryLoader.Load(
            Options(fragments: [new PromptFragmentDefinition { Id = "x", File = "big.md" }]),
            temp.Path);
        Assert.Equal(PromptLibraryCodes.FragmentTooLarge, big.Code);

        File.WriteAllBytes(Path.Combine(temp.Path, "prompts", "bad.md"), [0xFF, 0xFE, 0x00, 0x01]);
        var bad = PromptLibraryLoader.Load(
            Options(fragments: [new PromptFragmentDefinition { Id = "x", File = "bad.md" }]),
            temp.Path);
        Assert.Equal(PromptLibraryCodes.FragmentNotUtf8, bad.Code);
    }

    [Fact]
    public void Duplicate_fragment_and_preset_ids_are_rejected()
    {
        var duplicateFragment = PromptLibraryLoader.Load(
            Options(fragments: [Inline("dup", "a"), Inline("dup", "b")]),
            AppContext.BaseDirectory);
        Assert.Equal(PromptLibraryCodes.FragmentDuplicate, duplicateFragment.Code);

        var duplicatePreset = PromptLibraryLoader.Load(
            Options(
                fragments: [Inline("a", "a")],
                presets:
                [
                    new PromptPresetDefinition { Id = "p", Version = "1", Sections = [Section("a")] },
                    new PromptPresetDefinition { Id = "p", Version = "2", Sections = [Section("a")] },
                ]),
            AppContext.BaseDirectory);
        Assert.Equal(PromptLibraryCodes.PresetDuplicate, duplicatePreset.Code);
    }

    [Fact]
    public void Preset_references_and_inheritance_are_validated()
    {
        var missingFragment = PromptLibraryLoader.Load(
            Options(
                fragments: [Inline("a", "a")],
                presets: [new PromptPresetDefinition { Id = "p", Version = "1", Sections = [Section("nope")] }]),
            AppContext.BaseDirectory);
        Assert.Equal(PromptLibraryCodes.PresetFragmentMissing, missingFragment.Code);

        var missingBase = PromptLibraryLoader.Load(
            Options(
                fragments: [Inline("a", "a")],
                presets: [new PromptPresetDefinition { Id = "p", Version = "1", Extends = "nope", Sections = [Section("a")] }]),
            AppContext.BaseDirectory);
        Assert.Equal(PromptLibraryCodes.PresetMissingBase, missingBase.Code);

        var cycle = PromptLibraryLoader.Load(
            Options(
                fragments: [Inline("a", "a")],
                presets:
                [
                    new PromptPresetDefinition { Id = "a", Version = "1", Extends = "b", Sections = [Section("a")] },
                    new PromptPresetDefinition { Id = "b", Version = "1", Extends = "a", Sections = [Section("a")] },
                ]),
            AppContext.BaseDirectory);
        Assert.Equal(PromptLibraryCodes.PresetCycle, cycle.Code);

        var unknownActive = PromptLibraryLoader.Load(
            Options(
                fragments: [Inline("a", "a")],
                presets: [new PromptPresetDefinition { Id = "p", Version = "1", Sections = [Section("a")] }],
                preset: "missing"),
            AppContext.BaseDirectory);
        Assert.Equal(PromptLibraryCodes.PresetMissing, unknownActive.Code);
    }

    [Fact]
    public void Plugin_fragments_cannot_be_mandatory()
    {
        var result = PromptLibraryLoader.Load(
            Options(fragments: [new PromptFragmentDefinition { Id = "x", Text = "x", Mandatory = true }]),
            AppContext.BaseDirectory);

        Assert.Equal(PromptLibraryCodes.FragmentMandatory, result.Code);
    }

    [Fact]
    public void Empty_library_and_invalid_mode_are_rejected()
    {
        var empty = PromptLibraryLoader.Load(Options(), AppContext.BaseDirectory);
        Assert.Equal(PromptLibraryCodes.NotConfigured, empty.Code);

        var invalidMode = PromptLibraryLoader.Load(
            Options(fragments: [Inline("a", "a")], missingVariables: "loud"),
            AppContext.BaseDirectory);
        Assert.Equal(PromptLibraryCodes.ConfigInvalid, invalidMode.Code);
    }

    [Fact]
    public void Unknown_disabled_fragment_is_reported_as_a_warning()
    {
        var result = PromptLibraryLoader.Load(
            Options(fragments: [Inline("a", "a")], disabled: ["ghost"]),
            AppContext.BaseDirectory);

        Assert.True(result.Success, result.Code + ": " + result.Message);
        Assert.Contains("promptlibrary.disabled-unknown:ghost", result.Library!.Warnings);
        Assert.Contains("ghost", result.Library.DisabledFragments);
    }

    [Fact]
    public void Preset_file_is_resolved_inside_the_plugin_directory()
    {
        using var temp = new TempDirectory();
        var traversal = PromptLibraryLoader.Load(
            Options(fragments: [Inline("a", "a")], presetFile: "../presets.json"),
            temp.Path);
        Assert.Equal(PromptLibraryCodes.FragmentPathInvalid, traversal.Code);

        var absolute = PromptLibraryLoader.Load(
            Options(fragments: [Inline("a", "a")], presetFile: "/tmp/presets.json"),
            temp.Path);
        Assert.Equal(PromptLibraryCodes.FragmentPathInvalid, absolute.Code);
    }

    [Fact]
    public void Missing_fragment_file_is_reported()
    {
        using var temp = new TempDirectory();
        var result = PromptLibraryLoader.Load(
            Options(fragments: [new PromptFragmentDefinition { Id = "x", File = "missing.md" }]),
            temp.Path);
        Assert.Equal(PromptLibraryCodes.FragmentMissing, result.Code);
    }

    [Fact]
    public void Invalid_fragment_and_preset_ids_are_rejected()
    {
        var badFragment = PromptLibraryLoader.Load(
            Options(fragments: [Inline("bad id!", "x")]),
            AppContext.BaseDirectory);
        Assert.Equal(PromptLibraryCodes.FragmentInvalid, badFragment.Code);

        var badPreset = PromptLibraryLoader.Load(
            Options(
                fragments: [Inline("a", "a")],
                presets: [new PromptPresetDefinition { Id = "bad/preset", Version = "1", Sections = [Section("a")] }]),
            AppContext.BaseDirectory);
        Assert.Equal(PromptLibraryCodes.PresetInvalid, badPreset.Code);
    }

    [Fact]
    public void Fragment_limit_is_clamped_to_the_plugin_cap()
    {
        var result = PromptLibraryLoader.Load(
            Options(
                fragments: [new PromptFragmentDefinition { Id = "a", Text = "a", MaxCharacters = 50000 }],
                maxFragmentCharacters: 1000),
            AppContext.BaseDirectory);

        Assert.True(result.Success, result.Code + ": " + result.Message);
        Assert.Equal(1000, result.Library!.Fragments[0].MaxCharacters);
    }

    [Fact]
    public void Crlf_and_bom_are_normalized()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "prompts", "text.md");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\r', (byte)'\n', (byte)'b']);

        var result = PromptLibraryLoader.Load(
            Options(fragments: [new PromptFragmentDefinition { Id = "a", File = "text.md" }]),
            temp.Path);

        Assert.True(result.Success, result.Code + ": " + result.Message);
        Assert.Equal("a\nb", result.Library!.Fragments[0].Text);
    }
}
