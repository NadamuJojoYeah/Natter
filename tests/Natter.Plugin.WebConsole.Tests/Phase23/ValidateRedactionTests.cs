using System.Text;
using System.Text.Json;
using Xunit;

namespace Natter.Plugin.WebConsole.Tests.Phase23;

/// <summary>
/// S1-04 regression: <c>POST /api/config/{id}/validate</c> used to hand back the host diff verbatim,
/// whose <c>"-"</c> lines are the stored document. Combined with "/validate is Read", a viewer
/// credential could read the config text (plaintext tokens, PEM keys). The rules pinned here:
/// viewer gets verdict + issues only, every diff line is redacted, and read-only instances keep
/// validate available (documented policy).
/// </summary>
public sealed class ValidateRedactionTests
{
    private const string SecretSentinel = "sk-plaintextsecret1234";
    private const string BearerSentinel = "Bearer abcdefghijklmnop";
    private const string PemBodySentinel = "MIIBOgIBAAJBAKj34GkxFhD90vcNLYLInFEX6Ppy1tPf9Cnzj4p4WGeKLs1Pt8QuKUpRKfFLfRYC9AIKjbJTWit+CqvjWYzvQwECAwEAAQJAIJLixBy2qpFoS4DSmoEm";
    private const string PlainTokenValue = "hunter2-plaintext-token";

    private static string SecretDocument() => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        token = SecretSentinel,
        authorization = BearerSentinel,
        password = PlainTokenValue,
        privateKey = "-----BEGIN RSA PRIVATE KEY-----\\n" + PemBodySentinel + "\\n-----END RSA PRIVATE KEY-----",
        reference = WebConsoleDefaults.TokenReference,
    });

    private static WebConsoleOptions Options(bool readOnly = false) => new()
    {
        Port = 5090,
        ReadOnly = readOnly,
        AllowConfigWrite = !readOnly,
    };

    /// <summary>Options that enable viewer login and (optionally) a read-only console.</summary>
    private static WebConsoleOptions Viewable(bool readOnly) => new()
    {
        Port = 5090,
        ReadOnly = readOnly,
        AllowConfigWrite = !readOnly,
        ViewerToken = WebConsoleDefaults.ViewerTokenReference,
    };

    private static string Body(string content) => JsonSerializer.Serialize(new { content });

    private static string Text(WebConsoleHttpResponse response) => Encoding.UTF8.GetString(response.Body);

    private static JsonElement Json(WebConsoleHttpResponse response) => LoginResult.Json(response);

    private static void AssertNoSecretMaterial(string body)
    {
        Assert.DoesNotContain(SecretSentinel, body, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnop", body, StringComparison.Ordinal);
        Assert.DoesNotContain(PemBodySentinel, body, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN RSA PRIVATE KEY", body, StringComparison.Ordinal);
        Assert.DoesNotContain(PlainTokenValue, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Viewer_validate_returns_no_diff_and_no_secret_material()
    {
        var store = new FakeAdminConfigStore().WithDocument("profiles", SecretDocument());
        using var harness = WebConsoleHarness.Create(Viewable(readOnly: false), adminConfig: store);
        var viewer = await harness.LoginViewerAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/config/profiles/validate",
            body: Body("{\"schemaVersion\":2}"),
            cookie: viewer.Cookie,
            csrf: viewer.Csrf));

        Assert.Equal(200, response.StatusCode);
        var body = Text(response);
        AssertNoSecretMaterial(body);

        var validation = Json(response).GetProperty("validation");
        Assert.False(validation.TryGetProperty("diff", out _));
        Assert.Equal("role:viewer", validation.GetProperty("diffOmitted").GetString());
        Assert.True(validation.TryGetProperty("valid", out _));
        Assert.True(validation.TryGetProperty("issues", out _));
    }

    [Fact]
    public async Task Viewer_read_of_the_document_is_redacted_too()
    {
        var store = new FakeAdminConfigStore().WithDocument("profiles", SecretDocument());
        using var harness = WebConsoleHarness.Create(Viewable(readOnly: false), adminConfig: store);
        var viewer = await harness.LoginViewerAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/config/profiles", cookie: viewer.Cookie));

        Assert.Equal(200, response.StatusCode);
        AssertNoSecretMaterial(Text(response));
        Assert.True(Json(response).GetProperty("contentRedacted").GetBoolean());
    }

    [Fact]
    public async Task Admin_validate_still_returns_a_redacted_diff()
    {
        var store = new FakeAdminConfigStore().WithDocument("profiles", SecretDocument());
        using var harness = WebConsoleHarness.Create(Viewable(readOnly: false), adminConfig: store);
        var admin = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/config/profiles/validate",
            body: Body("{\"schemaVersion\":2}"),
            cookie: admin.Cookie,
            csrf: admin.Csrf));

        Assert.Equal(200, response.StatusCode);
        AssertNoSecretMaterial(Text(response));

        var validation = Json(response).GetProperty("validation");
        Assert.False(validation.TryGetProperty("diffOmitted", out _));
        var diff = validation.GetProperty("diff");
        Assert.True(diff.GetProperty("removed").GetInt32() >= 1);
        var lines = diff.GetProperty("lines").EnumerateArray().Select(line => line.GetString() ?? string.Empty).ToArray();
        Assert.Contains(lines, line => line.StartsWith("-", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("[redacted]", StringComparison.Ordinal));
        // The `${secret:NAME}` reference stays readable so operators can see what the document expects.
        Assert.Contains(lines, line => line.Contains(WebConsoleDefaults.TokenReference, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Read_only_instance_keeps_validate_available_without_a_diff_for_viewers()
    {
        var store = new FakeAdminConfigStore().WithDocument("profiles", SecretDocument());
        using var harness = WebConsoleHarness.Create(Viewable(readOnly: true), adminConfig: store);
        var viewer = await harness.LoginViewerAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/config/profiles/validate",
            body: Body("{}"),
            cookie: viewer.Cookie,
            csrf: viewer.Csrf));

        Assert.Equal(200, response.StatusCode);
        AssertNoSecretMaterial(Text(response));
        Assert.False(Json(response).GetProperty("validation").TryGetProperty("diff", out _));
    }

    [Fact]
    public async Task Admin_validate_in_read_only_mode_still_masks_the_diff()
    {
        var store = new FakeAdminConfigStore().WithDocument("profiles", SecretDocument());
        using var harness = WebConsoleHarness.Create(Viewable(readOnly: true), adminConfig: store);
        var admin = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/config/profiles/validate", body: Body("{}"), cookie: admin.Cookie, csrf: admin.Csrf));

        Assert.Equal(200, response.StatusCode);
        AssertNoSecretMaterial(Text(response));
        Assert.True(Json(response).GetProperty("validation").TryGetProperty("diff", out _));
    }

    [Fact]
    public async Task Save_response_diff_is_redacted_as_well()
    {
        var store = new FakeAdminConfigStore().WithDocument("profiles", SecretDocument());
        using var harness = WebConsoleHarness.Create(Viewable(readOnly: false), adminConfig: store);
        var admin = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/config/profiles",
            body: JsonSerializer.Serialize(new
            {
                content = "{\"schemaVersion\":1,\"token\":\"${secret:WEBCONSOLE_TOKEN}\"}",
                etag = Natter.Application.Admin.AdminETag.Compute(SecretDocument()),
            }),
            cookie: admin.Cookie,
            csrf: admin.Csrf));

        Assert.Equal(200, response.StatusCode);
        AssertNoSecretMaterial(Text(response));
    }

    [Fact]
    public async Task Gateway_masks_every_diff_line_but_keeps_the_counters()
    {
        var store = new FakeAdminConfigStore().WithDocument("profiles", SecretDocument());
        using var harness = WebConsoleHarness.Create(Viewable(readOnly: false), adminConfig: store);

        var result = await harness.Admin.ValidateAsync("profiles", "{\"schemaVersion\":2}");

        Assert.True(result.Ok);
        var diff = result.Value!.Diff;
        Assert.NotNull(diff);
        Assert.True(diff!.Removed >= 1);
        // The stored document is compact JSON (one line), so it is replaced by one added line.
        Assert.Equal(1, diff.Added);
        Assert.True(diff.Changed >= 0);
        Assert.All(diff.Lines, line => Assert.DoesNotContain(SecretSentinel, line, StringComparison.Ordinal));
        Assert.All(diff.Lines, line => Assert.DoesNotContain(PemBodySentinel, line, StringComparison.Ordinal));
        Assert.Equal("config.schema_invalid", result.Value.Issues.FirstOrDefault()?.Code ?? "config.schema_invalid");
    }

    [Fact]
    public void Redaction_masks_pem_blocks_truncated_pem_and_assignment_pairs()
    {
        var complete = "key: |\n-----BEGIN EC PRIVATE KEY-----\nMHcCAQEEIB\n-----END EC PRIVATE KEY-----\nnext: 1";
        var truncated = "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA";
        var inline = "{\"password\": \"hunter2\", \"note\": \"keep\"}";
        var reference = "{\"token\": \"${secret:WEBCONSOLE_TOKEN}\"}";

        var redactedComplete = WebConsoleRedaction.RedactSecrets(complete);
        Assert.DoesNotContain("MHcCAQEEIB", redactedComplete, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN EC PRIVATE KEY", redactedComplete, StringComparison.Ordinal);
        Assert.Contains("[redacted]", redactedComplete, StringComparison.Ordinal);

        var redactedTruncated = WebConsoleRedaction.RedactSecrets(truncated);
        Assert.DoesNotContain("b3BlbnNzaC1rZXktdjEAAAAA", redactedTruncated, StringComparison.Ordinal);

        var redactedInline = WebConsoleRedaction.RedactSecrets(inline);
        Assert.DoesNotContain("hunter2", redactedInline, StringComparison.Ordinal);
        Assert.Contains("keep", redactedInline, StringComparison.Ordinal);

        Assert.Equal(reference, WebConsoleRedaction.RedactSecrets(reference));
    }

    [Fact]
    public void Secret_references_survive_redaction_inside_longer_lines()
    {
        var line = "-{\"token\":\"sk-abcdefghijkl\",\"reference\":\"${secret:WEBCONSOLE_TOKEN}\",\"apiKey\":\"${secret:OTHER_KEY}\"}";

        var redacted = WebConsoleRedaction.RedactSecrets(line);

        Assert.Contains("${secret:WEBCONSOLE_TOKEN}", redacted, StringComparison.Ordinal);
        Assert.Contains("${secret:OTHER_KEY}", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-abcdefghijkl", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("${secret:[redacted]}", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeText_keeps_references_while_masking_values()
    {
        var text = "config saved for ${secret:WEBCONSOLE_TOKEN} with token=sk-abcdefghijkl";

        var sanitized = WebConsoleRedaction.SanitizeText(text);

        Assert.Contains("${secret:WEBCONSOLE_TOKEN}", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-abcdefghijkl", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Redaction_is_stable_for_already_masked_text()
    {
        var once = WebConsoleRedaction.RedactSecrets(
            "token=sk-abcdefghijkl\n-----BEGIN RSA PRIVATE KEY-----\nAAAA\n-----END RSA PRIVATE KEY-----");

        Assert.Equal(once, WebConsoleRedaction.RedactSecrets(once));
    }
}
