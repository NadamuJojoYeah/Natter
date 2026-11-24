namespace Natter.Plugin.WebConsole;

/// <summary>What an HTTP request needs from the caller.</summary>
public enum WebConsoleAccess
{
    /// <summary>Read-only endpoints plus the non-mutating <c>/validate</c> checks.</summary>
    Read = 0,

    /// <summary>Anything that could change host or plugin state (save, rollback, command run).</summary>
    Manage = 1,
}

/// <summary>
/// Server-side authorization for the two console roles. The UI hides controls, but this policy is
/// the enforcement point: every authenticated <c>/api/*</c> request is classified and checked.
/// </summary>
public static class WebConsoleAuthorization
{
    public const string ForbiddenCode = "auth.forbidden";
    public const string ReadOnlyCode = "console.read_only";
    public const string ConfigWriteDisabledCode = "config.write_disabled";
    public const string CommandDisabledCode = "command.disabled";

    /// <summary>Paths that are POST but never mutate state (they only run a pure policy check).</summary>
    private static readonly string[] NonMutatingSuffixes = ["/validate"];

    public static bool IsAuthenticatedSessionRoute(string path)
        => string.Equals(path, "/api/session", StringComparison.Ordinal);

    /// <summary>Classifies a request by method and path. Unknown verbs are treated as mutating.</summary>
    public static WebConsoleAccess Classify(string method, string path)
    {
        method ??= string.Empty;

        if (method is "GET" or "HEAD" or "OPTIONS")
            return WebConsoleAccess.Read;

        if (string.Equals(method, "POST", StringComparison.Ordinal)
            && NonMutatingSuffixes.Any(suffix => path.EndsWith(suffix, StringComparison.Ordinal)))
        {
            return WebConsoleAccess.Read;
        }

        return WebConsoleAccess.Manage;
    }

    public static bool IsAllowed(WebConsoleRole role, WebConsoleAccess access)
        => access == WebConsoleAccess.Read || role == WebConsoleRole.Admin;

    public static string RoleName(WebConsoleRole role)
        => role == WebConsoleRole.Admin ? "admin" : "viewer";

    public static string RequiredRoleName(WebConsoleAccess access)
        => access == WebConsoleAccess.Manage ? "admin" : "viewer";

    /// <summary>Why a manage request was refused before reaching the feature switch (null = allowed).</summary>
    public static string? GateCode(WebConsoleOptions options, WebConsoleAccess access)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (access == WebConsoleAccess.Read)
            return null;

        // One code for "the console is running read-only", independent of which feature was asked for.
        return options.ReadOnly ? ReadOnlyCode : null;
    }
}
