using System.Text;
using Natter.Plugin.Abstractions;
using Natter.Plugin.StateVitals.Definitions;
using Natter.Plugin.StateVitals.Storage;

namespace Natter.Plugin.StateVitals.Descriptions;

/// <summary>
/// Everything a description generator may look at. It is deliberately identity-free (design §7.2)
/// and carries the pack's <see cref="DescriptionVoice"/> so the core plugin never authors tone,
/// prompt, examples or banned words itself (§6.3 / §12.3).
/// </summary>
public sealed record StateDescriptionRequest(
    string StateId,
    long TransitionSeq,
    string Kind,
    string DefinitionId,
    string LevelId,
    string Label,
    string? Semantic,
    string? Template,
    string? PreviousLevelId,
    double? Value,
    DescriptionVoice Voice);

/// <summary>Generated or template text plus where it came from.</summary>
public sealed record StateDescriptionResult(string Text, string Source);

public static class StateDescriptionSources
{
    public const string Generated = "generated";
    public const string Template = "template";
}

/// <summary>
/// Description generation hook (design §6). Implementations may call a model with
/// <see cref="StateDescriptionRequest.Voice"/>; failures must degrade to the template.
/// </summary>
public interface IStateDescriptionGenerator
{
    ValueTask<StateDescriptionResult> GenerateAsync(
        StateDescriptionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Deterministic fallback: the definition's own template, then its semantic note, then its label.
/// All three are pack content, so even the degraded path speaks in the pack's words.
/// </summary>
public sealed class TemplateStateDescriptionGenerator : IStateDescriptionGenerator
{
    public ValueTask<StateDescriptionResult> GenerateAsync(
        StateDescriptionRequest request,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new StateDescriptionResult(TemplateText(request), StateDescriptionSources.Template));

    /// <summary>Template text of one state, never empty.</summary>
    public static string TemplateText(StateDescriptionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.IsNullOrWhiteSpace(request.Template))
            return request.Template.Trim();

        if (!string.IsNullOrWhiteSpace(request.Semantic))
            return request.Semantic.Trim();

        return string.IsNullOrWhiteSpace(request.Label) ? request.DefinitionId : request.Label.Trim();
    }
}

/// <summary>
/// Writes one description per state entry and never recomputes it while the state holds
/// (design §6.1). Idempotency key is <c>(stateId, transitionSeq)</c>: a replayed transition reads the
/// stored row instead of generating again.
/// </summary>
public sealed class StateDescriptionService
{
    private readonly IStateVitalsStore _store;
    private readonly IStateDescriptionGenerator _generator;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _timeout;
    private readonly IPluginLog? _log;

    public StateDescriptionService(
        IStateVitalsStore store,
        IStateDescriptionGenerator generator,
        TimeProvider clock,
        TimeSpan timeout,
        IPluginLog? log = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _timeout = timeout;
        _log = log;
    }

    /// <summary>Number of generator invocations; tests use it to prove "generate once".</summary>
    public int GenerationAttempts { get; private set; }

    /// <summary>Returns the stored description for this transition, generating it at most once.</summary>
    public async ValueTask<StoredDescription> GetOrCreateAsync(
        StateDescriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var existing = await _store.GetDescriptionAsync(request.StateId, request.TransitionSeq, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
            return existing;

        var result = await GenerateSafelyAsync(request, cancellationToken).ConfigureAwait(false);
        var text = Sanitize(result.Text);

        // Content constraints are the pack's: a generated line outside the voice's banned words falls
        // back to the template instead of shipping (§6.4).
        if (text.Length == 0 || ContainsBannedWord(text, request.Voice))
        {
            result = new StateDescriptionResult(
                TemplateStateDescriptionGenerator.TemplateText(request),
                StateDescriptionSources.Template);
            text = Sanitize(result.Text);
        }

        var description = new StoredDescription(
            request.StateId,
            request.TransitionSeq,
            request.DefinitionId,
            request.LevelId,
            text,
            result.Source,
            StateVitalsHashing.TextHash(text),
            _clock.GetUtcNow());

        return await _store.SaveDescriptionAsync(description, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Normalizes text for the prompt: single line, collapsed whitespace, bounded length. Keeps the
    /// fragment cheap and prevents a multi-paragraph generation from blowing the per-provider quota.
    /// </summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var character in text)
        {
            var isSpace = char.IsWhiteSpace(character) || char.IsControl(character);
            if (isSpace)
            {
                if (!lastWasSpace && builder.Length > 0)
                    builder.Append(' ');

                lastWasSpace = true;
                continue;
            }

            builder.Append(character);
            lastWasSpace = false;
        }

        var normalized = builder.ToString().Trim();
        return normalized.Length <= StateVitalsDefaults.MaxDescriptionCharacters
            ? normalized
            : normalized[..StateVitalsDefaults.MaxDescriptionCharacters].TrimEnd();
    }

    private static bool ContainsBannedWord(string text, DescriptionVoice voice)
    {
        foreach (var word in voice.BannedWords)
        {
            if (!string.IsNullOrWhiteSpace(word)
                && text.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async ValueTask<StateDescriptionResult> GenerateSafelyAsync(
        StateDescriptionRequest request,
        CancellationToken cancellationToken)
    {
        var template = new StateDescriptionResult(
            TemplateStateDescriptionGenerator.TemplateText(request),
            StateDescriptionSources.Template);

        GenerationAttempts++;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_timeout > TimeSpan.Zero)
                timeout.CancelAfter(_timeout);

            // WaitAsync stops waiting even when the generator ignores the token, so a slow model call
            // can never block the turn (design §6.2 point 3).
            var result = await _generator.GenerateAsync(request, timeout.Token)
                .AsTask()
                .WaitAsync(timeout.Token)
                .ConfigureAwait(false);

            if (result is null || string.IsNullOrWhiteSpace(result.Text))
                return template;

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Never log the text or the request content: only the code and the definition id (§6.4).
            _log?.Warning(
                "state description generation failed code={0} definition={1} level={2} error={3}",
                StateVitalsCodes.DescriptionFallback,
                request.DefinitionId,
                request.LevelId,
                exception.GetType().Name);

            return template;
        }
    }
}
