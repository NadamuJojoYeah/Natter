using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Natter.Application.Chat;

/// <summary>Whitelisted prompt variables and their deterministic escaping rules.</summary>
public static class PromptVariables
{
    /// <summary>Only these names are substituted; anything else stays literal and produces a warning.</summary>
    public static IReadOnlyList<string> Allowed { get; } =
    [
        "profile.id",
        "platform",
        "locale",
        "conversation.kind",
        "user.name",
    ];

    public static bool IsAllowed(string name)
        => Allowed.Contains(name, StringComparer.Ordinal);

    /// <summary>
    /// Escapes a runtime value before it is substituted into fragment text: control characters and
    /// line breaks are folded to spaces (no prompt-structure injection), braces and angle brackets
    /// are folded to full-width forms (no recursive <c>{{...}}</c> expansion, no role/tool tags), and
    /// the value is capped. Variable values are never logged.
    /// </summary>
    public static string Escape(string? value, int maxCharacters = PromptCompilerDefaults.VariableMaxCharacters)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var limit = Math.Max(1, maxCharacters);
        var builder = new StringBuilder(Math.Min(value.Length, limit));
        foreach (var character in value)
        {
            if (builder.Length >= limit)
                break;

            if (character is '\r' or '\n' or '\t')
            {
                builder.Append(' ');
                continue;
            }

            if (char.IsControl(character))
                continue;

            builder.Append(character switch
            {
                '{' => '｛',
                '}' => '｝',
                '<' => '＜',
                '>' => '＞',
                _ => character,
            });
        }

        return builder.ToString().Trim();
    }
}

/// <summary>SHA-256 helpers for fragment/plan identity. Hashes never contain source text.</summary>
public static class PromptHasher
{
    public static string HashText(string text)
        => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Compact fragment hash used by <see cref="DisabledPromptFragment.Hash"/>.</summary>
    public static string ShortHash(string text)
        => HashText(text)[..(6 + 16)];

    /// <summary>
    /// PromptPlan hash: preset id/version + ordered fragment ids and text hashes + disabled ids +
    /// variable names (never values), exactly as documented in docs/PROMPT_COMPILER.md §10.
    /// </summary>
    public static string HashPlan(
        string presetId,
        string presetVersion,
        IEnumerable<(string Id, string Text)> fragments,
        IEnumerable<string> disabledIds,
        IEnumerable<string> variableNames)
    {
        var canonical = new StringBuilder();
        canonical.Append("preset=").Append(presetId).Append('\n');
        canonical.Append("version=").Append(presetVersion).Append('\n');
        foreach (var (id, text) in fragments)
            canonical.Append("fragment=").Append(id).Append(':').Append(HashText(text)).Append('\n');
        foreach (var id in disabledIds)
            canonical.Append("disabled=").Append(id).Append('\n');
        foreach (var name in variableNames)
            canonical.Append("variable=").Append(name).Append('\n');
        return HashText(canonical.ToString());
    }
}

/// <summary>
/// Merges host and plugin fragment sets while enforcing plugin limits (host-internal strict path).
/// A plugin may not impersonate host builtins or overwrite a fragment id owned by another source.
/// Plugin-contributed ids are namespaced before reaching this helper.
/// </summary>
public static class PromptFragmentMerge
{
    public static IReadOnlyList<PromptFragment> Merge(
        IEnumerable<PromptFragment>? hostFragments,
        IEnumerable<PromptFragment>? pluginFragments,
        int maxPluginFragments = PromptCompilerDefaults.PluginMaxFragments)
    {
        var merged = new List<PromptFragment>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var fragment in hostFragments ?? [])
        {
            if (!ids.Add(fragment.Id))
                throw new PromptCompilationException(
                    PromptCompilerCodes.FragmentDuplicate,
                    $"Fragment id '{fragment.Id}' is registered more than once.");
            merged.Add(fragment);
        }

        var pluginCount = 0;
        foreach (var fragment in pluginFragments ?? [])
        {
            pluginCount++;
            if (pluginCount > Math.Max(0, maxPluginFragments))
                throw new PromptCompilationException(
                    PromptCompilerCodes.PluginFragmentLimit,
                    $"A plugin may contribute at most {maxPluginFragments} fragments.");

            if (fragment.Source is PromptFragmentSource.HostBuiltin)
                throw new PromptCompilationException(
                    PromptCompilerCodes.FragmentSourceForbidden,
                    $"Plugin fragment '{fragment.Id}' claims hostBuiltin source.");

            if (!ids.Add(fragment.Id))
                throw new PromptCompilationException(
                    PromptCompilerCodes.FragmentDuplicate,
                    $"Fragment id '{fragment.Id}' is registered more than once.");

            var capped = fragment.MaxCharacters > PromptCompilerDefaults.FragmentMaxCharacters
                ? fragment with { MaxCharacters = PromptCompilerDefaults.FragmentMaxCharacters }
                : fragment;
            merged.Add(capped);
        }

        return merged;
    }
}
