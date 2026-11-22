using System.Net;
using System.Net.Sockets;

namespace Natter.Plugin.ConversationConsole;

/// <summary>Effective Conversation Console configuration (design §4).</summary>
public sealed class ConversationConsoleOptions
{
    public string Bind { get; init; } = ConversationConsoleDefaults.Bind;

    public int Port { get; init; } = ConversationConsoleDefaults.Port;

    public string Token { get; init; } = ConversationConsoleDefaults.TokenReference;

    /// <summary>
    /// Default true, and P1 refuses <c>false</c>: the plugin has no write path at all, so accepting
    /// a "writable" configuration would advertise a capability that does not exist (design §4.3).
    /// </summary>
    public bool ReadOnly { get; init; } = true;

    /// <summary>Conversation page size used when the caller does not ask for one.</summary>
    public int PageSize { get; init; } = ConversationConsoleDefaults.PageSize;

    /// <summary>Message page size used when the caller does not ask for one.</summary>
    public int MessagePageSize { get; init; } = ConversationConsoleDefaults.MessagePageSize;

    /// <summary>Whole-request budget (body read included), in seconds.</summary>
    public int RequestTimeoutSeconds { get; init; } = ConversationConsoleDefaults.RequestTimeoutSeconds;

    /// <summary>Concurrent request slots; a saturated console answers 503 instead of queueing.</summary>
    public int MaxConcurrentRequests { get; init; } = ConversationConsoleDefaults.MaxConcurrentRequests;

    /// <summary>How many rotated audit files are kept (<c>.1</c> is the newest).</summary>
    public int AuditRetentionFiles { get; init; } = ConversationConsoleDefaults.AuditRetentionFiles;
}

/// <summary>Constants shared by config, auth, server and tests. One place, no drift.</summary>
public static class ConversationConsoleDefaults
{
    public const string Bind = "127.0.0.1";
    public const int Port = 5091;

    /// <summary>
    /// Deliberately a *different* secret from WebConsole's <c>WEBCONSOLE_TOKEN</c>: the two consoles
    /// have different threat models and must be revocable independently (design §4.1).
    /// </summary>
    public const string TokenReference = "${secret:CONVERSATION_CONSOLE_TOKEN}";

    public const string TokenSecretName = "CONVERSATION_CONSOLE_TOKEN";
    public const int MinTokenLength = 24;

    public const int PageSize = 25;
    public const int MessagePageSize = 50;
    public const int RequestTimeoutSeconds = 15;
    public const int MaxRequestTimeoutSeconds = 120;
    public const int MaxConcurrentRequests = 16;
    public const int MaxConcurrentRequestsLimit = 128;
    public const int AuditRetentionFiles = 3;
    public const int MaxAuditRetentionFiles = 20;

    public const int MaxRequestBodyBytes = 4 * 1024;

    /// <summary>State root environment variable shared with every other Natter plugin.</summary>
    public const string StateDirectoryEnvironmentVariable = "NATTER_STATE_DIR";

    /// <summary>Subdirectory under the state root; the plugin directory is never written to.</summary>
    public const string StateDirectoryName = "conversation-console";

    public const string AuditDirectoryName = "audit";
    public const string AuditFileName = "conversation-console-audit.jsonl";

    public const string AuthorizationHeader = "Authorization";
    public const string BearerPrefix = "Bearer ";
}

/// <summary>One configuration problem; <see cref="Pointer"/> mirrors the JSON Pointer style of NPS.</summary>
public sealed record ConversationConsoleOptionIssue(string Pointer, string Code, string Message);

/// <summary>Static validation executed before the server binds a port.</summary>
public static class ConversationConsoleOptionsValidator
{
    public static IReadOnlyList<ConversationConsoleOptionIssue> Validate(ConversationConsoleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var issues = new List<ConversationConsoleOptionIssue>();

        if (!ConversationConsoleNetwork.IsLoopbackHost(options.Bind))
        {
            issues.Add(new(
                "/bind",
                "conversation-console.bind-not-loopback",
                $"Bind address '{options.Bind}' is not loopback. The console only listens on 127.0.0.1, ::1 or localhost."));
        }

        if (options.Port is < 1 or > 65535)
        {
            issues.Add(new(
                "/port",
                "conversation-console.port-invalid",
                $"Port '{options.Port}' is outside 1..65535."));
        }

        if (!string.Equals(options.Token, ConversationConsoleDefaults.TokenReference, StringComparison.Ordinal))
        {
            issues.Add(new(
                "/token",
                "conversation-console.token-reference-required",
                $"token must be '{ConversationConsoleDefaults.TokenReference}'; plaintext tokens are not accepted."));
        }

        if (!options.ReadOnly)
        {
            issues.Add(new(
                "/readOnly",
                "conversation-console.read-only-required",
                "P1 has no write path; readOnly=false would advertise a capability that does not exist. "
                + "Keep readOnly=true until a later phase ships writes."));
        }

        if (options.PageSize is < 1 or > Natter.Application.Chat.ConversationQueryLimits.MaxPageSize)
        {
            issues.Add(new(
                "/pageSize",
                "conversation-console.page-size-invalid",
                $"pageSize must be between 1 and {Natter.Application.Chat.ConversationQueryLimits.MaxPageSize}."));
        }

        if (options.MessagePageSize is < 1 or > Natter.Application.Chat.ConversationQueryLimits.MaxMessagePageSize)
        {
            issues.Add(new(
                "/messagePageSize",
                "conversation-console.message-page-size-invalid",
                $"messagePageSize must be between 1 and {Natter.Application.Chat.ConversationQueryLimits.MaxMessagePageSize}."));
        }

        if (options.RequestTimeoutSeconds is < 1 or > ConversationConsoleDefaults.MaxRequestTimeoutSeconds)
        {
            issues.Add(new(
                "/requestTimeoutSeconds",
                "conversation-console.request-timeout-invalid",
                $"requestTimeoutSeconds must be between 1 and {ConversationConsoleDefaults.MaxRequestTimeoutSeconds}."));
        }

        if (options.MaxConcurrentRequests is < 1 or > ConversationConsoleDefaults.MaxConcurrentRequestsLimit)
        {
            issues.Add(new(
                "/maxConcurrentRequests",
                "conversation-console.max-concurrent-invalid",
                $"maxConcurrentRequests must be between 1 and {ConversationConsoleDefaults.MaxConcurrentRequestsLimit}."));
        }

        if (options.AuditRetentionFiles is < ConversationConsoleDefaults.AuditRetentionFiles
            or > ConversationConsoleDefaults.MaxAuditRetentionFiles)
        {
            issues.Add(new(
                "/auditRetentionFiles",
                "conversation-console.audit-retention-invalid",
                "auditRetentionFiles must be between "
                + $"{ConversationConsoleDefaults.AuditRetentionFiles} and {ConversationConsoleDefaults.MaxAuditRetentionFiles}."));
        }

        return issues;
    }
}

/// <summary>Loopback helpers (no third-party dependency, no DNS resolution).</summary>
public static class ConversationConsoleNetwork
{
    public static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        var normalized = host.Trim('[', ']');
        return IPAddress.TryParse(normalized, out var address) && IPAddress.IsLoopback(address);
    }

    /// <summary>Formats a <c>host:port</c> base URL, bracketing IPv6 literals.</summary>
    public static string BuildUrl(string bind, int port)
        => string.Equals(bind, "localhost", StringComparison.OrdinalIgnoreCase)
            ? $"http://localhost:{port}"
            : bind.Contains(':', StringComparison.Ordinal)
                ? $"http://[{bind}]:{port}"
                : $"http://{bind}:{port}";

    /// <summary>Parses an <c>Origin</c> header into host and port without trusting its path.</summary>
    public static bool TryGetOriginAuthority(string? origin, out string authority)
    {
        authority = string.Empty;
        if (string.IsNullOrWhiteSpace(origin) || string.Equals(origin, "null", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme is not ("http" or "https"))
            return false;

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return false;

        authority = uri.IsDefaultPort
            ? uri.Host
            : uri.Host + ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>Local address family helper used by the listener (loopback only).</summary>
    public static AddressFamily Family(string bind)
        => IPAddress.TryParse(bind.Trim('[', ']'), out var address)
            ? address.AddressFamily
            : AddressFamily.InterNetwork;
}
