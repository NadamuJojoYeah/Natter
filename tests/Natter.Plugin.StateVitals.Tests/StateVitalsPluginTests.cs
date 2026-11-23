using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Cli.Abstractions;
using Natter.Cli.Core;
using Natter.Infrastructure.Composition;
using Natter.Plugin.Abstractions;
using Natter.Plugin.StateVitals.Definitions;
using Xunit;

namespace Natter.Plugin.StateVitals.Tests;

/// <summary>
/// Package-level tests for State &amp; Vitals: manifest contract, config schema, opt-in behaviour,
/// state-directory and pack-path resolution, startup validation, CLI commands and real NPS loading.
/// </summary>
public sealed class StateVitalsPluginTests
{
    [Fact]
    public void The_manifest_declares_the_documented_contract()
    {
        var manifest = StateVitalsPlugin.Manifest;

        Assert.Equal("natter.feature.character.state-vitals", manifest.Id);
        Assert.Equal(PluginKind.Feature, manifest.Kind);
        Assert.Equal("lib/Natter.Plugin.StateVitals.dll", manifest.Entry?.Assembly);
        Assert.Equal("Natter.Plugin.StateVitals.StateVitalsPlugin", manifest.Entry?.Type);
        Assert.Equal("config.schema.json", manifest.ConfigSchema);

        var contributions = (manifest.Contributions ?? []).ToArray();
        Assert.Contains(contributions, item => item.Type == "prompt.fragment" && item.Id == StateVitalsDefaults.ContributionId);
        Assert.Contains(contributions, item => item.Type == "cli.command" && item.Id == "states.validate");
        Assert.Contains(contributions, item => item.Type == "cli.command" && item.Id == "states.explain");
    }

    [Fact]
    public void The_manifest_requests_exactly_the_documented_permissions()
    {
        var permissions = (StateVitalsPlugin.Manifest.Permissions ?? [])
            .OrderBy(permission => permission, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["ai.complete", "chat.read", "cli.commands", "storage"], permissions);

        foreach (var permission in permissions)
            Assert.True(PluginPermissions.IsKnown(permission), $"unknown permission '{permission}'");
    }

    [Fact]
    public void The_sample_config_satisfies_the_shipped_schema()
    {
        var issues = PluginConfigSchema.Validate(Schema(), JsonDocument.Parse(PluginPackage.Read("config.sample.json")).RootElement);
        Assert.Empty(issues);

        // An empty config is valid too: the plugin is opt-in and must not require a config file.
        Assert.Empty(PluginConfigSchema.Validate(Schema(), JsonDocument.Parse("{}").RootElement));
    }

    [Fact]
    public void An_unknown_config_key_is_rejected()
    {
        var config = JsonDocument.Parse("""{ "enabled": true, "nonsense": 1 }""").RootElement;
        var issues = PluginConfigSchema.Validate(Schema(), config);

        Assert.Contains(issues, issue => issue.Message.Contains("Additional properties", StringComparison.Ordinal));
    }

    [Fact]
    public void The_schema_defaults_match_the_plugin_defaults()
    {
        var schema = Schema();
        var defaults = new StateVitalsOptions();

        Assert.Equal(ReadBool(schema, "enabled"), defaults.Enabled);
        Assert.Equal(ReadString(schema, "packs"), defaults.Packs);
        Assert.Equal(ReadString(schema, "stateId"), defaults.StateId);
        Assert.Equal(ReadInt(schema, "fragmentOrder"), defaults.FragmentOrder);
        Assert.Equal(ReadBool(schema, "includeDescriptions"), defaults.IncludeDescriptions);
        Assert.Equal(ReadInt(schema, "descriptionTimeoutMilliseconds"), defaults.DescriptionTimeoutMilliseconds);
        Assert.Equal(ReadInt(schema, "minAdvanceIntervalSeconds"), defaults.MinAdvanceIntervalSeconds);
        Assert.Equal(ReadDouble(schema, "maxStep"), defaults.MaxStep);
        Assert.Equal(ReadInt(schema, "maxDeltaHours"), defaults.MaxDeltaHours);
        Assert.Equal(ReadDouble(schema, "maxStepPerTickFraction"), defaults.MaxStepPerTickFraction);
        Assert.Equal(ReadInt(schema, "maxAdvanceAttempts"), defaults.MaxAdvanceAttempts);
        Assert.Equal(ReadInt(schema, "maxFragmentCharacters"), defaults.MaxFragmentCharacters);
        Assert.Equal(ReadInt(schema, "maxPacks"), defaults.MaxPacks);
        Assert.Equal(ReadInt(schema, "maxDefinitionsPerKind"), defaults.MaxDefinitionsPerKind);
        Assert.Equal(ReadInt(schema, "maxEffectsPerPack"), defaults.MaxEffectsPerPack);
    }

    [Fact]
    public void The_shipped_pack_directory_is_what_the_default_config_points_to()
    {
        // The release staging copies `definitions/`; the default must match or a fresh install has
        // no content and fails at configure time.
        Assert.Equal("definitions", StateVitalsDefaults.PacksDirectory);
        Assert.True(Directory.Exists(Path.Combine(PluginPackage.Directory, "definitions")));
        Assert.True(File.Exists(Path.Combine(PluginPackage.Directory, "definitions", "core.json")));
    }

    [Fact]
    public void The_plugin_is_opt_in_by_default()
    {
        Assert.False(new StateVitalsOptions().Enabled);

        var context = new TestPluginContext("{}");
        var plugin = new StateVitalsPlugin();
        plugin.ConfigureAsync(context).AsTask().GetAwaiter().GetResult();
        plugin.ActivateAsync(context).AsTask().GetAwaiter().GetResult();

        var runtime = context.Resolve<StateVitalsRuntime>();
        Assert.False(runtime.Enabled);
        Assert.Null(runtime.Packs);

        var provider = context.Resolve<StateVitalsFragmentProvider>();
        Assert.Empty(provider.GetFragmentsAsync(Context()).AsTask().GetAwaiter().GetResult());

        plugin.DeactivateAsync(context).AsTask().GetAwaiter().GetResult();
    }

    [Fact]
    public void A_disabled_plugin_registers_its_contributions_but_opens_no_database()
    {
        using var temp = new TempDirectory();
        var stateDirectory = Path.Combine(temp.Path, "state");
        var context = new TestPluginContext($$"""
            { "enabled": false, "stateDirectory": {{JsonSerializer.Serialize(stateDirectory)}} }
            """);

        var plugin = new StateVitalsPlugin();
        plugin.ConfigureAsync(context).AsTask().GetAwaiter().GetResult();
        plugin.ActivateAsync(context).AsTask().GetAwaiter().GetResult();

        // Activation must not report 'missing-contribution': everything declared is registered.
        Assert.Contains(("prompt.fragment", StateVitalsDefaults.ContributionId), context.Registrations());
        Assert.Contains(("cli.command", "states.validate"), context.Registrations());
        Assert.Contains(("cli.command", "states.explain"), context.Registrations());

        // ... but an opted-out plugin touches nothing on disk.
        Assert.False(Directory.Exists(stateDirectory));
        plugin.DeactivateAsync(context).AsTask().GetAwaiter().GetResult();
    }

    [Fact]
    public void The_state_directory_follows_the_documented_layout()
    {
        var previous = Environment.GetEnvironmentVariable(StateVitalsDefaults.StateRootEnvironmentVariable);
        var root = Path.Combine(Path.GetTempPath(), "natter-state-vitals-root");
        try
        {
            Environment.SetEnvironmentVariable(StateVitalsDefaults.StateRootEnvironmentVariable, root);

            var resolved = StateVitalsPaths.ResolveStateDirectory(new StateVitalsOptions());

            Assert.Equal(Path.Combine(root, "plugins", "natter.feature.character.state-vitals"), resolved);
            Assert.Equal("state.db", Path.GetFileName(StateVitalsPaths.ResolveDatabasePath(resolved)));
            Assert.DoesNotContain("natter.db", resolved, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(StateVitalsDefaults.StateRootEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void An_unknown_state_directory_fails_loudly()
    {
        var previous = Environment.GetEnvironmentVariable(StateVitalsDefaults.StateRootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(StateVitalsDefaults.StateRootEnvironmentVariable, null);

            var error = Assert.Throws<StateVitalsStartupException>(
                () => StateVitalsPaths.ResolveStateDirectory(new StateVitalsOptions()));

            Assert.Equal(StateVitalsCodes.StateDirectoryRequired, error.Code);
        }
        finally
        {
            Environment.SetEnvironmentVariable(StateVitalsDefaults.StateRootEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void The_pack_directory_may_not_escape_the_plugin_directory()
    {
        var error = Assert.Throws<StateVitalsStartupException>(() => StateVitalsPaths.ResolvePacksDirectory(
            new StateVitalsOptions { Packs = "../../../etc" },
            Path.Combine(Path.GetTempPath(), "natter-plugin-dir")));

        Assert.Equal(StateVitalsCodes.DefinitionUnreadable, error.Code);
    }

    [Fact]
    public void An_unknown_rule_name_fails_at_configure_time_instead_of_at_runtime()
    {
        using var temp = new TempDirectory();
        var packs = Path.Combine(temp.Path, "packs");
        Directory.CreateDirectory(packs);
        File.WriteAllText(Path.Combine(packs, "core.json"), PackWithUnregisteredRule());

        var context = new TestPluginContext($$"""
            {
              "enabled": true,
              "packs": {{JsonSerializer.Serialize(packs)}},
              "stateDirectory": {{JsonSerializer.Serialize(Path.Combine(temp.Path, "state"))}}
            }
            """);

        var error = Assert.Throws<StateVitalsStartupException>(
            () => new StateVitalsPlugin().ConfigureAsync(context).AsTask().GetAwaiter().GetResult());

        Assert.Equal(StateVitalsCodes.RuleUnknown, error.Code);
        Assert.Contains("moonPhase", error.Message, StringComparison.Ordinal);
        Assert.Contains("建议", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_pack_directory_fails_at_configure_time()
    {
        using var temp = new TempDirectory();
        var context = new TestPluginContext($$"""
            {
              "enabled": true,
              "packs": {{JsonSerializer.Serialize(Path.Combine(temp.Path, "nope"))}},
              "stateDirectory": {{JsonSerializer.Serialize(Path.Combine(temp.Path, "state"))}}
            }
            """);

        var error = Assert.Throws<StateVitalsStartupException>(
            () => new StateVitalsPlugin().ConfigureAsync(context).AsTask().GetAwaiter().GetResult());

        Assert.Equal(StateVitalsCodes.PackDirectoryMissing, error.Code);
    }

    [Fact]
    public void Out_of_range_configuration_is_rejected_before_activation()
    {
        var issues = StateVitalsOptionsValidator.Validate(new StateVitalsOptions
        {
            Enabled = true,
            MaxStep = 2,
            MaxDeltaHours = 0,
            FragmentOrder = -1,
            MaxFragmentCharacters = 4,
            MaxPacks = 0,
        });

        Assert.Contains(issues, issue => issue.Code == StateVitalsCodes.ConfigMaxStepInvalid);
        Assert.Contains(issues, issue => issue.Code == StateVitalsCodes.ConfigMaxDeltaHoursInvalid);
        Assert.Contains(issues, issue => issue.Code == StateVitalsCodes.ConfigFragmentOrderInvalid);
        Assert.Contains(issues, issue => issue.Code == StateVitalsCodes.ConfigMaxFragmentCharactersInvalid);
        Assert.Contains(issues, issue => issue.Code == StateVitalsCodes.ConfigQuotaInvalid);
    }

    [Fact]
    public async Task The_plugin_loads_as_an_nps_package_and_contributes_one_namespaced_fragment()
    {
        using var temp = new TempDirectory();
        var packageDirectory = StagePackage(temp, enabled: true);

        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddModule<CliCoreCompositionModule>()
            .AddPluginDirectory(packageDirectory);

        await using var host = builder.Build();
        await host.StartAsync();

        var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);
        Assert.True(
            descriptor.State == PluginState.Activated,
            $"state={descriptor.State} error={descriptor.Error}");

        var source = host.Services.GetRequiredService<IPromptFragmentSource>();
        var result = await source.GetFragmentsAsync(PromptFragmentContexts.FromPromptRequest(
            Request(),
            PromptCompilerDefaults.StaticPresetId));

        // Quotas: one fragment, far below 16 fragments / 8000 characters (design §5.2).
        Assert.Empty(result.Warnings);
        Assert.Equal(1, result.ProviderCount);

        var fragment = Assert.Single(result.Fragments);
        Assert.Equal("natter.feature.character.state-vitals#state", fragment.Id);
        Assert.Equal(PromptFragmentSource.Plugin, fragment.Source);
        Assert.Equal(StateVitalsDefaults.FragmentOrder, fragment.Order);
        Assert.InRange(fragment.Order, 0, PromptCompilerDefaults.PluginFragmentOrderMax);
        Assert.InRange(fragment.Text.Length, 1, PromptCompilerDefaults.PluginCharactersPerProvider);
        Assert.Contains("[hunger: ", fragment.Text, StringComparison.Ordinal);

        // The plugin owns its database inside the configured state directory.
        Assert.True(File.Exists(Path.Combine(temp.Path, "state", "state.db")));
        Assert.False(File.Exists(Path.Combine(temp.Path, "state", "natter.db")));

        // Both CLI commands are contributed and resolvable.
        var cli = host.Services.GetRequiredService<CliCommandRegistry>();
        Assert.True(cli.TryGet("states validate", out _));
        Assert.True(cli.TryGet("states explain", out _));

        await host.StopAsync();
    }

    [Fact]
    public async Task The_states_commands_run_against_the_staged_pack()
    {
        using var temp = new TempDirectory();
        var packageDirectory = StagePackage(temp, enabled: true);

        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddModule<CliCoreCompositionModule>()
            .AddPluginDirectory(packageDirectory);

        await using var host = builder.Build();
        await host.StartAsync();

        var cli = host.Services.GetRequiredService<CliCommandRegistry>();
        Assert.True(cli.TryGet("states validate", out var validate));
        Assert.True(cli.TryGet("states explain", out var explain));

        var validateConsole = new StringConsole();
        var validateExit = await validate!.HandlerFactory(host.Services).ExecuteAsync(new CliInvocation(
            validate.Name,
            new Dictionary<string, string?>(),
            [],
            host.Services,
            validateConsole));

        Assert.Equal(0, validateExit);
        Assert.Contains("errors: 0", validateConsole.Output, StringComparison.Ordinal);
        Assert.Contains("core@1.0.0", validateConsole.Output, StringComparison.Ordinal);

        var explainConsole = new StringConsole();
        var explainExit = await explain!.HandlerFactory(host.Services).ExecuteAsync(new CliInvocation(
            explain.Name,
            new Dictionary<string, string?>(),
            ["hunger"],
            host.Services,
            explainConsole));

        Assert.Equal(0, explainExit);
        Assert.Contains("vital core#hunger", explainConsole.Output, StringComparison.Ordinal);
        Assert.Contains("[hunger: ", explainConsole.Output, StringComparison.Ordinal);

        await host.StopAsync();
    }

    [Fact]
    public async Task A_disabled_package_contributes_no_fragment_so_the_host_prompt_is_unchanged()
    {
        using var temp = new TempDirectory();
        var packageDirectory = StagePackage(temp, enabled: false);

        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddModule<CliCoreCompositionModule>()
            .AddPluginDirectory(packageDirectory);

        await using var host = builder.Build();
        await host.StartAsync();

        var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);
        Assert.Equal(PluginState.Activated, descriptor.State);

        var source = host.Services.GetRequiredService<IPromptFragmentSource>();
        var result = await source.GetFragmentsAsync(PromptFragmentContexts.FromPromptRequest(
            Request(),
            PromptCompilerDefaults.StaticPresetId));

        Assert.Empty(result.Fragments);
        Assert.False(File.Exists(Path.Combine(temp.Path, "state", "state.db")));

        await host.StopAsync();
    }

    // ------------------------------------------------------------------ helpers

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

        public string? ReadLine() => null;

        public string Output => _buffer.ToString();
    }

    private static JsonElement Schema()
        => JsonDocument.Parse(PluginPackage.Read("config.schema.json")).RootElement.Clone();

    private static bool ReadBool(JsonElement schema, string property) => ReadDefault(schema, property).GetBoolean();

    private static int ReadInt(JsonElement schema, string property) => ReadDefault(schema, property).GetInt32();

    private static double ReadDouble(JsonElement schema, string property) => ReadDefault(schema, property).GetDouble();

    private static string ReadString(JsonElement schema, string property) => ReadDefault(schema, property).GetString()!;

    private static JsonElement ReadDefault(JsonElement schema, string property)
    {
        Assert.True(schema.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty(property, out var node), $"property '{property}' missing from schema");
        Assert.True(node.TryGetProperty("default", out var defaultValue), $"property '{property}' has no default");
        return defaultValue;
    }

    private static PromptRequest Request()
        => new(
            new RuntimeProfile { Id = "tier1-default" },
            Source.Create("discord", "discord", "bot"),
            new Conversation("conv-1", ConversationKind.Direct),
            new User("user-1", "Alice"),
            Locale: null,
            Variables: new Dictionary<string, string>());

    private static PromptFragmentContext Context()
        => PromptFragmentContexts.FromPromptRequest(Request(), PromptCompilerDefaults.StaticPresetId);

    private static string PackWithUnregisteredRule() => """
        {
          "schemaVersion": 1,
          "packId": "core",
          "version": "1.0.0",
          "rules": [ "moonPhase" ],
          "vitals": [
            {
              "id": "hunger",
              "range": { "min": 0, "max": 100 },
              "baseline": 10,
              "decay": { "mode": "multiplicative", "ratePerHour": 0.08, "target": 100 },
              "levels": [ { "at": 0, "id": "sated" }, { "at": 60, "id": "hungry" } ]
            }
          ]
        }
        """;

    /// <summary>The plugin project's own NPS staging output (<c>bin/&lt;cfg&gt;/&lt;tf&gt;/lib</c>), if built.</summary>
    private static string? FindStagedLib()
    {
        var root = RepositoryRoot();
        var projectDirectory = Path.Combine(root, "plugins", "Natter.Plugin.StateVitals", "bin");
        if (!Directory.Exists(projectDirectory))
            return null;

        return Directory.GetDirectories(projectDirectory, "lib", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "net10.0", StringComparison.Ordinal))
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
            .FirstOrDefault();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Natter.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    /// <summary>
    /// Stages a real NPS package: <c>plugin.json</c>, the schema, a config file, the pack directory and
    /// a <c>lib/</c> folder holding the plugin assembly with its private dependencies
    /// (Microsoft.Data.Sqlite and its native runtime), which are not host contract assemblies.
    /// </summary>
    private static string StagePackage(TempDirectory temp, bool enabled)
    {
        var package = Path.Combine(temp.Path, "package");
        var lib = Path.Combine(package, "lib");
        var definitions = Path.Combine(package, "definitions");
        Directory.CreateDirectory(lib);
        Directory.CreateDirectory(definitions);

        // Prefer the real NPS staging output (bin/<cfg>/<tf>/lib), because that is exactly what the
        // release staging copies. It also proves the private native dependency (SQLite) resolves
        // inside the plugin load context. Fall back to enumerating the test output when the plugin
        // project has not been built standalone yet.
        var stagedLib = FindStagedLib();
        if (stagedLib is not null)
        {
            foreach (var file in Directory.GetFiles(stagedLib, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(lib, Path.GetRelativePath(stagedLib, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }
        else
        {
            var sourceDirectory = Path.GetDirectoryName(typeof(StateVitalsPlugin).Assembly.Location)!;
            foreach (var file in Directory.GetFiles(sourceDirectory, "*.dll"))
                File.Copy(file, Path.Combine(lib, Path.GetFileName(file)), overwrite: true);

            foreach (var file in Directory.GetFiles(sourceDirectory, "*.deps.json"))
                File.Copy(file, Path.Combine(lib, Path.GetFileName(file)), overwrite: true);
        }

        File.Copy(Path.Combine(PluginPackage.Directory, "plugin.json"), Path.Combine(package, "plugin.json"));
        File.Copy(Path.Combine(PluginPackage.Directory, "config.schema.json"), Path.Combine(package, "config.schema.json"));
        File.Copy(
            Path.Combine(PluginPackage.Directory, "definitions", "core.json"),
            Path.Combine(definitions, "core.json"));

        var config = $$"""
            {
              "enabled": {{(enabled ? "true" : "false")}},
              "packs": "definitions",
              "stateDirectory": {{JsonSerializer.Serialize(Path.Combine(temp.Path, "state"))}},
              "minAdvanceIntervalSeconds": 0
            }
            """;
        File.WriteAllText(Path.Combine(package, "config.json"), config);

        return package;
    }
}
