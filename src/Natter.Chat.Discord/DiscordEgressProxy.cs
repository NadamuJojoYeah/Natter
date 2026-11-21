using System.Net;

namespace Natter.Chat.Discord;

/// <summary>
/// Discord.Net's REST and gateway defaults deliberately ignore the process proxy settings:
/// <c>DefaultRestClientProvider.Instance</c> is created with <c>useProxy: false</c>, and
/// <c>DefaultWebSocketClient</c> assigns <see cref="System.Net.WebSockets.ClientWebSocketOptions.Proxy"/>
/// explicitly (null unless configured), which bypasses HTTP_PROXY/HTTPS_PROXY/ALL_PROXY.
/// Resolve the process-wide default proxy once so both legs keep the same egress route.
/// </summary>
public static class DiscordEgressProxy
{
    private static readonly Uri Probe = new("https://discord.com");

    /// <summary>
    /// Returns the proxy Discord.Net should use, or null when traffic must stay direct
    /// (no proxy configured, or the probe host is bypassed).
    /// </summary>
    public static IWebProxy? Resolve(IWebProxy? defaultProxy, Uri? probe = null)
    {
        if (defaultProxy is null)
            return null;

        try
        {
            return defaultProxy.IsBypassed(probe ?? Probe) ? null : defaultProxy;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves the proxy from the process environment via <see cref="HttpClient.DefaultProxy"/>
    /// (HTTP_PROXY / HTTPS_PROXY / ALL_PROXY / NO_PROXY).
    /// </summary>
    public static IWebProxy? FromEnvironment(Func<IWebProxy?>? source = null)
        => Resolve((source ?? (() => HttpClient.DefaultProxy))());
}
