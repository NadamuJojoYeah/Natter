using System.Text;
using System.Text.RegularExpressions;
using Natter.Application.Admin;
using Natter.Application.Redaction;
using Natter.Chat.Discord;
using Xunit;
using Xunit.Abstractions;

namespace Natter.Framework.Tests;

/// <summary>
/// C-03 shared fixture: one input matrix, every redaction implementation.
///
/// The point is not "the new code passes" but "no implementation can silently miss a pattern":
/// the same sensitive inputs are fed to each implementation and to a faithful copy of the pre-
/// consolidation Discord rules (the leak baseline). The baseline is asserted to leak, which proves
/// the fixture is sharp enough to catch a missing rule instead of just agreeing with itself.
/// </summary>
public sealed class SecretRedactionConsistencyTests(ITestOutputHelper output)
{
    /// <summary>One input plus the substrings that must never survive redaction.</summary>
    private sealed record Case(string Name, string Input, params string[] MustNotAppear)
    {
        public override string ToString() => Name;
    }

    private static readonly Case[] Matrix =
    [
        new("key=value token",
            "DISCORD_TOKEN=MTIzNDU2Nzg5MDEyMzQ1Njc4.abcdefGHI.JKLmnop",
            "MTIzNDU2Nzg5MDEyMzQ1Njc4"),
        new("quoted token",
            "token = \"super-secret-value-42\"",
            "super-secret-value-42"),
        new("authorization bot header",
            "Authorization: Bot MTIzNDU2Nzg5.abcdef.ghijkl",
            "MTIzNDU2Nzg5.abcdef.ghijkl"),
        new("url credentials",
            "GET https://bot-user:p@ssw0rd-value@discord.com/api/v10/users failed",
            "p@ssw0rd-value"),
        new("full pem block",
            "key:\n-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA\n-----END RSA PRIVATE KEY-----\nend",
            "MIIEowIBAAKCAQEA"),
        new("dangling pem block",
            "-----BEGIN OPENSSH PRIVATE KEY-----\nAAAAB3NzaC1yc2E",
            "AAAAB3NzaC1yc2E"),
        new("literal sk key",
            "provider rejected sk-live-ABCDEFGHIJKLMNOP",
            "sk-live-ABCDEFGHIJKLMNOP"),
        new("client secret",
            "client_secret=zzz-client-secret-zzz",
            "zzz-client-secret-zzz"),
        new("cookie",
            "cookie: session=abc123cookievalue",
            "abc123cookievalue"),
        new("connection string",
            "ConnectionString=Server=db;Password=connpass123",
            "connpass123"),
        new("unicode + tab",
            "配置\ttoken\t=\t中文密钥值12345",
            "中文密钥值12345"),
        new("newline separated",
            "line1\napi_key: key-value-with-newline\nline3",
            "key-value-with-newline"),
    ];

    public static IEnumerable<object[]> MatrixNames()
        => Matrix.Select(item => new object[] { item.Name });

    [Theory]
    [MemberData(nameof(MatrixNames))]
    public void Every_implementation_removes_the_secret(string caseName)
    {
        var item = Matrix.Single(entry => entry.Name == caseName);

        AssertClean("AdminRedactor", AdminRedactor.Default.Redact(item.Input), item);
        AssertClean("DiscordLogRedaction", DiscordLogRedaction.Redact(item.Input), item);
        AssertClean("SecretRedaction.Admin", SecretRedaction.Apply(item.Input, SecretRedactionOptions.Admin), item);
        AssertClean("SecretRedaction.Discord", SecretRedaction.Apply(item.Input, SecretRedactionOptions.Discord), item);
        AssertClean("RuntimeHealth", Natter.Cli.Core.Commands.RuntimeHealth.Sanitize(item.Input), item);
    }

    [Fact]
    public void Output_markers_stay_implementation_specific()
    {
        const string input = "token=abc123secret";

        Assert.Contains("[redacted]", AdminRedactor.Default.Redact(input), StringComparison.Ordinal);
        Assert.Contains("***", DiscordLogRedaction.Redact(input), StringComparison.Ordinal);
        Assert.DoesNotContain("[redacted]", DiscordLogRedaction.Redact(input), StringComparison.Ordinal);

        // Discord keeps its three specialised output shapes.
        Assert.Contains("https://***@", DiscordLogRedaction.Redact("https://user:pass@host/x"), StringComparison.Ordinal);
        Assert.Contains("Bot ***", DiscordLogRedaction.Redact("Authorization: Bot abcdefghijklmnop"), StringComparison.Ordinal);
    }

    [Fact]
    public void Secret_references_survive_every_implementation()
    {
        const string input = "token=${secret:WEBCONSOLE_TOKEN}";

        foreach (var redacted in new[]
                 {
                     AdminRedactor.Default.Redact(input),
                     DiscordLogRedaction.Redact(input),
                     SecretRedaction.Apply(input, SecretRedactionOptions.Discord),
                 })
        {
            Assert.Contains("${secret:WEBCONSOLE_TOKEN}", redacted, StringComparison.Ordinal);
            Assert.DoesNotContain("${secret:[redacted]}", redacted, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Oversized_input_is_capped_instead_of_scanned_whole()
    {
        // 4 MiB line with the secret at the very end: the scan cap means the tail is never reached,
        // and the result carries the truncation marker rather than growing to megabytes.
        var huge = new string('a', 4 * 1024 * 1024) + " token=tail-secret-value";
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var admin = AdminRedactor.Default.Redact(huge);
        var discord = DiscordLogRedaction.Redact(huge);
        var console = SecretRedaction.Apply(huge, SecretRedactionOptions.Admin);

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"redaction of 4 MiB took {stopwatch.Elapsed}");
        foreach (var result in new[] { admin, discord, console })
        {
            Assert.True(result.Length <= SecretRedaction.DefaultMaxScanLength + 32, $"not capped: {result.Length}");
            Assert.Contains("…(truncated)", result, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Hostile_input_does_not_hang_and_keeps_a_bounded_time()
    {
        // Pathological backtracking bait for the assignment rule.
        var hostile = string.Concat(Enumerable.Repeat("token:", 20_000)) + new string('x', 20_000);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = DiscordLogRedaction.Redact(hostile);

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"hostile input took {stopwatch.Elapsed}");
        Assert.NotNull(result);
    }

    /// <summary>
    /// The pre-consolidation Discord rules (copied verbatim from HEAD `DiscordLogRedaction`). They
    /// have no PEM rule, no literal `sk-` rule, no scan cap and no regex timeout. Asserting that the
    /// baseline leaks proves the matrix is sharp; the list is printed so the report can quote it.
    /// </summary>
    [Fact]
    public void The_pre_consolidation_baseline_leaks_and_the_matrix_catches_it()
    {
        var leaked = new List<string>();
        foreach (var item in Matrix)
        {
            var baseline = LegacyDiscordRedact(item.Input);
            foreach (var secret in item.MustNotAppear)
            {
                if (baseline.Contains(secret, StringComparison.Ordinal))
                    leaked.Add($"{item.Name} -> '{secret}'");
            }
        }

        output.WriteLine("legacy baseline leaked: " + string.Join(" | ", leaked.Distinct(StringComparer.Ordinal)));
        Assert.NotEmpty(leaked);
        Assert.Contains(leaked, entry => entry.StartsWith("full pem block", StringComparison.Ordinal));
        Assert.Contains(leaked, entry => entry.StartsWith("dangling pem block", StringComparison.Ordinal));
        Assert.Contains(leaked, entry => entry.StartsWith("literal sk key", StringComparison.Ordinal));
    }

    private void AssertClean(string implementation, string redacted, Case item)
    {
        foreach (var secret in item.MustNotAppear)
        {
            Assert.False(
                redacted.Contains(secret, StringComparison.Ordinal),
                $"{implementation} leaked '{secret}' for '{item.Name}': {redacted}");
        }
    }

    // ---- verbatim copy of the pre-consolidation implementation (HEAD 3b80da0) ----
    private static readonly Regex LegacyUrlCredentials = new(
        @"\b(https?://)([^/@\s]+)@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LegacyBotAuthorization = new(
        @"\bBot\s+[A-Za-z0-9._\-]{12,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LegacySecretAssignment = new(
        @"(?<key>(?:token|authorization|bearer|api[_-]?key|secret|password|passwd)\s*[=:]\s*)(?<value>[^\s,;""']+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string LegacyDiscordRedact(string? value, string? secret = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value ?? string.Empty;

        var result = LegacyUrlCredentials.Replace(value, "$1***@");
        result = LegacyBotAuthorization.Replace(result, "Bot ***");
        result = LegacySecretAssignment.Replace(result, "${key}***");
        if (!string.IsNullOrEmpty(secret))
            result = result.Replace(secret, "***", StringComparison.Ordinal);
        return result;
    }
}
