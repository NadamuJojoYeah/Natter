using System.Net;
using System.Net.Sockets;

namespace Natter.Plugin.WebConsole;

/// <summary>Effective WebConsole configuration (design §5).</summary>
public sealed class WebConsoleOptions
{
    public string Bind { get; init; } = WebConsoleDefaults.Bind;

    public int Port { get; init; } = WebConsoleDefaults.Port;

    public string? PublicBaseUrl { get; init; }

    public string HostHealthUrl { get; init; } = WebConsoleDefaults.HostHealthUrl;

    public string Token { get; init; } = WebConsoleDefaults.TokenReference;

    /// <summary>
    /// Optional read-only credential. Empty/absent disables viewer logins; when set it must be the
    /// <c>${secret:WEBCONSOLE_VIEWER_TOKEN}</c> reference and the secret provider must supply it.
    /// </summary>
    public string? ViewerToken { get; init; }

    public bool ReadOnly { get; init; } = true;

    public bool AllowConfigWrite { get; init; }

    public bool AllowCommandConsole { get; init; }

    public IReadOnlyList<string> CommandAllowlist { get; init; } = [];

    /// <summary>Plugin-side budget for one command; the host runner enforces its own timeout too.</summary>
    public int CommandTimeoutSeconds { get; init; } = WebConsoleDefaults.CommandTimeoutSeconds;

    public int SessionMinutes { get; init; } = WebConsoleDefaults.SessionMinutes;

    public int MaxLogLines { get; init; } = WebConsoleDefaults.MaxLogLines;

    public IReadOnlyList<string> TrustedProxies { get; init; } = ["127.0.0.1"];
}

/// <summary>Constants shared by config, auth, server and tests.</summary>
public static class WebConsoleDefaults
{
    public const string Bind = "127.0.0.1";
    public const int Port = 5090;
    public const string TokenReference = "${secret:WEBCONSOLE_TOKEN}";
    public const string ViewerTokenReference = "${secret:WEBCONSOLE_VIEWER_TOKEN}";
    public const string TokenSecretName = "WEBCONSOLE_TOKEN";
    public const string ViewerTokenSecretName = "WEBCONSOLE_VIEWER_TOKEN";
    public const string HostHealthUrl = "http://127.0.0.1:5099/health";
    public const int SessionMinutes = 60;
    public const int MaxLogLines = 500;
    public const int MaxRequestBodyBytes = 64 * 1024;
    public const int MaxLoginBodyBytes = 4 * 1024;
    public const int MaxAdminBodyBytes = 256 * 1024 + 4 * 1024;
    public const int MaxCommandBodyBytes = 8 * 1024;
    public const int CommandTimeoutSeconds = 10;
    public const int MinTokenLength = 24;
    public const string SessionCookieName = "natter_webconsole";
    public const string CsrfHeaderName = "X-WebConsole-CSRF";
    public const string AuditDirectoryName = "audit";
    public const string AuditStateDirectoryName = "webconsole";
    public const string StateDirectoryEnvironmentVariable = "NATTER_STATE_DIR";
    public const string AuditFileName = "webconsole-audit.jsonl";
    public const string DefaultDocumentationFile = "docs/WEBCONSOLE_HOST_API_GAP.md";
}

/// <summary>One configuration problem; <see cref="Pointer"/> mirrors the JSON Pointer style used by NPS.</summary>
public sealed record WebConsoleOptionIssue(string Pointer, string Code, string Message);

/// <summary>Static validation executed before the server binds a port.</summary>
public static class WebConsoleOptionsValidator
{
    public static IReadOnlyList<WebConsoleOptionIssue> Validate(WebConsoleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var issues = new List<WebConsoleOptionIssue>();

        if (!WebConsoleNetwork.IsLoopbackHost(options.Bind))
        {
            issues.Add(new WebConsoleOptionIssue(
                "/bind",
                "webconsole.bind-not-loopback",
                $"Bind address '{options.Bind}' is not loopback. Phase 1 only listens on 127.0.0.1, ::1 or localhost."));
        }

        if (options.Port is < 1 or > 65535)
        {
            issues.Add(new WebConsoleOptionIssue(
                "/port",
                "webconsole.port-invalid",
                $"Port '{options.Port}' is outside 1..65535."));
        }

        if (!string.Equals(options.Token, WebConsoleDefaults.TokenReference, StringComparison.Ordinal))
        {
            issues.Add(new WebConsoleOptionIssue(
                "/token",
                "webconsole.token-reference-required",
                $"token must be '{WebConsoleDefaults.TokenReference}'; plaintext tokens are not accepted."));
        }

        if (!string.IsNullOrEmpty(options.ViewerToken)
            && !string.Equals(options.ViewerToken, WebConsoleDefaults.ViewerTokenReference, StringComparison.Ordinal))
        {
            issues.Add(new WebConsoleOptionIssue(
                "/viewerToken",
                "webconsole.viewer-token-reference-required",
                $"viewerToken must be empty or '{WebConsoleDefaults.ViewerTokenReference}'; plaintext tokens are not accepted."));
        }


        if (!TryParseLoopbackUrl(options.HostHealthUrl, out var healthUri))
        {
            issues.Add(new WebConsoleOptionIssue(
                "/hostHealthUrl",
                "webconsole.health-url-invalid",
                "hostHealthUrl must be an absolute http(s) URL on a loopback host (for example http://127.0.0.1:5099/health)."));
        }
        else if (!string.Equals(healthUri!.AbsolutePath, "/health", StringComparison.Ordinal))
        {
            issues.Add(new WebConsoleOptionIssue(
                "/hostHealthUrl",
                "webconsole.health-url-path",
                "hostHealthUrl must point at the documented '/health' endpoint."));
        }

        if (!string.IsNullOrWhiteSpace(options.PublicBaseUrl)
            && !TryParseBaseUrl(options.PublicBaseUrl!, out _))
        {
            issues.Add(new WebConsoleOptionIssue(
                "/publicBaseUrl",
                "webconsole.public-base-url-invalid",
                "publicBaseUrl must be an absolute http(s) URL without query/fragment and with path '/'."));
        }

        if (options.SessionMinutes is < 1 or > 1440)
        {
            issues.Add(new WebConsoleOptionIssue(
                "/sessionMinutes",
                "webconsole.session-minutes-invalid",
                "sessionMinutes must be between 1 and 1440."));
        }

        if (options.MaxLogLines is < 1 or > 10000)
        {
            issues.Add(new WebConsoleOptionIssue(
                "/maxLogLines",
                "webconsole.max-log-lines-invalid",
                "maxLogLines must be between 1 and 10000."));
        }

        if (options.CommandAllowlist.Count > 64)
        {
            issues.Add(new WebConsoleOptionIssue(
                "/commandAllowlist",
                "webconsole.command-allowlist-invalid",
                "commandAllowlist must contain at most 64 entries."));
        }

        foreach (var entry in options.CommandAllowlist)
        {
            var normalized = WebConsoleCommandPolicy.NormalizeCommand(entry);
            if (normalized.Length == 0
                || normalized.Length > WebConsoleCommandPolicy.MaxCommandLength
                || entry.Any(char.IsControl))
            {
                issues.Add(new WebConsoleOptionIssue(
                    "/commandAllowlist",
                    "webconsole.command-allowlist-invalid",
                    $"commandAllowlist entry '{entry}' is empty, too long or contains control characters."));
            }
        }

        foreach (var proxy in options.TrustedProxies)
        {
            if (!WebConsoleNetwork.TryParseCidr(proxy, out _, out _))
            {
                issues.Add(new WebConsoleOptionIssue(
                    "/trustedProxies",
                    "webconsole.trusted-proxy-invalid",
                    $"trustedProxies entry '{proxy}' is not an IP address or CIDR."));
            }
        }

        if (options.CommandTimeoutSeconds is < 1 or > 10)
        {
            issues.Add(new WebConsoleOptionIssue(
                "/commandTimeoutSeconds",
                "webconsole.command-timeout-invalid",
                "commandTimeoutSeconds must be between 1 and 10 so the console can answer before the HTTP timeout."));
        }

        if (options.AllowConfigWrite && options.ReadOnly)
        {
            issues.Add(new WebConsoleOptionIssue(
                "/allowConfigWrite",
                "webconsole.write-conflict",
                "allowConfigWrite=true conflicts with readOnly=true; set readOnly=false to allow writes."));
        }

        return issues;
    }

    public static bool TryParseLoopbackUrl(string? value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed))
            return false;

        if (parsed.Scheme is not ("http" or "https"))
            return false;

        if (!string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Fragment))
            return false;

        if (!WebConsoleNetwork.IsLoopbackHost(parsed.Host))
            return false;

        uri = parsed;
        return true;
    }

    public static bool TryParseBaseUrl(string value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed))
            return false;

        if (parsed.Scheme is not ("http" or "https"))
            return false;

        if (!string.IsNullOrEmpty(parsed.UserInfo)
            || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment))
        {
            return false;
        }

        if (!string.Equals(parsed.AbsolutePath, "/", StringComparison.Ordinal))
            return false;

        uri = parsed;
        return true;
    }
}

/// <summary>Loopback and CIDR helpers (no third-party network dependency).</summary>
public static class WebConsoleNetwork
{
    public static bool IsLoopbackHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        var normalized = host.Trim('[', ']');
        return IPAddress.TryParse(normalized, out var address) && IPAddress.IsLoopback(address);
    }

    public static bool TryParseCidr(string? value, out IPAddress network, out int prefixLength)
    {
        network = IPAddress.None;
        prefixLength = 0;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();
        var slash = text.IndexOf('/');
        var addressText = slash < 0 ? text : text[..slash];

        if (!IPAddress.TryParse(addressText.Trim('[', ']'), out var address))
            return false;

        var maxPrefix = address.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        var prefix = maxPrefix;

        if (slash >= 0 && (!int.TryParse(text[(slash + 1)..], out prefix) || prefix < 0 || prefix > maxPrefix))
            return false;

        network = address;
        prefixLength = prefix;
        return true;
    }

    public static bool Contains(IPAddress network, int prefixLength, IPAddress candidate)
    {
        if (network.AddressFamily != candidate.AddressFamily)
            return false;

        var networkBytes = network.GetAddressBytes();
        var candidateBytes = candidate.GetAddressBytes();
        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        for (var index = 0; index < fullBytes; index++)
        {
            if (networkBytes[index] != candidateBytes[index])
                return false;
        }

        if (remainingBits == 0)
            return true;

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (networkBytes[fullBytes] & mask) == (candidateBytes[fullBytes] & mask);
    }

    public static bool IsInAnyCidr(string? addressText, IReadOnlyList<string> cidrs)
    {
        if (string.IsNullOrWhiteSpace(addressText) || !IPAddress.TryParse(addressText, out var address))
            return false;

        foreach (var cidr in cidrs)
        {
            if (TryParseCidr(cidr, out var network, out var prefix) && Contains(network, prefix, address))
                return true;
        }

        return false;
    }

    public static string? NormalizeAddress(string? addressText)
    {
        if (string.IsNullOrWhiteSpace(addressText))
            return null;

        var text = addressText.Trim();
        if (text.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase)
            && IPAddress.TryParse(text, out var mapped)
            && mapped.IsIPv4MappedToIPv6)
        {
            text = mapped.MapToIPv4().ToString();
        }

        return IPAddress.TryParse(text, out var address) ? address.ToString() : null;
    }
}
