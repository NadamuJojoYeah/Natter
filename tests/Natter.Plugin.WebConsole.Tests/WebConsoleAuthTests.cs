using Xunit;

namespace Natter.Plugin.WebConsole.Tests;

public sealed class WebConsoleAuthTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Token_verifier_accepts_exact_token_only()
    {
        var verifier = new WebConsoleTokenAuthenticator(WebConsoleHarness.Token);

        Assert.True(verifier.IsUsable);
        Assert.True(verifier.Verify(WebConsoleHarness.Token));
        Assert.False(verifier.Verify(WebConsoleHarness.Token + "x"));
        Assert.False(verifier.Verify(""));
        Assert.False(verifier.Verify(null));
    }

    [Fact]
    public void Token_verifier_reports_missing_and_short_tokens()
    {
        Assert.Equal("webconsole.token-missing", new WebConsoleTokenAuthenticator(null).Problem);
        Assert.Equal("webconsole.token-too-short", new WebConsoleTokenAuthenticator("short").Problem);
        Assert.False(new WebConsoleTokenAuthenticator("short").IsUsable);
    }

    [Fact]
    public void Session_expires_and_can_be_revoked()
    {
        var clock = new FakeTimeProvider(Now);
        var store = new WebConsoleSessionStore(clock);

        var session = store.Create(WebConsoleRole.Admin, TimeSpan.FromMinutes(10));
        Assert.NotNull(store.Find(session.Id));

        store.Revoke(session.Id);
        Assert.Null(store.Find(session.Id));

        var second = store.Create(WebConsoleRole.Admin, TimeSpan.FromMinutes(10));
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Null(store.Find(second.Id));
    }

    [Fact]
    public void Login_throttle_blocks_after_five_failures_and_resets()
    {
        var clock = new FakeTimeProvider(Now);
        var throttle = new WebConsoleLoginThrottle(clock);
        const string client = "127.0.0.1";

        Assert.False(throttle.IsBlocked(client, out _));

        for (var index = 0; index < WebConsoleLoginThrottle.MaxFailuresPerClient; index++)
            throttle.RecordFailure(client);

        Assert.True(throttle.IsBlocked(client, out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);

        throttle.Reset(client);
        Assert.False(throttle.IsBlocked(client, out _));
    }

    [Fact]
    public void Login_throttle_expires_after_window()
    {
        var clock = new FakeTimeProvider(Now);
        var throttle = new WebConsoleLoginThrottle(clock);

        for (var index = 0; index < WebConsoleLoginThrottle.MaxFailuresPerClient; index++)
            throttle.RecordFailure("127.0.0.1");

        Assert.True(throttle.IsBlocked("127.0.0.1", out _));

        clock.Advance(WebConsoleLoginThrottle.Window + TimeSpan.FromSeconds(1));
        Assert.False(throttle.IsBlocked("127.0.0.1", out _));
    }

    [Fact]
    public void Origin_policy_allows_loopback_and_rejects_cross_origin()
    {
        var policy = new WebConsoleOriginPolicy(null, 5090);

        Assert.True(policy.IsAllowed(null, null));
        Assert.True(policy.IsAllowed("http://127.0.0.1:5090", null));
        Assert.True(policy.IsAllowed("http://localhost:5090", "same-origin"));
        Assert.False(policy.IsAllowed("https://evil.example", null));
        Assert.False(policy.IsAllowed("http://127.0.0.1:5090", "cross-site"));
        Assert.False(policy.IsAllowed("not a url", null));
        Assert.False(policy.RequiresSecureCookie);
    }

    [Fact]
    public void Origin_policy_uses_public_base_url_and_secure_cookie()
    {
        var policy = new WebConsoleOriginPolicy("https://admin.example.com", 5090);

        Assert.True(policy.RequiresSecureCookie);
        Assert.True(policy.IsAllowed("https://admin.example.com", null));
        Assert.True(policy.IsAllowed("http://127.0.0.1:5090", null));
        Assert.False(policy.IsAllowed("http://admin.example.com", null));
    }

    [Fact]
    public void Cookie_helpers_emit_strict_httponly_cookie()
    {
        var cookie = WebConsoleCookies.BuildSessionCookie("abc", 600, secure: true);

        Assert.Contains("HttpOnly", cookie);
        Assert.Contains("SameSite=Strict", cookie);
        Assert.Contains("Secure", cookie);
        Assert.Contains("Max-Age=600", cookie);

        Assert.Equal("v", WebConsoleCookies.ReadCookie("a=1; natter_webconsole=v", WebConsoleDefaults.SessionCookieName));
        Assert.Null(WebConsoleCookies.ReadCookie(null, WebConsoleDefaults.SessionCookieName));
    }
}
