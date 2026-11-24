using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Natter.Chat;

namespace Natter.Application.Chat;

/// <summary>
/// Rules for plugin-contributed prompt fragments. Plugin ids are namespaced with the contributing
/// plugin (<c>owner#local</c>), so fragments from different plugins can never collide and the host
/// no longer arbitrates "first wins". Plugins still may not impersonate host builtins.
/// </summary>
public static class PromptPluginFragmentRules
{
    public const string LocalIdPattern = "^[a-z0-9][a-z0-9._-]{0,63}$";

    public const string NamespacedIdPattern = "^[a-z0-9][a-z0-9._-]{0,63}#[a-z0-9][a-z0-9._-]{0,63}$";

    private static readonly Regex LocalIdRegex = new(
        LocalIdPattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex NamespacedIdRegex = new(
        NamespacedIdPattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public static bool IsValidLocalId(string? id)
        => !string.IsNullOrEmpty(id) && LocalIdRegex.IsMatch(id);

    public static bool IsValidNamespacedId(string? id)
        => !string.IsNullOrEmpty(id) && NamespacedIdRegex.IsMatch(id);

    /// <summary>Extracts the owner part of a catalog reference (<c>owner#contribution</c>).</summary>
    public static bool TryGetOwner(string reference, out string owner)
    {
        owner = string.Empty;
        if (string.IsNullOrWhiteSpace(reference))
            return false;

        var separator = reference.IndexOf('#');
        var candidate = separator > 0 ? reference[..separator] : reference;
        if (!IsValidLocalId(candidate))
            return false;

        owner = candidate;
        return true;
    }

    /// <summary>Applies the owner namespace to a plugin-local fragment id.</summary>
    public static string Namespace(string reference, string localId)
        => TryGetOwner(reference, out var owner) ? $"{owner}#{localId}" : localId;

    public static int NormalizeOrder(int order)
        => Math.Clamp(order, 0, PromptCompilerDefaults.PluginFragmentOrderMax);
}

/// <summary>Limits and timeout for the host fragment aggregator.</summary>
public sealed record PromptFragmentSourceOptions(
    TimeSpan ProviderTimeout,
    int MaxFragments,
    int MaxFragmentCharacters,
    int MaxTotalCharacters,
    int MaxFragmentsPerProvider = PromptCompilerDefaults.PluginFragmentsPerProvider,
    int MaxCharactersPerProvider = PromptCompilerDefaults.PluginCharactersPerProvider)
{
    public static PromptFragmentSourceOptions Default { get; } = new(
        TimeSpan.FromMilliseconds(PromptCompilerDefaults.PluginFragmentTimeoutMilliseconds),
        PromptCompilerDefaults.PluginMaxFragments,
        PromptCompilerDefaults.FragmentMaxCharacters,
        PromptCompilerDefaults.PluginTotalMaxCharacters);
}

/// <summary>Cleaned plugin fragments plus diagnostics (no fragment text is ever included).</summary>
public sealed record PromptFragmentSourceResult(
    IReadOnlyList<PromptFragment> Fragments,
    IReadOnlyList<string> Warnings,
    int ProviderCount,
    int RejectedCount,
    long ElapsedMilliseconds = 0)
{
    public static PromptFragmentSourceResult Empty { get; } = new([], [], 0, 0, 0);
}

/// <summary>Host-side aggregation point for <c>prompt.fragment</c> providers.</summary>
public interface IPromptFragmentSource
{
    ValueTask<PromptFragmentSourceResult> GetFragmentsAsync(
        PromptFragmentContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Builds the target facts handed to plugin fragment providers.</summary>
public static class PromptFragmentContexts
{
    public static PromptFragmentContext FromPromptRequest(PromptRequest request, string? presetId)
    {
        ArgumentNullException.ThrowIfNull(request);

        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in request.Profile.Prompt.Variables ?? new Dictionary<string, string>())
            variables[pair.Key] = pair.Value;
        foreach (var pair in request.Variables ?? new Dictionary<string, string>())
            variables[pair.Key] = pair.Value;

        return new PromptFragmentContext(
            request.Profile,
            presetId,
            request.Locale ?? request.Profile.Prompt.Locale,
            request.Source.Platform,
            request.Source.Protocol,
            request.Conversation.Kind,
            variables)
        {
            // §11.1: session identity travels as an init property so no provider signature changes.
            Conversation = ConversationScope.FromPromptRequest(request),
        };
    }
}

