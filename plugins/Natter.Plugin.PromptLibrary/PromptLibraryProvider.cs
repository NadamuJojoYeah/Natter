using Natter.Application.Chat;

namespace Natter.Plugin.PromptLibrary;

/// <summary>
/// NPS <c>prompt.provider</c> implementation. The plugin supplies presets/fragments; compilation
/// goes through the host-owned <see cref="PromptComposer"/>, which injects no fragments and
/// treats safety like any other preset fragment. On any failure the provider degrades to the same
/// safe fallback as <c>host#static</c> (the fallback has no appended safety block).
/// No prompt text or variable value is ever logged.
/// </summary>
public sealed class PromptLibraryProvider : IPromptProvider
{
    public const string FallbackSystemPrompt = "You are Natter, a concise assistant.";

    private readonly PromptLibraryRuntime _runtime;
    private readonly PromptComposer _composer = new();
    private readonly IPromptFragmentSource? _fragmentSource;

    public PromptLibraryProvider(
        PromptLibraryRuntime runtime,
        IPromptFragmentSource? fragmentSource = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _fragmentSource = fragmentSource;
    }

    public async ValueTask<PromptBundle> GetAsync(PromptRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var library = _runtime.Library;
        if (!library.Enabled)
            return Fallback(request, "disabled");

        try
        {
            var presetId = FirstNonEmpty(request.Profile.Prompt.Preset, library.ActivePreset);
            var pluginFragments = await CollectPluginFragmentsAsync(request, presetId, cancellationToken)
                .ConfigureAwait(false);

            var compose = PromptComposeRequest.FromPromptRequest(
                request,
                library.Presets,
                library.Fragments,
                presetId: presetId,
                budget: library.Budget,
                missingVariables: library.MissingVariables);

            compose = compose with
            {
                Locale = request.Locale ?? request.Profile.Prompt.Locale ?? library.Locale,
                DisabledFragmentIds = Combine(library.DisabledFragments, request.Profile.Prompt.DisabledFragments),
                PluginFragments = pluginFragments.Fragments,
                PluginFragmentWarnings = pluginFragments.Warnings,
                PluginFragmentProviderCount = pluginFragments.ProviderCount,
                PluginFragmentElapsedMilliseconds = pluginFragments.ElapsedMilliseconds,
            };

            var plan = _composer.Compose(compose);
            _runtime.Log.Debug(
                "prompt library composed preset={PresetId} version={PresetVersion} fragments={FragmentCount} pluginFragments={PluginFragmentCount} disabled={DisabledCount} hash={Hash}",
                plan.PresetId,
                plan.PresetVersion,
                plan.Fragments.Count,
                plan.PluginFragmentCount,
                plan.Disabled.Count,
                plan.Hash);

            return _composer.ToBundle(plan);
        }
        catch (PromptCompilationException exception)
        {
            // Code only: the message can name ids but never text/values, and even that stays out.
            _runtime.Log.Error(null, "prompt library composition failed code={Code}; using safe fallback", exception.Code);
            return Fallback(request, exception.Code);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _runtime.Log.Error(null, "prompt library composition failed type={Type}; using safe fallback", exception.GetType().Name);
            return Fallback(request, "unexpected");
        }
    }

    /// <summary>
    /// Optional host integration: when the host does not expose IPromptFragmentSource (older host,
    /// missing registration) or aggregation fails unexpectedly, the library still compiles without
    /// plugin fragments. Activation is never failed and no prompt text is logged. Caller
    /// cancellation is always propagated. The provider deliberately caches nothing, so fragments
    /// from one composition cannot leak into another.
    /// </summary>
    private async ValueTask<PromptFragmentSourceResult> CollectPluginFragmentsAsync(
        PromptRequest request,
        string? presetId,
        CancellationToken cancellationToken)
    {
        if (_fragmentSource is null)
            return PromptFragmentSourceResult.Empty;

        try
        {
            return await _fragmentSource.GetFragmentsAsync(
                PromptFragmentContexts.FromPromptRequest(request, presetId),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _runtime.Log.Debug(
                "prompt library fragment aggregation failed code={Code}; continuing without plugin fragments",
                PromptCompilerCodes.FragmentFailed);
            return PromptFragmentSourceResult.Empty;
        }
    }

    private static PromptBundle Fallback(PromptRequest request, string reason)
    {
        var system = request.Profile.Prompt.SystemPrompt;
        if (string.IsNullOrWhiteSpace(system)
            && request.Variables is not null
            && request.Variables.TryGetValue("systemPrompt", out var configured))
        {
            system = configured;
        }
        if (string.IsNullOrWhiteSpace(system))
            system = FallbackSystemPrompt;

        return new PromptBundle(system, Persona: null, Version: "prompt-library-fallback:" + reason);
    }

    private static IReadOnlyList<string> Combine(
        IReadOnlyList<string>? first,
        IReadOnlyList<string>? second)
    {
        var combined = new List<string>();
        foreach (var id in (first ?? []).Concat(second ?? []))
        {
            if (!string.IsNullOrWhiteSpace(id) && !combined.Contains(id, StringComparer.Ordinal))
                combined.Add(id);
        }

        return combined;
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
