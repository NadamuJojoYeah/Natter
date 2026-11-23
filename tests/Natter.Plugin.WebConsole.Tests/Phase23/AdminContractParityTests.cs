using System.Text;
using Natter.Application.Admin;
using Xunit;

namespace Natter.Plugin.WebConsole.Tests.Phase23;

/// <summary>
/// The console and the host admin surface must agree on the shared vocabulary; a drift here shows
/// up as "every save conflicts" or "a valid config is rejected". These tests pin the parity.
/// </summary>
public sealed class AdminContractParityTests
{
    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\n  \"schemaVersion\": 1\n}\n")]
    [InlineData("{\"a\":\"中文\",\"n\":1}")]
    public void ETag_algorithm_matches_the_plugin_policy(string content)
    {
        Assert.Equal(WebConsoleConfigEditPolicy.ComputeETag(content), AdminETag.Compute(content));
        Assert.StartsWith("sha256:", AdminETag.Compute(content));
        Assert.True(AdminETag.Matches(content, WebConsoleConfigEditPolicy.ComputeETag(content)));
    }

    [Theory]
    [InlineData("profiles", true)]
    [InlineData("plugin:natter.tool.admin.webconsole", true)]
    [InlineData("plugin:bad id", false)]
    [InlineData("../../etc/passwd", false)]
    [InlineData("plugin:../escape", false)]
    [InlineData("secrets", false)]
    public void Document_id_whitelist_agrees(string id, bool expected)
    {
        var pluginAccepts = WebConsoleConfigEditPolicy.IsAllowedId(id);
        var hostAccepts = AdminConfigDocumentPolicy.TryNormalize(id, out _, out _, out _);

        Assert.Equal(expected, pluginAccepts);
        Assert.Equal(expected, hostAccepts);
    }

    [Theory]
    [InlineData("{\"token\":\"plain-value\"}", true)]
    [InlineData("{\"token\":\"${secret:WEBCONSOLE_TOKEN}\"}", false)]
    [InlineData("{\"apiKey\":\"sk-abcdefghijkl\"}", true)]
    [InlineData("{\"authorization\":\"Bearer abcdefghijkl\"}", true)]
    [InlineData("{\"name\":\"console\"}", false)]
    public void Secret_literal_detection_agrees(string content, bool expectsSecret)
    {
        var pluginSays = WebConsoleConfigEditPolicy.ContainsSecretLiteral(content);
        var hostSays = AdminConfigContentValidator.ContainsSecretLiteral(content);

        Assert.Equal(expectsSecret, pluginSays);
        Assert.Equal(expectsSecret, hostSays);
    }

    [Theory]
    [InlineData("health", "health")]
    [InlineData("plugins   list", "plugins list")]
    [InlineData("  version  ", "version")]
    public void Command_normalization_agrees(string raw, string expected)
    {
        Assert.Equal(expected, WebConsoleCommandPolicy.NormalizeCommand(raw));
        Assert.Equal(expected, AdminCommandPolicy.NormalizeCommand(raw));
    }

    [Fact]
    public void Output_budget_and_redaction_agree()
    {
        var output = WebConsoleCommandPolicy.NormalizeOutput("token sk-abcdefghijkl", "ok");
        Assert.Contains("[redacted]", output.Stdout, StringComparison.Ordinal);
        Assert.Equal(WebConsoleCommandPolicy.MaxOutputBytes, AdminCommandPolicy.MaxOutputBytes);
        Assert.Equal(WebConsoleConfigEditPolicy.MaxContentBytes, AdminConfigDocumentPolicy.MaxDocumentBytes);
    }

    [Fact]
    public void Reload_hints_describe_the_documented_restart_requirement()
    {
        // The host rewrites profiles via the config directory but does not hot-reload them yet, so
        // the console must tell the operator a restart is required rather than claiming a reload.
        Assert.Equal(AdminConfigReloadHint.RestartHost, AdminConfigDocumentPolicy.ReloadHintFor("profiles"));
        Assert.Equal(
            AdminConfigReloadHint.RestartPlugin,
            AdminConfigDocumentPolicy.ReloadHintFor("plugin:natter.tool.admin.webconsole"));
    }

    [Fact]
    public void Content_size_limit_is_256_kib_in_both_layers()
    {
        var payload = new string('x', WebConsoleConfigEditPolicy.MaxContentBytes + 1);
        var pluginValidation = WebConsoleConfigEditPolicy.Validate("profiles", "\"" + payload + "\"");
        var hostIssues = AdminConfigContentValidator.Validate("\"" + payload + "\"");

        Assert.Contains(pluginValidation.Issues, issue => issue.Code == AdminCodes.ConfigTooLarge);
        Assert.Contains(hostIssues, issue => issue.Code == AdminCodes.ConfigTooLarge);
        Assert.True(Encoding.UTF8.GetByteCount(payload) > WebConsoleConfigEditPolicy.MaxContentBytes);
    }
}
