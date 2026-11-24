namespace Natter.Plugin.Abstractions;

/// <summary>
/// Naming rule: <c>natter.&lt;kind&gt;.&lt;domain&gt;.&lt;name&gt;</c>, where the second segment must
/// equal the manifest kind. Install directories use the full id.
/// </summary>
public static class PluginNaming
{
    public const string Prefix = "natter.";

    public static string ExpectedPrefix(PluginKind kind)
        => Prefix + kind.ToString().ToLowerInvariant() + ".";

    public static bool IdMatchesKind(string? id, PluginKind kind)
        => !string.IsNullOrWhiteSpace(id)
           && id.StartsWith(ExpectedPrefix(kind), StringComparison.Ordinal);
}
