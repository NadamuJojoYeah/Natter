using System.Text.RegularExpressions;

namespace Natter.Application.Admin;

/// <summary>
/// Whitelist for editable config documents. Only <c>profiles</c> and
/// <c>plugin:&lt;pluginId&gt;</c> are addressable; anything that could resolve to a path outside the
/// config directory, or to a secret file, is rejected before any IO happens.
/// </summary>
public static class AdminConfigDocumentPolicy
{
    public const int MaxDocumentBytes = 256 * 1024;

    public const int MaxPluginIdLength = 128;

    public const string ProfilesId = "profiles";

    public const string PluginPrefix = "plugin:";

    /// <summary>Same shape as NPS plugin ids: <c>^[a-z0-9]+(?:[._-][a-z0-9]+)+$</c>.</summary>
    private static readonly Regex PluginIdPattern = new(
        @"^[a-z0-9]+(?:[._-][a-z0-9]+)+$",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    /// <summary>File names that are reserved for the profiles document or for secret material.</summary>
    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "profiles.json",
        "secrets.json",
        "secrets.local.json",
        "credentials.json",
        "credentials.local.json",
        ".env",
    };

    /// <summary>Normalizes and validates one document id.</summary>
    public static bool TryNormalize(
        string? id,
        out string normalizedId,
        out string code,
        out string message)
    {
        normalizedId = string.Empty;
        code = AdminCodes.ConfigDocumentNotAllowed;
        message = "Document id must be 'profiles' or 'plugin:<pluginId>'.";

        var candidate = (id ?? string.Empty).Trim();
        if (candidate.Length == 0)
        {
            message = "Document id must not be empty.";
            return false;
        }

        if (candidate.Length > MaxPluginIdLength + PluginPrefix.Length)
        {
            message = $"Document id exceeds {MaxPluginIdLength + PluginPrefix.Length} characters.";
            return false;
        }

        // Reject any path-like input outright (defense in depth: ids are never joined raw).
        if (candidate.Contains('/') || candidate.Contains('\\') || candidate.Contains('\0')
            || candidate.Contains("..", StringComparison.Ordinal))
        {
            message = "Document id must not contain path separators or parent segments.";
            return false;
        }

        if (string.Equals(candidate, ProfilesId, StringComparison.Ordinal))
        {
            normalizedId = ProfilesId;
            code = string.Empty;
            message = string.Empty;
            return true;
        }

        if (!candidate.StartsWith(PluginPrefix, StringComparison.Ordinal))
        {
            message = "Document id must be 'profiles' or 'plugin:<pluginId>'.";
            return false;
        }

        var pluginId = candidate[PluginPrefix.Length..];
        if (pluginId.Length == 0 || pluginId.Length > MaxPluginIdLength || !PluginIdPattern.IsMatch(pluginId))
        {
            message = $"Plugin id must match ^[a-z0-9]+(?:[._-][a-z0-9]+)+$ and be at most {MaxPluginIdLength} characters.";
            return false;
        }

        if (IsSecretFileName(pluginId + ".json"))
        {
            message = $"Document 'plugin:{pluginId}' resolves to a reserved config file.";
            return false;
        }

        normalizedId = PluginPrefix + pluginId;
        code = string.Empty;
        message = string.Empty;
        return true;
    }

    /// <summary>Extracts the plugin id from a normalized <c>plugin:&lt;id&gt;</c> document id.</summary>
    public static bool TryGetPluginId(string normalizedId, out string pluginId)
    {
        pluginId = string.Empty;
        if (!normalizedId.StartsWith(PluginPrefix, StringComparison.Ordinal))
            return false;

        pluginId = normalizedId[PluginPrefix.Length..];
        return pluginId.Length > 0;
    }

    /// <summary>File name inside the host config directory for a normalized document id.</summary>
    public static string FileNameFor(string normalizedId)
        => TryGetPluginId(normalizedId, out var pluginId) ? pluginId + ".json" : "profiles.json";

    /// <summary>True for file names reserved for secret material or for the profiles document.</summary>
    public static bool IsSecretFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return true;

        var name = Path.GetFileName(fileName.Trim());
        if (ReservedFileNames.Contains(name))
            return true;

        return name.EndsWith(".secret.json", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".secrets.json", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".key", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reload hint per document kind: profiles are host-level, plugin configs are plugin-level.</summary>
    public static AdminConfigReloadHint ReloadHintFor(string normalizedId)
        => TryGetPluginId(normalizedId, out _)
            ? AdminConfigReloadHint.RestartPlugin
            : AdminConfigReloadHint.RestartHost;
}
