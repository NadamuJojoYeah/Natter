using System.Net;
using System.Net.Sockets;

namespace Natter.Chat.DingTalk;

/// <summary>
/// Validation for the <c>sessionWebhook</c> that DingTalk returns inside a callback payload.
/// The webhook is attacker-influenced data: a forged/compromised callback could point it at an
/// arbitrary host and turn inbound chat into an SSRF / credential-exfiltration primitive
/// (S2-A5). Only <c>https</c> on DingTalk-owned hosts is ever posted to.
/// </summary>
public static class DingTalkSessionWebhookPolicy
{
    /// <summary>Stable rejection code surfaced by the transport and in health diagnostics.</summary>
    public const string RejectedCode = "session_webhook_rejected";

    /// <summary>Exact hosts DingTalk uses for robot/session replies.</summary>
    private static readonly string[] ExactHosts =
    [
        "oapi.dingtalk.com",
        "api.dingtalk.com",
    ];

    /// <summary>Official wildcard suffix; <c>evil-dingtalk.com</c> does not match.</summary>
    private const string OfficialSuffix = ".dingtalk.com";

    /// <summary>True when <paramref name="host"/> is an official DingTalk API host.</summary>
    public static bool IsAllowedHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        // IP literals are never DingTalk (and are the classic SSRF payload).
        if (IPAddress.TryParse(host, out var address)
            && address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
        {
            return false;
        }

        if (ExactHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
            return true;

        return host.EndsWith(OfficialSuffix, StringComparison.OrdinalIgnoreCase)
               && host.Length > OfficialSuffix.Length;
    }

    /// <summary>
    /// Validates a sessionWebhook. <paramref name="reason"/> is a stable machine-readable token
    /// (<c>missing</c>, <c>invalid_url</c>, <c>scheme_not_https</c>, <c>host_not_allowed</c>,
    /// <c>credentials_in_url</c>, <c>non_default_port</c>) so operators can grep one code.
    /// </summary>
    public static bool TryValidate(string? webhook, out Uri? uri, out string? reason)
    {
        uri = null;
        reason = null;

        if (string.IsNullOrWhiteSpace(webhook))
        {
            reason = "missing";
            return false;
        }

        if (!Uri.TryCreate(webhook, UriKind.Absolute, out var parsed))
        {
            reason = "invalid_url";
            return false;
        }

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            reason = "scheme_not_https";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            reason = "credentials_in_url";
            return false;
        }

        if (parsed.Port != 443)
        {
            reason = "non_default_port";
            return false;
        }

        if (!IsAllowedHost(parsed.Host))
        {
            reason = "host_not_allowed";
            return false;
        }

        uri = parsed;
        return true;
    }
}
