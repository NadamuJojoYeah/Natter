using Microsoft.Extensions.DependencyInjection;
using Natter.Cli.Abstractions;
using Natter.Plugin.Abstractions;

namespace Natter.Plugin.PromptLibrary;

/// <summary>
/// NPS v1 entry point. Configure loads/validates the library and stages the
/// <c>prompt.provider</c> + <c>prompt explain</c> contributions. The default profile keeps using
/// <c>host#static</c>; a profile opts in by setting
/// <c>prompt.provider = "natter.prompt.library#default"</c>.
/// </summary>
public sealed class PromptLibraryPlugin : INatterPlugin
{
    public static PluginManifest Manifest { get; } = PluginManifestReader.ReadEmbedded(
        typeof(PromptLibraryPlugin).Assembly,
        "Natter.Plugin.PromptLibrary.plugin.json");

    public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        PromptLibraryOptions options;
        try
        {
            options = context.Config.Get<PromptLibraryOptions>();
        }
        catch (PluginConfigException exception)
        {
            throw new PromptLibraryStartupException(PromptLibraryCodes.ConfigInvalid, exception.Message);
        }

        var pluginDirectory = PromptLibraryPaths.ResolvePluginDirectory(typeof(PromptLibraryPlugin).Assembly.Location);
        var load = PromptLibraryLoader.Load(options, pluginDirectory);
        if (!load.Success)
            throw new PromptLibraryStartupException(load.Code!, load.Message!);

        var library = load.Library!;
        foreach (var warning in library.Warnings)
            context.Log.Warning("{Warning}", warning);

        if (!library.Enabled)
            context.Log.Warning("prompt library is disabled (enabled=false); the provider returns the safe fallback prompt.");

        var runtime = new PromptLibraryRuntime(context.Manifest.Id, context.Manifest.Version, library, context.Log);

        // Diagnostics only: ids/counts/hash, never fragment text or variable values.
        context.Log.Information(
            "prompt library loaded fragments={FragmentCount} presets={PresetCount} preset={Preset} locale={Locale}",
            library.Fragments.Count,
            library.Presets.Count,
            library.ActivePreset ?? "auto",
            library.Locale ?? "auto");

        var services = context.Services.Collection;
        services.AddSingleton(runtime);
        services.AddSingleton<PromptLibraryProvider>();

        context.Contributions.AddPromptProvider<PromptLibraryProvider>("default");
        context.Contributions.AddCliCommand(new CliCommandDescriptor
        {
            Name = "prompt explain",
            Description = "Show the compiled prompt recipe (preset, fragment ids, disabled ids, hash) without prompt text.",
            Usage = "natter prompt explain [--json]",
            Options = [new CliOptionDescriptor("json", "Emit machine-readable JSON.")],
            HandlerFactory = provider => new PromptExplainCommand(provider.GetRequiredService<PromptLibraryRuntime>()),
        });

        return ValueTask.CompletedTask;
    }

    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}
