using Natter.Application.Chat;

namespace Natter.Plugin.PromptLibrary;

/// <summary>
/// NPS <c>prompt.provider</c> implementation. The plugin supplies presets/fragments; compilation
/// goes through the host-owned <see cref="PromptComposer"/>, which always injects the mandatory
/// host safety fragment, so this provider cannot remove it. On any failure the provider degrades to
/// the same safe fallback as <c>host#static</c> (which lets the context assembler append safety).
/// No prompt text or variable value is ever logged.
/// </summary>
public sealed class PromptLibraryProvider : IPromptProvider
{
    public const string FallbackSystemPrompt = "You are Natter, a concise assistant.";

    private readonly PromptLibraryRuntime _runtime;
    private readonly PromptComposer _composer = new();

    public PromptLibraryProvider(PromptLibraryRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public ValueTask<PromptBundle> GetAsync(PromptRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var library = _runtime.Library;
        if (!library.Enabled)
            return ValueTask.FromResult(Fallback(request, "disabled"));

        try
        {
            var presetId = FirstNonEmpty(request.Profile.Prompt.Preset, library.ActivePreset);
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
            };

            var plan = _composer.Compose(compose);
            _runtime.Log.Debug(
                "prompt library composed preset={PresetId} version={PresetVersion} fragments={FragmentCount} disabled={DisabledCount} hash={Hash}",
                plan.PresetId,
                plan.PresetVersion,
                plan.Fragments.Count,
                plan.Disabled.Count,
                plan.Hash);

            return ValueTask.FromResult(_composer.ToBundle(plan));
        }
        catch (PromptCompilationException exception)
        {
            // Code only: the message can name ids but never text/values, and even that stays out.
            _runtime.Log.Error(null, "prompt library composition failed code={Code}; using safe fallback", exception.Code);
            return ValueTask.FromResult(Fallback(request, exception.Code));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _runtime.Log.Error(null, "prompt library composition failed type={Type}; using safe fallback", exception.GetType().Name);
            return ValueTask.FromResult(Fallback(request, "unexpected"));
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

        // SafetyAppended stays false so the host context assembler appends the mandatory block.
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
