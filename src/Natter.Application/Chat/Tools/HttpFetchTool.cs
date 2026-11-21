using System.Net;
using System.Net.Sockets;

namespace Natter.Application.Chat;

/// <summary>
/// Small read-only HTTP tool with an explicit host allowlist, response limit and SSRF guards:
/// private/loopback/link-local/metadata addresses are rejected before any request (IP literal or
/// DNS resolution), redirects are never followed and a response that came from another origin is
/// rejected. The host additionally installs a <c>ConnectCallback</c> so the address actually used
/// for the socket connection is validated (DNS rebinding / redirect defence).
/// </summary>
public sealed class HttpFetchTool(
    HttpClient httpClient,
    IReadOnlySet<string>? allowedHosts = null,
    int maxBytes = 64 * 1024,
    Func<string, CancellationToken, ValueTask<IPAddress[]>>? addressResolver = null) : ITool
{
    private readonly HttpClient _http = httpClient;
    private readonly IReadOnlySet<string> _allowed = allowedHosts ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxBytes = Math.Clamp(maxBytes, 1, 256 * 1024);
    private readonly Func<string, CancellationToken, ValueTask<IPAddress[]>> _resolveAddresses =
        addressResolver ?? ResolveAsync;

    public string Name => "http-fetch";

    public string Description => "Fetches a read-only HTTPS URL from the configured host allowlist.";

    public ToolCapabilities Capabilities => ToolCapabilities.ReadOnly | ToolCapabilities.Network;

    /// <summary>True for addresses a network tool must never reach (private, loopback, metadata, ...).</summary>
    public static bool IsBlockedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.Broadcast))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] is 0 or 10 or 127
                || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)          // carrier-grade NAT
                || (bytes[0] == 169 && bytes[1] == 254)                        // link-local + cloud metadata
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 198 && bytes[1] is 18 or 19)                   // benchmarking range
                || bytes[0] >= 224;                                            // multicast + reserved + broadcast
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
                return true;

            // Unique local addresses fc00::/7.
            var bytes = address.GetAddressBytes();
            return (bytes[0] & 0xFE) == 0xFC;
        }

        return true;
    }

    public async ValueTask<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(request.Input, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
            return new ToolResult("", "invalid_url", "Only absolute HTTPS URLs are allowed.");
        if (!_allowed.Contains(uri.Host) && !_allowed.Contains(uri.DnsSafeHost))
            return new ToolResult("", "host_not_allowed", "Host is not in the tool allowlist.");

        if (await ValidateAddressAsync(uri, cancellationToken).ConfigureAwait(false) is { } addressFailure)
            return addressFailure;

        try
        {
            using var response = await _http
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (CheckRedirect(uri, response) is { } redirectFailure)
                return redirectFailure;

            if (!response.IsSuccessStatusCode)
                return new ToolResult("", "http_error", $"HTTP {(int)response.StatusCode}.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[Math.Min(_maxBytes, 16 * 1024)];
            using var output = new MemoryStream();
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                var take = Math.Min(read, _maxBytes - (int)output.Length);
                if (take > 0)
                    output.Write(buffer, 0, take);
                if (output.Length >= _maxBytes)
                    break;
            }

            return new ToolResult(System.Text.Encoding.UTF8.GetString(output.ToArray()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ToolResult("", "http_failed", ex.GetType().Name);
        }
    }

    /// <summary>Rejects private/loopback/link-local/metadata targets before any request is sent.</summary>
    private async ValueTask<ToolResult?> ValidateAddressAsync(Uri uri, CancellationToken cancellationToken)
    {
        var host = uri.DnsSafeHost;
        if (IPAddress.TryParse(host, out var literal))
            return IsBlockedAddress(literal) ? Blocked(host) : null;

        IPAddress[] addresses;
        try
        {
            addresses = await _resolveAddresses(host, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or InvalidOperationException)
        {
            return new ToolResult("", "address_unresolved", $"Host '{host}' could not be resolved.");
        }

        if (addresses.Length == 0)
            return new ToolResult("", "address_unresolved", $"Host '{host}' did not resolve to any address.");

        foreach (var address in addresses)
        {
            if (IsBlockedAddress(address))
                return Blocked(host);
        }

        return null;
    }

    private static ToolResult? CheckRedirect(Uri requested, HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
            return new ToolResult("", "redirect_blocked", $"Redirects are not followed (HTTP {status}).");

        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null)
            return null;

        if (!string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(finalUri.DnsSafeHost, requested.DnsSafeHost, StringComparison.OrdinalIgnoreCase))
        {
            return new ToolResult(
                "",
                "redirect_blocked",
                "The response came from a different origin; redirects are not followed.");
        }

        return null;
    }

    private static ToolResult Blocked(string host)
        => new("", "address_not_allowed", $"Host '{host}' resolves to a private, loopback or metadata address.");

    private static async ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
        => await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
}
