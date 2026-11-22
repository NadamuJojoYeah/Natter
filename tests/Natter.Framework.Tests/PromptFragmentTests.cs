using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Natter.Abstractions.Plugins;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Infrastructure.Composition;
using Natter.Plugin.Abstractions;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// Prompt Compiler P3: plugin-contributed fragments. The host aggregator is the fault boundary
/// (drop + warning, never fail the prompt); the composer stays a pure, strict function and always
/// keeps the host mandatory safety fragment last. Caller cancellation is propagated untouched.
/// </summary>
public sealed class PromptFragmentTests
{
    private static RuntimeProfile Profile(string id = "tier1-default", List<string>? disabled = null)
        => new()
        {
            Id = id,
            Prompt = new ProfilePrompt
            {
                Provider = "host#static",
                SystemPrompt = "system prompt",
                DisabledFragments = disabled ?? [],
            },
        };

    private static PromptRequest Request(RuntimeProfile? profile = null, string userName = "Alice")
        => new(
            profile ?? Profile(),
            Source.Create("discord", "discord", "bot"),
            new Conversation("c1", ConversationKind.Direct),
            new User("u1", userName),
            Locale: null,
            Variables: new Dictionary<string, string>());

    private static PromptFragment Fragment(string id, string text, int order = 10, bool mandatory = false)
        => new(
            id,
            text,
            PromptFragmentSource.Plugin,
            order,
            EnabledByDefault: true,
            CanDisable: true,
            Mandatory: mandatory);

    private static DelegateProvider Provider(
        Func<PromptFragmentContext, CancellationToken, ValueTask<IReadOnlyList<PromptFragment>>> handler)
        => new(handler);

    private static DelegateProvider Provider(params PromptFragment[] fragments)
        => new((_, _) => ValueTask.FromResult<IReadOnlyList<PromptFragment>>(fragments));

    private static ContextComponentCatalog Catalog(params (string Reference, IPromptFragmentProvider Provider)[] providers)
    {
        var catalog = new ContextComponentCatalog();
        foreach (var (reference, provider) in providers)
            catalog.RegisterFragmentProvider(reference, provider);

        return catalog;
    }

    private static async Task<PromptBundle> ComposeAsync(
        ContextComponentCatalog catalog,
        ListLogger<HostPromptFragmentSource>? logger = null,
        PromptFragmentSourceOptions? options = null,
        PromptRequest? request = null)
    {
        var source = new HostPromptFragmentSource(catalog, logger, options);
        var provider = new HostStaticPromptProvider(new PromptComposer(), source);
        return await provider.GetAsync(request ?? Request());
    }

    [Fact]
    public async Task No_provider_keeps_default_profile_output_byte_identical()
    {
        var baseline = await new HostStaticPromptProvider(new PromptComposer()).GetAsync(Request());
        var withAggregator = await ComposeAsync(new ContextComponentCatalog());

        Assert.Equal(baseline.SystemPrompt, withAggregator.SystemPrompt);
        Assert.Equal(baseline.Version, withAggregator.Version);
        Assert.Equal(baseline.PlanHash, withAggregator.PlanHash);
        Assert.Equal("system prompt" + HostPromptSafety.Suffix, withAggregator.SystemPrompt);
        Assert.Equal(0, withAggregator.PluginFragmentCount);
        Assert.Equal(0, withAggregator.PluginFragmentProviderCount);
        Assert.Equal(0, withAggregator.Plan!.PluginFragmentCount);

        var defaults = new RuntimeProfileStore(null).Current;
        Assert.All(defaults.Profiles, profile => Assert.Equal("host#static", profile.Prompt.Provider));
    }

    [Fact]
    public async Task Single_provider_fragments_are_included_in_order_with_normalized_orders()
    {
        var catalog = Catalog(("natter.test.fragment#multi", Provider(
            Fragment("zeta", "plugin zeta", order: 20),
            Fragment("alpha", "plugin alpha", order: -100))));

        var bundle = await ComposeAsync(catalog);
        var order = bundle.Plan!.Fragments.Select(fragment => fragment.Id).ToArray();

        Assert.Equal(
            new[] { "alpha", HostStaticPromptProvider.SystemFragmentId, "zeta", HostPromptSafety.MandatoryFragmentId },
            order);
        Assert.Equal(2, bundle.Plan.PluginFragmentCount);
        Assert.Equal(2, bundle.PluginFragmentCount);
        Assert.Equal(1, bundle.PluginFragmentProviderCount);
        Assert.Contains("plugin alpha", bundle.SystemPrompt);
        Assert.Contains("plugin zeta", bundle.SystemPrompt);
        Assert.All(
            bundle.Plan.Fragments.Where(fragment => fragment.Source == PromptFragmentSource.Plugin),
            fragment => Assert.True(fragment.CanDisable));
        Assert.Equal(0, bundle.Plan.Fragments.Single(fragment => fragment.Id == "alpha").Order);
    }

    [Fact]
    public async Task Multiple_providers_are_processed_in_reference_order_and_first_wins()
    {
        var catalog = Catalog(
            ("natter.test.b#frag", Provider(Fragment("shared", "from b"))),
            ("natter.test.a#frag", Provider(Fragment("shared", "from a"))));

        var first = await ComposeAsync(catalog);
        var second = await ComposeAsync(catalog);

        Assert.Contains("from a", first.SystemPrompt);
        Assert.DoesNotContain("from b", first.SystemPrompt);
        Assert.Equal(first.Plan!.Fragments.Select(fragment => fragment.Id), second.Plan!.Fragments.Select(fragment => fragment.Id));
        Assert.Contains($"{PromptCompilerCodes.FragmentDuplicate}:shared", first.Plan.Warnings);
    }

    [Fact]
    public async Task Host_fragment_id_collisions_drop_only_the_plugin_fragment()
    {
        var catalog = Catalog(("natter.test.fragment#collision", Provider(
            Fragment(HostStaticPromptProvider.SystemFragmentId, "HIJACK-SYSTEM"),
            Fragment(HostPromptSafety.MandatoryFragmentId, "HIJACK-SAFETY"),
            Fragment("extra", "legit plugin fragment"))));

        var bundle = await ComposeAsync(catalog);

        Assert.Contains("system prompt", bundle.SystemPrompt);
        Assert.DoesNotContain("HIJACK-SYSTEM", bundle.SystemPrompt);
        Assert.DoesNotContain("HIJACK-SAFETY", bundle.SystemPrompt);
        Assert.Contains("legit plugin fragment", bundle.SystemPrompt);
        Assert.EndsWith(HostPromptSafety.MandatoryText, bundle.SystemPrompt, StringComparison.Ordinal);
        Assert.Equal(1, bundle.Plan!.PluginFragmentCount);
        Assert.Contains($"{PromptCompilerCodes.FragmentConflict}:{HostStaticPromptProvider.SystemFragmentId}", bundle.Plan.Warnings);
        Assert.Contains($"{PromptCompilerCodes.FragmentConflict}:{HostPromptSafety.MandatoryFragmentId}", bundle.Plan.Warnings);
    }

    [Fact]
    public async Task Mandatory_plugin_fragments_are_rejected_and_the_prompt_still_compiles()
    {
        var catalog = Catalog(("natter.test.fragment#forced", Provider(
            Fragment("forced", "FORCED FRAGMENT", mandatory: true),
            Fragment("optional", "optional fragment"))));

        var bundle = await ComposeAsync(catalog);

        Assert.DoesNotContain("FORCED FRAGMENT", bundle.SystemPrompt);
        Assert.Contains("optional fragment", bundle.SystemPrompt);
        Assert.Equal(1, bundle.Plan!.PluginFragmentCount);
        Assert.Contains($"{PromptCompilerCodes.FragmentMandatoryRejected}:forced", bundle.Plan.Warnings);
    }

    [Fact]
    public async Task Provider_exception_and_timeout_are_isolated()
    {
        var options = new PromptFragmentSourceOptions(
            TimeSpan.FromMilliseconds(50),
            PromptCompilerDefaults.PluginMaxFragments,
            PromptCompilerDefaults.FragmentMaxCharacters,
            PromptCompilerDefaults.PluginTotalMaxCharacters);

        var catalog = Catalog(
            ("natter.test.fragment#boom", Provider((_, _) =>
                throw new InvalidOperationException("PROMPT-BODY-SENTINEL-BOOM"))),
            ("natter.test.fragment#slow", Provider(async (_, cancellationToken) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                return Array.Empty<PromptFragment>();
            })),
            ("natter.test.fragment#good", Provider(Fragment("good", "good fragment"))));

        var logger = new ListLogger<HostPromptFragmentSource>();
        var bundle = await ComposeAsync(catalog, logger, options);

        Assert.Contains("good fragment", bundle.SystemPrompt);
        Assert.Equal(1, bundle.Plan!.PluginFragmentCount);
        Assert.Equal(3, bundle.PluginFragmentProviderCount);
        Assert.Contains($"{PromptCompilerCodes.FragmentFailed}:natter.test.fragment#boom", bundle.Plan.Warnings);
        Assert.Contains($"{PromptCompilerCodes.FragmentTimeout}:natter.test.fragment#slow", bundle.Plan.Warnings);
        Assert.DoesNotContain(logger.Messages, message => message.Contains("PROMPT-BODY-SENTINEL-BOOM", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Oversized_fragments_are_dropped()
    {
        var catalog = Catalog(("natter.test.fragment#big", Provider(
            Fragment("too.big", new string('x', PromptCompilerDefaults.FragmentMaxCharacters + 1)))));

        var bundle = await ComposeAsync(catalog);

        Assert.Equal(0, bundle.Plan!.PluginFragmentCount);
        Assert.Contains($"{PromptCompilerCodes.FragmentTooLong}:too.big", bundle.Plan.Warnings);
        Assert.Equal("system prompt" + HostPromptSafety.Suffix, bundle.SystemPrompt);
    }

    [Fact]
    public async Task Fragment_count_limit_is_enforced()
    {
        var fragments = Enumerable.Range(0, PromptCompilerDefaults.PluginMaxFragments + 6)
            .Select(index => Fragment($"f{index}", $"fragment {index}"))
            .ToArray();
        var catalog = Catalog(("natter.test.fragment#many", Provider(fragments)));

        var bundle = await ComposeAsync(catalog);

        Assert.Equal(PromptCompilerDefaults.PluginMaxFragments, bundle.Plan!.PluginFragmentCount);
        Assert.Contains(
            bundle.Plan.Warnings,
            warning => warning.StartsWith(PromptCompilerCodes.PluginFragmentLimit, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Total_length_budget_drops_excess_fragments()
    {
        var fragments = Enumerable.Range(0, 5)
            .Select(index => Fragment($"b{index}", new string('y', 7000)))
            .ToArray();
        var catalog = Catalog(("natter.test.fragment#budget", Provider(fragments)));

        var bundle = await ComposeAsync(catalog);

        Assert.Equal(4, bundle.Plan!.PluginFragmentCount);
        Assert.Contains(
            bundle.Plan.Warnings,
            warning => warning.StartsWith(PromptCompilerCodes.FragmentBudget, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        var catalog = Catalog(("natter.test.fragment#wait", Provider(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Array.Empty<PromptFragment>();
        })));
        var source = new HostPromptFragmentSource(catalog);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await source.GetFragmentsAsync(
                PromptFragmentContexts.FromPromptRequest(Request(), null),
                cancellation.Token));

        using var alreadyCancelled = new CancellationTokenSource();
        await alreadyCancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await source.GetFragmentsAsync(
                PromptFragmentContexts.FromPromptRequest(Request(), null),
                alreadyCancelled.Token));
    }

    [Fact]
    public async Task Logs_contain_diagnostics_but_never_fragment_text_or_variable_values()
    {
        const string bodySentinel = "PLUGIN-BODY-SENTINEL-42";
        const string variableSentinel = "VARIABLE-SENTINEL-99";
        var catalog = Catalog(("natter.test.fragment#log", Provider(
            Fragment("sentinel", bodySentinel),
            Fragment("bad id!", "rejected body"))));

        var logger = new ListLogger<HostPromptFragmentSource>();
        var bundle = await ComposeAsync(catalog, logger, request: Request(userName: variableSentinel));

        Assert.NotEmpty(logger.Messages);
        Assert.Contains(logger.Messages, message => message.Contains("accepted=", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message => message.Contains(bodySentinel, StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("rejected body", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message => message.Contains(variableSentinel, StringComparison.Ordinal));
        Assert.Contains(
            bundle.Plan!.Warnings,
            warning => warning.StartsWith(PromptCompilerCodes.FragmentIdRejected + ":", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Context_diagnostics_report_plugin_fragment_and_provider_counts()
    {
        var catalog = Catalog(("natter.test.fragment#diag", Provider(Fragment("diag", "diagnostic fragment"))));
        var profile = Profile();
        var bundle = await ComposeAsync(catalog, request: Request(profile));
        var source = Source.Create("discord", "discord", "bot");
        var conversation = new Conversation("c1", ConversationKind.Direct);
        var user = new User("u1", "Alice");

        var context = await new HostRecentWindowContextAssembler().AssembleAsync(new ContextRequest(
            profile,
            bundle,
            source,
            conversation,
            user,
            "hello",
            [],
            [],
            null,
            new ContextBudget(4000, 8, 1200, 20, 24000)));

        Assert.Equal(1, context.Diagnostics.PluginFragmentCount);
        Assert.Equal(1, context.Diagnostics.PluginFragmentProviderCount);
        Assert.Equal(bundle.PlanHash, context.Diagnostics.PromptPlanHash);
    }

    [Fact]
    public void Prompt_fragment_is_a_known_contribution_type()
    {
        Assert.Contains(PluginContributionTypes.PromptFragment, PluginContributionTypes.All);
        Assert.True(PluginContributionTypes.IsKnown(PluginContributionTypes.PromptFragment));
        Assert.True(PluginContributionTypes.IsKnown(PluginContributionTypes.PromptProvider));
    }

    [Fact]
    public async Task Declared_prompt_fragment_without_registration_fails_with_missing_contribution()
    {
        var manifest = new PluginManifest
        {
            SchemaVersion = 1,
            Id = "natter.test.fragment.missing",
            Name = "Missing Fragment Provider",
            Version = "0.1.0",
            Kind = PluginKind.Feature,
            HostApi = ">=0.1.0 <1.0.0",
            Entry = new PluginEntry
            {
                Assembly = "Natter.Framework.Tests.dll",
                Type = typeof(NoOpFragmentPlugin).AssemblyQualifiedName!,
            },
            Permissions = [],
            Contributions =
            [
                new PluginContribution
                {
                    Type = PluginContributionTypes.PromptFragment,
                    Id = "missing",
                },
            ],
        };

        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddEmbeddedNpsPlugin(manifest, new NoOpFragmentPlugin());

        await using var host = builder.Build();
        await host.StartAsync();

        var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);
        Assert.Equal(PluginState.Failed, descriptor.State);
        Assert.Equal("missing-contribution", descriptor.ErrorDetail!.Code);
        Assert.Contains("prompt.fragment/missing", descriptor.Error, StringComparison.Ordinal);
        Assert.Empty(host.Services.GetRequiredService<IContextComponentCatalog>().FragmentProviders);
    }

    [Fact]
    public void Default_aggregator_timeout_is_two_seconds()
        => Assert.Equal(TimeSpan.FromSeconds(2), PromptFragmentSourceOptions.Default.ProviderTimeout);

    [Fact]
    public async Task Nps_prompt_fragment_contribution_registers_and_disappears_after_stop()
    {
        var manifest = new PluginManifest
        {
            SchemaVersion = 1,
            Id = "natter.test.fragment.plugin",
            Name = "Prompt Fragment Test",
            Version = "0.1.0",
            Kind = PluginKind.Feature,
            HostApi = ">=0.1.0 <1.0.0",
            Entry = new PluginEntry
            {
                Assembly = "Natter.Framework.Tests.dll",
                Type = typeof(NpsFragmentTestPlugin).AssemblyQualifiedName!,
            },
            Permissions = [],
            Contributions =
            [
                new PluginContribution
                {
                    Type = PluginContributionTypes.PromptFragment,
                    Id = "extra",
                },
            ],
        };

        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddEmbeddedNpsPlugin(manifest, new NpsFragmentTestPlugin());

        await using var host = builder.Build();
        await host.StartAsync();

        var catalog = host.Services.GetRequiredService<IContextComponentCatalog>();
        Assert.Contains(catalog.FragmentProviders, item => item.Reference == "natter.test.fragment.plugin#extra");
        Assert.Contains(
            catalog.Components,
            component => component.Type == PluginContributionTypes.PromptFragment
                         && component.Id == "extra");

        var promptProvider = host.Services.GetRequiredService<IPromptProvider>();
        var withPlugin = await promptProvider.GetAsync(Request());
        Assert.Contains("NPS PLUGIN FRAGMENT", withPlugin.SystemPrompt);
        Assert.Equal(1, withPlugin.Plan!.PluginFragmentCount);
        Assert.Equal(1, withPlugin.PluginFragmentProviderCount);

        await host.StopAsync();

        Assert.Empty(catalog.FragmentProviders);
        var afterStop = await promptProvider.GetAsync(Request());
        Assert.DoesNotContain("NPS PLUGIN FRAGMENT", afterStop.SystemPrompt);
        Assert.Equal(0, afterStop.Plan!.PluginFragmentCount);
    }

    private sealed class DelegateProvider(
        Func<PromptFragmentContext, CancellationToken, ValueTask<IReadOnlyList<PromptFragment>>> handler)
        : IPromptFragmentProvider
    {
        public ValueTask<IReadOnlyList<PromptFragment>> GetFragmentsAsync(
            PromptFragmentContext context,
            CancellationToken cancellationToken = default)
            => handler(context, cancellationToken);
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    /// <summary>Embedded NPS plugin that contributes one <c>prompt.fragment</c>.</summary>
    public sealed class NpsFragmentTestPlugin : INatterPlugin
    {
        public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            context.Contributions.AddPromptFragmentProvider<NpsFragmentProvider>("extra");
            return ValueTask.CompletedTask;
        }

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    /// <summary>Declares prompt.fragment in the manifest but never registers a provider.</summary>
    public sealed class NoOpFragmentPlugin : INatterPlugin
    {
        public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    public sealed class NpsFragmentProvider : IPromptFragmentProvider
    {
        public ValueTask<IReadOnlyList<PromptFragment>> GetFragmentsAsync(
            PromptFragmentContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<PromptFragment>>(
            [
                new PromptFragment("nps.extra", "NPS PLUGIN FRAGMENT", PromptFragmentSource.Plugin, 15),
            ]);
    }
}
