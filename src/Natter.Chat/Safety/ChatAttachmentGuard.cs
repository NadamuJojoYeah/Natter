using System.Net;
using System.Net.Sockets;

namespace Natter.Chat.Safety;

/// <summary>Attachment admission policy applied before download or outbound send.</summary>
public sealed record ChatAttachmentPolicy
{
    /// <summary>When false (default) loopback, private, link-local and CGNAT literals are rejected.</summary>
    public bool AllowPrivateNetworkUrls { get; init; }

    public long? MaxBytes { get; init; }

    /// <summary>MIME prefixes such as <c>image/</c>; null allows known MIME types.</summary>
    public IReadOnlyList<string>? AllowedMimePrefixes { get; init; }
}

/// <summary>
/// v0.1 attachment rules: absolute http(s) only, no base64/local paths, optional size and
/// MIME limits, and no redirects/literals into private networks.
/// </summary>
public static class ChatAttachmentGuard
{
    public static string? Validate(Attachment? attachment, ChatAttachmentPolicy? policy = null)
    {
        if (attachment is null)
            return "attachment must not be null.";

        if (string.IsNullOrWhiteSpace(attachment.Url))
            return "attachment.url is required.";

        if (!Uri.TryCreate(attachment.Url, UriKind.Absolute, out var uri))
            return "attachment.url must be an absolute URL.";

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return "attachment.url must use http or https.";

        if (!string.IsNullOrEmpty(uri.UserInfo))
            return "attachment.url must not contain credentials.";

        if (!(policy?.AllowPrivateNetworkUrls ?? false) && IsPrivateNetworkHost(uri.Host))
            return "attachment.url must not target a private or loopback network.";

        if (policy?.MaxBytes is { } maxBytes && attachment.Size is { } size && size > maxBytes)
            return $"attachment.size ({size}) exceeds the limit ({maxBytes}).";

        if (policy?.AllowedMimePrefixes is { Count: > 0 } prefixes
            && !string.IsNullOrWhiteSpace(attachment.MimeType)
            && !prefixes.Any(prefix => attachment.MimeType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return $"attachment.mimeType '{attachment.MimeType}' is not allowed.";
        }

        return null;
    }

    /// <summary>Convenience check used by adapters that only have a URL at hand.</summary>
    public static bool IsAllowedUrl(string? url, bool allowPrivateNetworks = false)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        return Validate(new Attachment(url), new ChatAttachmentPolicy { AllowPrivateNetworkUrls = allowPrivateNetworks }) is null;
    }

    public static bool IsPrivateNetworkUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return IsPrivateNetworkHost(uri.Host);
    }

    private static bool IsPrivateNetworkHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        var normalized = host.Trim('[', ']').TrimEnd('.').ToLowerInvariant();

        if (normalized is "localhost" or "localhost.localdomain"
            || normalized.EndsWith(".localhost", StringComparison.Ordinal)
            || normalized.EndsWith(".local", StringComparison.Ordinal)
            || normalized.EndsWith(".internal", StringComparison.Ordinal))
        {
            return true;
        }

        if (!IPAddress.TryParse(normalized, out var address))
        {
            // Hostnames are not resolved in the model layer: a DNS name can still resolve
            // to a private address. Adapters that perform downloads must re-check the
            // resolved endpoint and every redirect (SSRF defence in depth).
            return false;
        }

        return IsPrivateAddress(address);
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 0
                   || bytes[0] == 10
                   || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                   || (bytes[0] == 192 && bytes[1] == 168)
                   || (bytes[0] == 169 && bytes[1] == 254)
                   || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            var uniqueLocal = (bytes[0] & 0xFE) == 0xFC;
            var linkLocal = bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80;
            var siteLocal = bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0xC0;
            return uniqueLocal || linkLocal || siteLocal || address.Equals(IPAddress.IPv6Any);
        }

        return false;
    }
}
