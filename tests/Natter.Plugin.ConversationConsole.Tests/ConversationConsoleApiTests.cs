using System.Text;
using System.Text.Json;
using Natter.Application.Chat;
using Xunit;

namespace Natter.Plugin.ConversationConsole.Tests;

/// <summary>
/// API contract of the read-only console: filters reach the host port clamped, message paging is
/// incremental, and a host without the port degrades to 501 instead of inventing data.
/// </summary>
public sealed class ConversationConsoleApiTests
{
    [Fact]
    public async Task List_passes_filters_through_and_returns_the_next_cursor()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var query = new FakeConversationQuery()
            .WithSession("s-1", "discord:discord:11")
            .WithSession("s-2", "dingtalk:dingtalk:22");
        query.NextCursor = "opaque-cursor";
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(workspace, query: query);

        var response = await handler.HandleAsync(ConversationConsoleTestFactory.Request(
            "/api/sessions",
            query: "source=discord%3Adiscord%3A11&actor=actor-a&q=needle&limit=2"));

        Assert.Equal(200, response.StatusCode);

        var filter = Assert.Single(query.Filters);
        Assert.Equal("discord:discord:11", filter.SourceKey);
        Assert.Equal("actor-a", filter.ActorId);
        Assert.Equal("needle", filter.Query);
        Assert.Equal(2, filter.Limit);
        Assert.Null(filter.Cursor);

        using var document = JsonDocument.Parse(response.Body);
        Assert.Equal(2, document.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal("opaque-cursor", document.RootElement.GetProperty("nextCursor").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task List_parses_time_bounds_and_the_cursor()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var query = new FakeConversationQuery().WithSession("s-1", "discord:discord:11");
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(workspace, query: query);

        var encodedCursor = Uri.EscapeDataString("cursor value+slash/");
        var response = await handler.HandleAsync(ConversationConsoleTestFactory.Request(
            "/api/sessions",
            query: "since=2026-11-20T10%3A00%3A00Z&until=2026-11-21T10%3A00%3A00Z&cursor=" + encodedCursor));

        Assert.Equal(200, response.StatusCode);

        var filter = Assert.Single(query.Filters);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero), filter.Since);
        Assert.Equal(new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero), filter.Until);
        Assert.Equal("cursor value+slash/", filter.Cursor);
    }

    [Theory]
    [InlineData("", ConversationConsoleDefaults.PageSize)]
    [InlineData("limit=0", ConversationConsoleDefaults.PageSize)]
    [InlineData("limit=-5", ConversationConsoleDefaults.PageSize)]
    [InlineData("limit=7", 7)]
    public async Task List_clamps_the_requested_page_size(string query, int expected)
    {
        using var workspace = new ConversationConsoleWorkspace();
        var fake = new FakeConversationQuery().WithSession("s-1", "discord:discord:11");
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(workspace, query: fake);

        await handler.HandleAsync(ConversationConsoleTestFactory.Request("/api/sessions", query: query));

        Assert.Equal(expected, Assert.Single(fake.Filters).Limit);
    }

    [Fact]
    public async Task List_never_asks_the_port_for_more_than_the_hard_limit()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var fake = new FakeConversationQuery().WithSession("s-1", "discord:discord:11");
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(workspace, query: fake);

        await handler.HandleAsync(ConversationConsoleTestFactory.Request("/api/sessions", query: "limit=100000"));

        Assert.Equal(ConversationQueryLimits.MaxPageSize, Assert.Single(fake.Filters).Limit);
    }

    [Fact]
    public async Task Messages_page_incrementally_with_after_and_expose_next_after()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var fake = new FakeConversationQuery()
            .WithSession("s-1", "discord:discord:11")
            .WithMessages("s-1", "one", "two", "three");
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(workspace, query: fake);

        var first = await handler.HandleAsync(ConversationConsoleTestFactory.Request(
            "/api/sessions/s-1/messages",
            query: "limit=2"));
        Assert.Equal(200, first.StatusCode);

        using (var document = JsonDocument.Parse(first.Body))
        {
            Assert.Equal(2, document.RootElement.GetProperty("items").GetArrayLength());
            Assert.Equal(2, document.RootElement.GetProperty("nextAfter").GetInt64());
            Assert.Equal("s-1", document.RootElement.GetProperty("sessionId").GetString());
        }

        var second = await handler.HandleAsync(ConversationConsoleTestFactory.Request(
            "/api/sessions/s-1/messages",
            query: "after=2&limit=2"));
        using (var document = JsonDocument.Parse(second.Body))
        {
            var items = document.RootElement.GetProperty("items");
            Assert.Equal(1, items.GetArrayLength());
            Assert.Equal("three", items[0].GetProperty("text").GetString());
        }

        Assert.Equal([(("s-1", 0L, 2)), ("s-1", 2L, 2)], fake.MessageCalls
            .Select(call => (call.SessionId, call.After, call.Limit))
            .ToArray());
    }

    [Fact]
    public async Task Messages_clamp_the_page_size_and_default_after_to_zero()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var fake = new FakeConversationQuery().WithMessages("s-1", "one");
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(workspace, query: fake);

        await handler.HandleAsync(ConversationConsoleTestFactory.Request(
            "/api/sessions/s-1/messages",
            query: "limit=99999"));

        var call = Assert.Single(fake.MessageCalls);
        Assert.Equal(0, call.After);
        Assert.Equal(ConversationQueryLimits.MaxMessagePageSize, call.Limit);
    }

    [Fact]
    public async Task Messages_are_truncated_at_the_display_bound_and_flagged()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var longText = new string('x', ConversationConsoleQueryGateway.MaxMessageCharacters + 100);
        var fake = new FakeConversationQuery().WithMessages("s-1", longText);
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(workspace, query: fake);

        var response = await handler.HandleAsync(ConversationConsoleTestFactory.Request("/api/sessions/s-1/messages"));
        using var document = JsonDocument.Parse(response.Body);
        var item = document.RootElement.GetProperty("items")[0];

        Assert.True(item.GetProperty("truncated").GetBoolean());
        Assert.Equal(
            ConversationConsoleQueryGateway.MaxMessageCharacters,
            item.GetProperty("text").GetString()!.Length);
    }

    [Fact]
    public async Task Session_id_is_url_decoded_before_reaching_the_port()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var fake = new FakeConversationQuery().WithMessages("session with space", "hello");
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(workspace, query: fake);

        var response = await handler.HandleAsync(ConversationConsoleTestFactory.Request(
            "/api/sessions/session%20with%20space/messages"));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("session with space", Assert.Single(fake.MessageCalls).SessionId);
    }

    [Fact]
    public async Task Missing_host_port_answers_501_for_every_data_route()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(workspace, query: null);

        var list = await handler.HandleAsync(ConversationConsoleTestFactory.Request("/api/sessions"));
        var messages = await handler.HandleAsync(
            ConversationConsoleTestFactory.Request("/api/sessions/s-1/messages"));

        foreach (var response in new[] { list, messages })
        {
            Assert.Equal(501, response.StatusCode);
            var body = Encoding.UTF8.GetString(response.Body);
            Assert.Contains("host.conversation-query.unavailable", body, StringComparison.Ordinal);
            Assert.Contains("chat.read", body, StringComparison.Ordinal);
        }

        // Health still answers so the UI can explain *why* the console is degraded.
        var health = await handler.HandleAsync(ConversationConsoleTestFactory.Request("/api/health"));
        Assert.Equal(200, health.StatusCode);
        using var document = JsonDocument.Parse(health.Body);
        Assert.False(document.RootElement.GetProperty("query").GetProperty("available").GetBoolean());
        // A-05: source is data, not status. With no injected port there is no source at all, and the
        // status booleans carry the "unavailable" meaning.
        var queryNode = document.RootElement.GetProperty("query");
        // The null source is omitted from JSON entirely; either shape means "no source".
        Assert.True(
            !queryNode.TryGetProperty("source", out var sourceNode) || sourceNode.ValueKind == JsonValueKind.Null,
            "an uninjected port must not name a source");
        Assert.False(queryNode.GetProperty("injected").GetBoolean());
        Assert.False(queryNode.GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task Empty_fallback_port_is_injected_but_not_available()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(
            workspace,
            query: new Natter.Application.Chat.EmptyConversationQuery());

        var health = await handler.HandleAsync(ConversationConsoleTestFactory.Request("/api/health"));
        Assert.Equal(200, health.StatusCode);
        using var document = JsonDocument.Parse(health.Body);
        var query = document.RootElement.GetProperty("query");
        Assert.False(query.GetProperty("available").GetBoolean());
        Assert.True(query.GetProperty("injected").GetBoolean());
        Assert.False(query.GetProperty("hasDataSource").GetBoolean());
        // The empty fallback is injected but has no data source, so the source name is absent.
        Assert.True(
            !query.TryGetProperty("source", out var fallbackSource) || fallbackSource.ValueKind == JsonValueKind.Null,
            "the empty fallback must not name a source");
        Assert.True(query.GetProperty("injected").GetBoolean());
        Assert.False(query.GetProperty("available").GetBoolean());

        var list = await handler.HandleAsync(ConversationConsoleTestFactory.Request("/api/sessions"));
        Assert.Equal(501, list.StatusCode);
        Assert.Contains("host.conversation-query.unavailable", Encoding.UTF8.GetString(list.Body), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("limit=abc")]
    [InlineData("since=not-a-date")]
    [InlineData("until=2026-13-45T99%3A99%3A99Z")]
    public async Task Malformed_filters_answer_400(string query)
    {
        using var workspace = new ConversationConsoleWorkspace();
        var fake = new FakeConversationQuery().WithSession("s-1", "discord:discord:11");
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(workspace, query: fake);

        var response = await handler.HandleAsync(
            ConversationConsoleTestFactory.Request("/api/sessions", query: query));

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("filter.invalid", Encoding.UTF8.GetString(response.Body), StringComparison.Ordinal);
        Assert.Empty(fake.Filters);
    }

    [Fact]
    public async Task Malformed_after_answers_400()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var fake = new FakeConversationQuery();
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(workspace, query: fake);

        var response = await handler.HandleAsync(ConversationConsoleTestFactory.Request(
            "/api/sessions/s-1/messages",
            query: "after=-1"));

        Assert.Equal(400, response.StatusCode);
        Assert.Empty(fake.MessageCalls);
    }

    [Fact]
    public async Task Unknown_api_route_answers_404()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(
            workspace,
            query: new FakeConversationQuery());

        var response = await handler.HandleAsync(ConversationConsoleTestFactory.Request("/api/nope"));

        Assert.Equal(404, response.StatusCode);
        Assert.Contains("route.not_found", Encoding.UTF8.GetString(response.Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Responses_carry_security_headers_and_never_leak_the_token()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(
            workspace,
            query: new FakeConversationQuery().WithSession("s-1", "discord:discord:11"));

        var response = await handler.HandleAsync(ConversationConsoleTestFactory.Request("/api/sessions"));
        var body = Encoding.UTF8.GetString(response.Body);

        Assert.Equal("no-store", response.Headers["Cache-Control"]);
        Assert.Equal("nosniff", response.Headers["X-Content-Type-Options"]);
        Assert.Equal("DENY", response.Headers["X-Frame-Options"]);

        var csp = response.Headers["Content-Security-Policy"];
        Assert.Contains("default-src 'none'", csp, StringComparison.Ordinal);
        // The invariant worth protecting is that SCRIPT stays strict. style-src carries
        // 'unsafe-inline' by design: Ant Design v5 styles through CSS-in-JS (runtime <style> injection,
        // no static .css in the bundle), and a nonce cannot cover the inline style attributes it sets
        // for dynamic geometry. Asserting "no unsafe-inline anywhere" would either fail on a correct
        // policy or pressure someone into weakening script-src to compensate — so scope the check.
        Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", csp, StringComparison.Ordinal);

        var styleDirective = csp.Split(';').Single(part => part.TrimStart().StartsWith("style-src", StringComparison.Ordinal));
        Assert.Contains("'self'", styleDirective, StringComparison.Ordinal);
        Assert.Contains("'unsafe-inline'", styleDirective, StringComparison.Ordinal);
        Assert.DoesNotContain("'unsafe-eval'", styleDirective, StringComparison.Ordinal);

        Assert.DoesNotContain(ConversationConsoleTestFactory.ValidToken, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_reports_limits_from_the_shared_contract()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(
            workspace,
            query: new FakeConversationQuery());

        var response = await handler.HandleAsync(ConversationConsoleTestFactory.Request("/api/health"));
        using var document = JsonDocument.Parse(response.Body);
        var limits = document.RootElement.GetProperty("limits");

        Assert.Equal(ConversationQueryLimits.MaxPageSize, limits.GetProperty("maxPageSize").GetInt32());
        Assert.Equal(ConversationQueryLimits.MaxMessagePageSize, limits.GetProperty("maxMessagePageSize").GetInt32());
        Assert.Equal(
            ConversationConsoleQueryGateway.MaxMessageCharacters,
            limits.GetProperty("maxMessageCharacters").GetInt32());
    }
}
