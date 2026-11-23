using Xunit;

namespace Natter.Plugin.ConversationConsole.Tests;

/// <summary>
/// P1 acceptance (docs/CONVERSATION_CONSOLE.md §7): independent token, 401 on bad credentials, and a
/// read-only surface that cannot be reached with a mutating verb.
/// </summary>
public sealed class ConversationConsoleAuthAndReadOnlyTests
{
    // ------------------------------------------------------------------ authentication

    [Fact]
    public async Task Missing_token_is_rejected_with_401()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(
            workspace,
            query: new FakeConversationQuery().WithSession("s-1", "discord:discord:1"));

        var response = await handler.HandleAsync(
            ConversationConsoleTestFactory.Request("/api/sessions", token: null));

        Assert.Equal(401, response.StatusCode);
        Assert.Contains("auth.required", System.Text.Encoding.UTF8.GetString(response.Body), StringComparison.Ordinal);
        Assert.Equal("Bearer", response.Headers["WWW-Authenticate"]);
    }

    [Fact]
    public async Task Wrong_token_is_rejected_with_401()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(
            workspace,
            query: new FakeConversationQuery().WithSession("s-1", "discord:discord:1"));

        var response = await handler.HandleAsync(
            ConversationConsoleTestFactory.Request("/api/sessions", token: "not-the-console-token-000000"));

        Assert.Equal(401, response.StatusCode);
        Assert.Contains("auth.invalid", System.Text.Encoding.UTF8.GetString(response.Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Valid_token_reaches_the_api()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(
            workspace,
            query: new FakeConversationQuery().WithSession("s-1", "discord:discord:1"));

        var response = await handler.HandleAsync(ConversationConsoleTestFactory.Request("/api/sessions"));

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public void Authenticator_rejects_short_or_missing_tokens()
    {
        Assert.False(new ConversationConsoleTokenAuthenticator(null).IsUsable);
        Assert.Equal("conversation-console.token-missing", new ConversationConsoleTokenAuthenticator("").Problem);

        var shortToken = new string('a', ConversationConsoleDefaults.MinTokenLength - 1);
        Assert.False(new ConversationConsoleTokenAuthenticator(shortToken).IsUsable);
        Assert.Equal("conversation-console.token-too-short", new ConversationConsoleTokenAuthenticator(shortToken).Problem);

        var usable = new ConversationConsoleTokenAuthenticator(new string('a', ConversationConsoleDefaults.MinTokenLength));
        Assert.True(usable.IsUsable);
        Assert.Null(usable.Problem);
    }

    [Fact]
    public void Authenticator_requires_the_bearer_scheme()
    {
        var token = new string('b', 32);
        var authenticator = new ConversationConsoleTokenAuthenticator(token);

        Assert.True(authenticator.IsAuthorized("Bearer " + token));
        Assert.True(authenticator.IsAuthorized("bearer " + token));
        Assert.False(authenticator.IsAuthorized(token));
        Assert.False(authenticator.IsAuthorized("Basic " + token));
        Assert.False(authenticator.IsAuthorized("Bearer "));
        Assert.False(authenticator.IsAuthorized("Bearer  " + token + "x"));
    }

    [Fact]
    public void Config_rejects_a_plaintext_token_and_a_wrong_reference()
    {
        var plaintext = ConversationConsoleOptionsValidator.Validate(
            new ConversationConsoleOptions { Token = "literal-token-value-000000" });

        Assert.Contains(plaintext, issue => issue.Code == "conversation-console.token-reference-required");

        // The default reference points at the console's own secret, never at the WebConsole one.
        Assert.Equal("${secret:CONVERSATION_CONSOLE_TOKEN}", ConversationConsoleDefaults.TokenReference);
        Assert.NotEqual(ConversationConsoleDefaults.TokenSecretName, "WEBCONSOLE_TOKEN");
        Assert.Empty(ConversationConsoleOptionsValidator.Validate(new ConversationConsoleOptions()));
    }

    // ------------------------------------------------------------------ read-only

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Mutating_verbs_are_rejected_with_405(string method)
    {
        using var workspace = new ConversationConsoleWorkspace();
        var query = new FakeConversationQuery().WithSession("s-1", "discord:discord:1").WithMessages("s-1", "hello");
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(workspace, query: query);

        foreach (var path in new[] { "/api/sessions", "/api/sessions/s-1/messages", "/api/health" })
        {
            var response = await handler.HandleAsync(
                ConversationConsoleTestFactory.Request(path, method: method));

            Assert.Equal(405, response.StatusCode);
            Assert.Equal("GET, HEAD", response.Headers["Allow"]);
            Assert.Contains(
                "method.not_allowed",
                System.Text.Encoding.UTF8.GetString(response.Body),
                StringComparison.Ordinal);
        }

        // Nothing reached the read-only port on a rejected verb.
        Assert.Empty(query.Filters);
        Assert.Empty(query.MessageCalls);
    }

    [Theory]
    [InlineData("/api/sessions/delete")]
    [InlineData("/api/sessions/s-1/delete")]
    [InlineData("/api/sessions/s-1")]
    [InlineData("/api/messages")]
    [InlineData("/api/export")]
    [InlineData("/api/sessions/s-1/export")]
    public async Task There_is_no_write_or_export_route(string path)
    {
        using var workspace = new ConversationConsoleWorkspace();
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(
            workspace,
            query: new FakeConversationQuery().WithSession("s-1", "discord:discord:1"));

        var get = await handler.HandleAsync(ConversationConsoleTestFactory.Request(path));
        var post = await handler.HandleAsync(ConversationConsoleTestFactory.Request(path, method: "POST"));

        Assert.True(
            get.StatusCode is 404 or 405,
            $"GET {path} must not be served, got {get.StatusCode}");
        Assert.True(
            post.StatusCode is 404 or 405,
            $"POST {path} must not be served, got {post.StatusCode}");
    }

    [Fact]
    public void Read_only_defaults_to_true_and_cannot_be_disabled_in_p1()
    {
        Assert.True(new ConversationConsoleOptions().ReadOnly);

        var issues = ConversationConsoleOptionsValidator.Validate(
            new ConversationConsoleOptions { ReadOnly = false });

        Assert.Contains(issues, issue => issue.Code == "conversation-console.read-only-required");
    }

    [Fact]
    public async Task Health_reports_a_zero_write_path_surface()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(
            workspace,
            query: new FakeConversationQuery());

        var response = await handler.HandleAsync(ConversationConsoleTestFactory.Request("/api/health"));
        using var document = System.Text.Json.JsonDocument.Parse(response.Body);

        var root = document.RootElement;
        Assert.True(root.GetProperty("readOnly").GetBoolean());
        Assert.Equal(0, root.GetProperty("writePaths").GetInt32());
        Assert.True(root.GetProperty("query").GetProperty("available").GetBoolean());
        Assert.Equal("host.conversation-query", root.GetProperty("query").GetProperty("source").GetString());
        Assert.Equal("state-directory", root.GetProperty("audit").GetProperty("root").GetString());
    }

    // ------------------------------------------------------------------ origin

    [Fact]
    public async Task Foreign_origin_is_rejected_before_authentication()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(
            workspace,
            query: new FakeConversationQuery());

        var response = await handler.HandleAsync(
            ConversationConsoleTestFactory.Request("/api/sessions", origin: "https://evil.example"));

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("request.origin_denied", System.Text.Encoding.UTF8.GetString(response.Body), StringComparison.Ordinal);
        Assert.Contains(
            workspace.ReadAudit(),
            entry => entry.Action == ConversationConsoleAuditActions.OriginDenied);
    }

    [Fact]
    public async Task Cross_site_fetch_metadata_is_rejected()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(
            workspace,
            query: new FakeConversationQuery());

        var response = await handler.HandleAsync(
            ConversationConsoleTestFactory.Request("/api/sessions", fetchSite: "cross-site"));

        Assert.Equal(403, response.StatusCode);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5091")]
    [InlineData("http://localhost:5091")]
    public async Task Same_origin_requests_are_allowed(string origin)
    {
        using var workspace = new ConversationConsoleWorkspace();
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(
            workspace,
            query: new FakeConversationQuery());

        var response = await handler.HandleAsync(
            ConversationConsoleTestFactory.Request("/api/health", origin: origin));

        Assert.Equal(200, response.StatusCode);
    }

    [Theory]
    // Bound to `localhost` must still accept a request whose Origin says 127.0.0.1 (and vice versa):
    // the address bar decides what the browser sends, and both names reach the same socket. Echoing
    // back only the configured Bind produced request.origin_denied on a correctly configured instance.
    [InlineData("localhost", "http://127.0.0.1:5091")]
    [InlineData("localhost", "http://localhost:5091")]
    [InlineData("localhost", "http://[::1]:5091")]
    [InlineData("127.0.0.1", "http://localhost:5091")]
    [InlineData("127.0.0.1", "http://127.0.0.1:5091")]
    [InlineData("::1", "http://127.0.0.1:5091")]
    public async Task Loopback_bind_accepts_every_loopback_origin_spelling(string bind, string origin)
    {
        using var workspace = new ConversationConsoleWorkspace();
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(
            workspace,
            options: new ConversationConsoleOptions { Bind = bind },
            query: new FakeConversationQuery());

        var response = await handler.HandleAsync(
            ConversationConsoleTestFactory.Request("/api/health", origin: origin));

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task Loopback_bind_still_rejects_a_non_loopback_origin()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(
            workspace,
            options: new ConversationConsoleOptions { Bind = "localhost" },
            query: new FakeConversationQuery());

        var response = await handler.HandleAsync(
            ConversationConsoleTestFactory.Request("/api/health", origin: "http://evil.example:5091"));

        Assert.Equal(403, response.StatusCode);
    }

}
