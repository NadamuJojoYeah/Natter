using System.Text.RegularExpressions;

namespace Natter.Plugin.Store;

/// <summary>
/// Single source of truth for plugin ids accepted by the app-private store. The same shape as the
/// NPS manifest id rule; store paths are derived from this value, so anything else is rejected
/// before a path is built or deleted (S1-01).
/// </summary>
public static partial class PluginStoreIds
{
    public const int MaxLength = 64;

    public const string Pattern = "^[a-z0-9][a-z0-9._-]{0,63}$";

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant, 1_000)]
    private static partial Regex IdPattern();

    public static bool IsValid(string? pluginId)
        => !string.IsNullOrEmpty(pluginId)
           && pluginId.Length <= MaxLength
           && IdPattern().IsMatch(pluginId)
           && !pluginId.Contains("..", StringComparison.Ordinal);

    /// <summary>Throws <see cref="PluginStoreException"/> for anything that is not a manifest-safe id.</summary>
    public static string Validate(string? pluginId)
    {
        if (IsValid(pluginId))
            return pluginId!;

        throw new PluginStoreException(
            $"invalid plugin id '{pluginId}'",
            [new PluginPackageIssue("plugin-id-invalid", $"Plugin id must match {Pattern}.")]);
    }
}
