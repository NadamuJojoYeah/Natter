using Natter.Application.Redaction;
using Xunit;

namespace Natter.Plugin.WebConsole.Tests;

/// <summary>
/// C-03: the console's redaction must be the shared core, not a fourth private copy. These cases
/// pin the delegation and the console-specific choices (marker, preserved `${secret:NAME}`).
/// </summary>
public sealed class RedactionDelegationTests
{
    [Theory]
    [InlineData("token=abc123secret")]
    [InlineData("Authorization: Bot abcdefghijklmnop")]
    [InlineData("https://user:passw0rd@host/x")]
    [InlineData("sk-live-ABCDEFGHIJKLMNOP")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIEow\n-----END RSA PRIVATE KEY-----")]
    public void Console_redaction_matches_the_shared_core(string input)
    {
        Assert.Equal(
            SecretRedaction.Apply(input, SecretRedactionOptions.Admin),
            WebConsoleRedaction.RedactSecrets(input));
    }

    [Fact]
    public void Console_keeps_its_marker_and_preserves_secret_references()
    {
        Assert.Contains("[redacted]", WebConsoleRedaction.RedactSecrets("token=abc123secret"), StringComparison.Ordinal);
        Assert.Contains(
            "${secret:WEBCONSOLE_TOKEN}",
            WebConsoleRedaction.RedactSecrets("token=${secret:WEBCONSOLE_TOKEN}"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Console_pass_is_capped_like_the_shared_core()
    {
        var huge = new string('a', 4 * 1024 * 1024) + " token=tail-secret";
        var redacted = WebConsoleRedaction.RedactSecrets(huge);

        Assert.True(redacted.Length <= SecretRedaction.DefaultMaxScanLength + 32, $"not capped: {redacted.Length}");
        Assert.Contains("…(truncated)", redacted, StringComparison.Ordinal);
    }
}
