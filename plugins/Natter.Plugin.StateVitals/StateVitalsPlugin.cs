using Microsoft.Extensions.DependencyInjection;
using Natter.Cli.Abstractions;
using Natter.Plugin.Abstractions;
using Natter.Plugin.StateVitals.Cli;

namespace Natter.Plugin.StateVitals;

/// <summary>
/// NPS v1 entry point for State &amp; Vitals (docs/STATE_VITALS_DESIGN.md). Configure validates the
/// configuration, resolves the content packs (unknown rule names, broken buckets and parent cycles
/// all fail here, at startup) and stages the runtime; Activate only opens the plugin's own database.
///
/// <para>
/// The plugin is opt-in: with <c>enabled=false</c> the contributions are still registered — the
/// manifest declares them, so activation must not report them missing — but the fragment provider
/// answers with zero fragments. An installed-but-unconfigured plugin therefore changes the host
/// prompt not at all.
/// </para>
/// </summary>
public sealed class StateVitalsPlugin : INatterPlugin
{
    public static PluginManifest Manifest { get; } = PluginManifestReader.ReadEmbedded(
        typeof(StateVitalsPlugin).Assembly,
        "Natter.Plugin.StateVitals.plugin.json");

    private StateVitalsRuntime? _runtime;
    private IDisposable? _stopRegistration;

    public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = LoadOptions(context);

        var issues = StateVitalsOptionsValidator.Validate(options);
        if (issues.Count > 0)
        {
            throw new StateVitalsStartupException(
                issues[0].Code,
                string.Join("; ", issues.Select(issue => $"{issue.Pointer} {issue.Message}")));
        }

        var pluginDirectory = ResolvePluginDirectory(typeof(StateVitalsPlugin).Assembly.Location);
        var packsDirectory = StateVitalsPaths.ResolvePacksDirectory(options, pluginDirectory);

        // Pack problems surface here, before anything is registered.
        _runtime = StateVitalsRuntimeBuilder.Build(
            options,
            pluginDirectory,
            context.Log,
            TimeProvider.System,
            packsDirectory: packsDirectory);

        context.Services.Collection.AddSingleton(options);
        context.Services.Collection.AddSingleton(_runtime);
        context.Services.Collection.AddSingleton<StateVitalsFragmentProvider>(provider => new StateVitalsFragmentProvider(
            provider.GetRequiredService<StateVitalsRuntime>(),
            provider.GetRequiredService<StateVitalsOptions>(),
            provider.GetService<IPluginLog>()));

        context.Contributions.AddPromptFragmentProvider<StateVitalsFragmentProvider>(StateVitalsDefaults.ContributionId);

        context.Contributions.AddCliCommand(new CliCommandDescriptor
        {
            Name = "states validate",
            Description = "Load every content pack and report all configuration errors plus definitions skipped for a missing soft dependency.",
            Usage = "natter states validate [--json]",
            Options = [new CliOptionDescriptor("json", "Emit machine-readable JSON.")],
            HandlerFactory = provider => new StatesValidateCommand(
                provider.GetRequiredService<StateVitalsRuntime>(),
                packsDirectory),
        });

        context.Contributions.AddCliCommand(new CliCommandDescriptor
        {
            Name = "states explain",
            Description = "Show one resolved definition, its current value/bucket and the fragment it renders to.",
            Usage = "natter states explain <id> [--json]",
            Options = [new CliOptionDescriptor("json", "Emit machine-readable JSON.")],
            Arguments = [new CliArgumentDescriptor("id", "Vital, assertedState or derivedState id.", Required: true)],
            HandlerFactory = provider => new StatesExplainCommand(provider.GetRequiredService<StateVitalsRuntime>()),
        });

        context.Log.Debug(
            "state-vitals configured enabled={0} stateId={1} definitions={2}",
            options.Enabled,
            options.StateId,
            _runtime.Packs?.DefinitionVersion ?? "-");

        return ValueTask.CompletedTask;
    }

    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var runtime = _runtime
                      ?? throw new InvalidOperationException("State & Vitals was not configured.");

        await runtime.InitializeAsync(cancellationToken).ConfigureAwait(false);

        _stopRegistration = context.Lifetime.OnStop(token => new ValueTask(DisposeSafelyAsync(runtime)));
    }

    public async ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        _stopRegistration?.Dispose();
        _stopRegistration = null;

        if (_runtime is not null)
            await _runtime.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Resolves the package root from an assembly location (handles the <c>lib/</c> layout).</summary>
    public static string ResolvePluginDirectory(string? assemblyLocation)
    {
        var directory = string.IsNullOrWhiteSpace(assemblyLocation)
            ? AppContext.BaseDirectory
            : Path.GetDirectoryName(assemblyLocation) ?? AppContext.BaseDirectory;

        if (string.Equals(Path.GetFileName(directory), "lib", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(directory);
            if (!string.IsNullOrEmpty(parent))
                return parent;
        }

        return directory;
    }

    private static async Task DisposeSafelyAsync(StateVitalsRuntime runtime)
    {
        try
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Stop is best-effort: a content plugin must never keep the host alive.
        }
    }

    private static StateVitalsOptions LoadOptions(IPluginContext context)
    {
        try
        {
            return context.Config.Get<StateVitalsOptions>() ?? new StateVitalsOptions();
        }
        catch (PluginConfigException exception)
        {
            throw new StateVitalsStartupException("state-vitals.config-invalid", exception.Message);
        }
    }
}
