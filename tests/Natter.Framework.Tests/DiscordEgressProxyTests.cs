using System.Net;
using Natter.Chat.Discord;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// Gateway proxy resolution: Discord.Net sets ClientWebSocketOptions.Proxy explicitly, so the
/// process-wide proxy has to be handed over for the gateway to keep using HTTP_PROXY et al.
/// </summary>
public sealed class DiscordEgressProxyTests
{
    [Fact]
    public void No_default_proxy_keeps_the_gateway_direct()
        => Assert.Null(DiscordEgressProxy.Resolve(null));

    [Fact]
    public void Bypassed_proxy_is_not_handed_to_the_gateway()
        => Assert.Null(DiscordEgressProxy.Resolve(new FakeProxy(isBypassed: true)));

    [Fact]
    public void Configured_proxy_is_handed_to_the_gateway()
    {
        var proxy = new FakeProxy(isBypassed: false);
        Assert.Same(proxy, DiscordEgressProxy.Resolve(proxy));
    }

    [Fact]
    public void Environment_source_is_used_when_no_explicit_proxy_is_given()
    {
        var proxy = new FakeProxy(isBypassed: false);
        Assert.Same(proxy, DiscordEgressProxy.FromEnvironment(() => proxy));
        Assert.Null(DiscordEgressProxy.FromEnvironment(() => null));
    }

    [Fact]
    public void Proxy_carving_out_the_probe_host_is_skipped()
    {
        var proxy = new FakeProxy(isBypassed: false) { BypassHost = "discord.com" };
        Assert.Null(DiscordEgressProxy.Resolve(proxy, new Uri("https://discord.com")));
        Assert.Same(proxy, DiscordEgressProxy.Resolve(proxy, new Uri("https://example.test")));
    }

    private sealed class FakeProxy(bool isBypassed) : IWebProxy
    {
        public string? BypassHost { get; init; }

        public ICredentials? Credentials { get; set; }

        public Uri? GetProxy(Uri destination) => new("http://127.0.0.1:7890");

        public bool IsBypassed(Uri host)
            => isBypassed || (BypassHost is not null && host.Host.Equals(BypassHost, StringComparison.OrdinalIgnoreCase));
    }
}
