using Xunit;

namespace Natter.Plugin.WebConsole.Tests;

public sealed class WebConsoleOptionsTests
{
    private static WebConsoleOptions Valid() => new() { Port = 5090 };

    [Fact]
    public void Defaults_are_valid()
    {
        var issues = WebConsoleOptionsValidator.Validate(Valid());
        Assert.Empty(issues);
    }

    [Fact]
    public void Non_loopback_bind_is_rejected()
    {
        var issues = WebConsoleOptionsValidator.Validate(new WebConsoleOptions { Bind = "0.0.0.0", Port = 5090 });

        Assert.Contains(issues, issue => issue.Code == "webconsole.bind-not-loopback");
    }

    [Fact]
    public void Plaintext_token_is_rejected()
    {
        var issues = WebConsoleOptionsValidator.Validate(new WebConsoleOptions { Token = "plain-token-value", Port = 5090 });

        Assert.Contains(issues, issue => issue.Code == "webconsole.token-reference-required");
    }

    [Fact]
    public void Non_loopback_health_url_is_rejected()
    {
        var issues = WebConsoleOptionsValidator.Validate(new WebConsoleOptions
        {
            HostHealthUrl = "http://10.0.0.5:5099/health",
            Port = 5090,
        });

        Assert.Contains(issues, issue => issue.Code == "webconsole.health-url-invalid");
    }

    [Fact]
    public void Health_url_must_point_at_health_path()
    {
        var issues = WebConsoleOptionsValidator.Validate(new WebConsoleOptions
        {
            HostHealthUrl = "http://127.0.0.1:5099/metrics",
            Port = 5090,
        });

        Assert.Contains(issues, issue => issue.Code == "webconsole.health-url-path");
    }

    [Theory]
    [InlineData("http://example.com/admin")]
    [InlineData("http://127.0.0.1:5090/admin")]
    [InlineData("http://127.0.0.1:5090/?a=1")]
    public void Invalid_public_base_url_is_rejected(string value)
    {
        var issues = WebConsoleOptionsValidator.Validate(new WebConsoleOptions
        {
            PublicBaseUrl = value,
            Port = 5090,
        });

        Assert.Contains(issues, issue => issue.Code == "webconsole.public-base-url-invalid");
    }

    [Fact]
    public void Read_only_and_write_enabled_conflict()
    {
        var issues = WebConsoleOptionsValidator.Validate(new WebConsoleOptions
        {
            ReadOnly = true,
            AllowConfigWrite = true,
            Port = 5090,
        });

        Assert.Contains(issues, issue => issue.Code == "webconsole.write-conflict");
    }

    [Fact]
    public void Port_range_is_validated()
    {
        var issues = WebConsoleOptionsValidator.Validate(new WebConsoleOptions { Port = 70000 });
        Assert.Contains(issues, issue => issue.Code == "webconsole.port-invalid");
    }

    [Fact]
    public void Invalid_trusted_proxy_is_rejected()
    {
        var issues = WebConsoleOptionsValidator.Validate(new WebConsoleOptions
        {
            TrustedProxies = ["not-an-ip"],
            Port = 5090,
        });

        Assert.Contains(issues, issue => issue.Code == "webconsole.trusted-proxy-invalid");
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.8.9.10", true)]
    [InlineData("::1", true)]
    [InlineData("localhost", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("example.com", false)]
    public void Loopback_detection_is_conservative(string host, bool expected)
    {
        Assert.Equal(expected, WebConsoleNetwork.IsLoopbackHost(host));
    }

    [Fact]
    public void Cidr_matching_handles_v4_and_invalid_input()
    {
        Assert.True(WebConsoleNetwork.IsInAnyCidr("127.0.0.1", ["127.0.0.0/8"]));
        Assert.True(WebConsoleNetwork.IsInAnyCidr("10.1.2.3", ["10.1.0.0/16"]));
        Assert.False(WebConsoleNetwork.IsInAnyCidr("10.2.0.1", ["10.1.0.0/16"]));
        Assert.False(WebConsoleNetwork.IsInAnyCidr("127.0.0.1", ["nonsense"]));

        Assert.True(WebConsoleNetwork.TryParseCidr("::1/128", out _, out var prefix));
        Assert.Equal(128, prefix);
    }

    [Fact]
    public void Loopback_url_parser_accepts_default_and_rejects_userinfo()
    {
        Assert.True(WebConsoleOptionsValidator.TryParseLoopbackUrl("http://127.0.0.1:5099/health", out var uri));
        Assert.NotNull(uri);

        Assert.False(WebConsoleOptionsValidator.TryParseLoopbackUrl("http://user:pass@127.0.0.1:5099/health", out _));
        Assert.False(WebConsoleOptionsValidator.TryParseLoopbackUrl("ftp://127.0.0.1/health", out _));
    }
}
