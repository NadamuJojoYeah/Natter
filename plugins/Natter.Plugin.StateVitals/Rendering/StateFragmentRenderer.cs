using System.Text;

namespace Natter.Plugin.StateVitals.Rendering;

/// <summary>One vital as it may appear in the prompt: bucket ids and text only, never a float.</summary>
public sealed record VitalViewEntry(
    string VitalId,
    string LocalId,
    string Label,
    string LevelId,
    string LevelLabel,
    double Value,
    string? Description);

/// <summary>One derived or asserted state in the prompt (§11 shows them as <c>[injured]</c>).</summary>
public sealed record StateViewEntry(
    string DefinitionId,
    string LocalId,
    string Label,
    string Kind,
    string? Description);

/// <summary>Read-only projection of the snapshot used for rendering and diagnostics.</summary>
public sealed record StateView(
    string StateId,
    long Version,
    long TransitionSeq,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<VitalViewEntry> Vitals,
    IReadOnlyList<StateViewEntry> States);

/// <summary>
/// Bucketed rendering (design §4). The internal value decays smoothly but the fragment only ever
/// contains level ids, so text changes when — and only when — a bucket is crossed. That keeps the
/// plan hash stable across turns inside one level.
/// </summary>
public sealed class StateFragmentRenderer
{
    /// <summary>Renders the fragment body; empty string when there is nothing to say.</summary>
    public string Render(StateView view, bool includeDescriptions, int maxCharacters)
    {
        ArgumentNullException.ThrowIfNull(view);

        if (view.Vitals.Count == 0 && view.States.Count == 0)
            return string.Empty;

        var limit = Math.Clamp(maxCharacters, 16, 8000);
        var tags = new StringBuilder();

        foreach (var vital in view.Vitals)
        {
            if (tags.Length > 0)
                tags.Append(' ');

            tags.Append('[').Append(vital.LocalId).Append(": ").Append(vital.LevelId).Append(']');
        }

        foreach (var state in view.States)
        {
            if (tags.Length > 0)
                tags.Append(' ');

            tags.Append('[').Append(state.LocalId).Append(']');
        }

        var tagLine = tags.ToString();
        if (tagLine.Length > limit)
            return TruncateTags(tagLine, limit);

        if (!includeDescriptions)
            return tagLine;

        var described = new StringBuilder(tagLine);
        foreach (var entry in view.Vitals.Cast<object>().Concat(view.States))
        {
            var (key, description) = entry switch
            {
                VitalViewEntry vital => (vital.LocalId, vital.Description),
                StateViewEntry state => (state.LocalId, state.Description),
                _ => (string.Empty, null),
            };

            if (string.IsNullOrWhiteSpace(description))
                continue;

            var line = $"{key}: {description}";
            if (described.Length + 1 + line.Length > limit)
                break;

            described.Append('\n').Append(line);
        }

        return described.ToString();
    }

    /// <summary>Falls back to whole tags only, so the fragment never ends mid-token.</summary>
    private static string TruncateTags(string tagLine, int limit)
    {
        var builder = new StringBuilder();
        foreach (var tag in tagLine.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (builder.Length + 1 + tag.Length > limit)
                break;

            if (builder.Length > 0)
                builder.Append(' ');

            builder.Append(tag);
        }

        return builder.ToString();
    }
}
