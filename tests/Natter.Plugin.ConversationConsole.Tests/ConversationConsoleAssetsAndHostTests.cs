using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Natter.Application.Chat;
using Natter.Plugin.Abstractions;
using Xunit;

namespace Natter.Plugin.ConversationConsole.Tests;

/// <summary>
/// Packaging and hosting guarantees: zero-build assets with no external imports and no inline
/// script, a manifest that asks only for what it uses, and a real loopback listener that answers
/// 401/200 with the documented headers.
/// </summary>
public sealed class ConversationConsoleAssetsAndHostTests
{
    private static readonly string AssetRoot = ConversationConsoleRuntime.ResolveStaticRoot(
        Path.Combine(AppContext.BaseDirectory, "plugin-package"));

    // ------------------------------------------------------------------ assets

    [Fact]
    public void Static_assets_are_served_with_explicit_content_types()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(workspace, query: new FakeConversationQuery());

        AssertAsset(handler, "/", "text/html; charset=utf-8");

        // wwwroot is Vite output now: one hashed entry chunk, no hand-written app.js/app.css.
        var script = Directory.GetFiles(AssetRoot, "*.js", SearchOption.AllDirectories)
            .Select(file => "/" + Path.GetRelativePath(AssetRoot, file).Replace(Path.DirectorySeparatorChar, '/'))
            .First();
        AssertAsset(handler, script, "text/javascript; charset=utf-8");

        var asset = handler.HandleAsync(ConversationConsoleTestFactory.Request(script)).GetAwaiter().GetResult();
        Assert.Equal(ConversationConsoleStaticAssets.ImmutableCacheControl, asset.Headers["Cache-Control"]);
    }

    [Theory]
    [InlineData("/app.js.map")]
    [InlineData("/secret.txt")]
    [InlineData("/wwwroot/app.js")]
    [InlineData("/../appsettings.json")]
    [InlineData("/%2e%2e/app.css")]
    public async Task Unknown_or_traversal_paths_are_never_served(string path)
    {
        using var workspace = new ConversationConsoleWorkspace();
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(workspace, query: new FakeConversationQuery());

        var response = await handler.HandleAsync(ConversationConsoleTestFactory.Request(path));

        Assert.Equal(404, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.ContentType);
    }

    [Fact]
    public void Index_html_has_no_inline_script_and_loads_only_local_assets()
    {
        var html = File.ReadAllText(Path.Combine(AssetRoot, "index.html"));

        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);

        var scriptTags = ExtractTags(html, "script");
        Assert.Single(scriptTags);
        Assert.Contains("type=\"module\"", scriptTags[0], StringComparison.Ordinal);
        Assert.Matches(@"src=""/assets/index-[A-Za-z0-9_-]+\.js""", scriptTags[0]);

        var styleTags = ExtractTags(html, "style");
        Assert.Empty(styleTags);

        // No inline event handlers: CSP would block them, and they are an injection foothold.
        foreach (var handler in new[] { "onclick=", "onload=", "onerror=", "onsubmit=", "onchange=" })
            Assert.DoesNotContain(handler, html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_wwwroot_file_references_the_network_or_a_cdn()
    {
        var files = Directory.GetFiles(AssetRoot, "*", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        // Bundled vendors contain documentation URLs as *strings*; what matters is that nothing is
        // loaded from another origin: no external script src, no external module import, no CDN host.
        var externalLoad = new System.Text.RegularExpressions.Regex(
            @"from\s*[""'](?:https?:)?//|import\s*\(\s*[""'](?:https?:)?//|src\s*=\s*[""'](?:https?:)?//|"
            + @"cdn\.jsdelivr|unpkg\.com|use\.typekit|fonts\.googleapis",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (var file in files)
        {
            var content = File.ReadAllText(file);

            Assert.DoesNotMatch(externalLoad, content);
            Assert.DoesNotContain("eval(", content, StringComparison.Ordinal);
            Assert.DoesNotContain("localStorage", content, StringComparison.Ordinal);
        }
    }


    // ------------------------------------------------------------------ manifest

    [Fact]
    public void Manifest_declares_only_the_permissions_the_console_uses()
    {
        var manifest = ConversationConsolePlugin.Manifest;

        Assert.Equal("natter.tool.conversation.conversation-console", manifest.Id);
        Assert.Equal(PluginKind.Tool, manifest.Kind);
        Assert.Empty(manifest.Contributions ?? []);

        var permissions = manifest.Permissions ?? [];
        Assert.Contains(PluginPermissions.ChatRead, permissions);
        Assert.Contains(PluginPermissions.Network, permissions);
        Assert.Contains(PluginPermissions.Secret(ConversationConsoleDefaults.TokenSecretName), permissions);

        // The console must never inherit WebConsole's capabilities or credentials.
        Assert.DoesNotContain(PluginPermissions.AdminConfigWrite, permissions);
        Assert.DoesNotContain(PluginPermissions.AdminCommandExecute, permissions);
        Assert.DoesNotContain("admin.read", permissions);
        Assert.DoesNotContain(PluginPermissions.Secret("WEBCONSOLE_TOKEN"), permissions);

        var secret = Assert.Single(manifest.Secrets ?? []);
        Assert.Equal(ConversationConsoleDefaults.TokenSecretName, secret.Name);
        Assert.True(secret.Required);
    }

    [Fact]
    public void Config_schema_and_sample_match_the_options_defaults()
    {
        var packageRoot = Path.Combine(AppContext.BaseDirectory, "plugin-package");
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(packageRoot, "config.schema.json")));
        using var sample = JsonDocument.Parse(File.ReadAllText(Path.Combine(packageRoot, "config.sample.json")));

        var defaults = new ConversationConsoleOptions();
        var properties = schema.RootElement.GetProperty("properties");

        Assert.Equal(defaults.Port, sample.RootElement.GetProperty("port").GetInt32());
        Assert.Equal(defaults.Port, properties.GetProperty("port").GetProperty("default").GetInt32());
        Assert.Equal(defaults.PageSize, properties.GetProperty("pageSize").GetProperty("default").GetInt32());
        Assert.Equal(
            defaults.MessagePageSize,
            properties.GetProperty("messagePageSize").GetProperty("default").GetInt32());
        Assert.Equal(
            ConversationConsoleDefaults.TokenReference,
            sample.RootElement.GetProperty("token").GetString());
        Assert.True(sample.RootElement.GetProperty("readOnly").GetBoolean());
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
    }

    // ------------------------------------------------------------------ hosting

    [Fact]
    public async Task Runtime_refuses_to_start_without_a_state_directory()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var runtime = new ConversationConsoleRuntime(
            new ConversationConsoleOptions { Port = 5099 },
            workspace.PluginDir,
            new RecordingPluginLog(),
            TimeProvider.System,
            stateDirectoryProvider: () => null);

        var exception = await Assert.ThrowsAsync<ConversationConsoleStartupException>(
            async () => await runtime.StartAsync(
                new FakeSecrets(ConversationConsoleDefaults.TokenSecretName),
                new FakeServiceProvider().Add<IConversationQuery>(new FakeConversationQuery())));

        Assert.Equal("conversation-console.state-dir-required", exception.Code);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Runtime_reports_whether_the_host_granted_the_query_port()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var secrets = new FakeSecrets(ConversationConsoleDefaults.TokenSecretName);

        var withoutPort = await StartAsync(workspace, secrets, provider: new FakeServiceProvider());
        Assert.False(withoutPort.Runtime.QueryAvailable);
        Assert.Equal("unavailable", withoutPort.Runtime.QuerySource);

        using var second = new ConversationConsoleWorkspace();
        var withPort = await StartAsync(
            second,
            secrets,
            provider: new FakeServiceProvider().Add<IConversationQuery>(
                new FakeConversationQuery().WithSession("s-1", "discord:discord:11")));

        Assert.True(withPort.Runtime.QueryAvailable);
        Assert.Equal("host.conversation-query", withPort.Runtime.QuerySource);
    }

    [Fact]
    public async Task Loopback_listener_serves_the_console_with_token_auth()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var secrets = new FakeSecrets(ConversationConsoleDefaults.TokenSecretName);
        var query = new FakeConversationQuery()
            .WithSession("s-1", "discord:discord:11")
            .WithMessages("s-1", "hello over http");
        query.NextCursor = null;

        var host = await StartAsync(
            workspace,
            secrets,
            provider: new FakeServiceProvider().Add<IConversationQuery>(query));

        using var client = new HttpClient { BaseAddress = new Uri(host.Runtime.Url + "/") };

        var anonymous = await client.GetAsync("api/health");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("Bearer", anonymous.Headers.WwwAuthenticate.ToString());

        using (var authorizedRequest = new HttpRequestMessage(HttpMethod.Get, "api/health"))
        {
            authorizedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secrets.Token);
            var health = await client.SendAsync(authorizedRequest);

            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.Equal("no-store", health.Headers.CacheControl?.ToString());
            Assert.True(health.Headers.Contains("Content-Security-Policy"));

            var payload = await health.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(payload);
            Assert.True(document.RootElement.GetProperty("readOnly").GetBoolean());
            Assert.True(document.RootElement.GetProperty("query").GetProperty("available").GetBoolean());
        }

        using (var sessionsRequest = new HttpRequestMessage(HttpMethod.Get, "api/sessions"))
        {
            sessionsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secrets.Token);
            var sessions = await client.SendAsync(sessionsRequest);

            Assert.Equal(HttpStatusCode.OK, sessions.StatusCode);
            var body = await sessions.Content.ReadAsStringAsync();
            Assert.Contains("discord:discord:11", body, StringComparison.Ordinal);

            // The token is never echoed back in a response body.
            Assert.DoesNotContain(secrets.Token, body, StringComparison.Ordinal);
        }

        var hashedScript = Directory.GetFiles(AssetRoot, "*.js", SearchOption.AllDirectories)
            .Select(file => "/" + Path.GetRelativePath(AssetRoot, file).Replace(Path.DirectorySeparatorChar, '/'))
            .First();
        var script = await client.GetAsync(hashedScript);
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal("text/javascript; charset=utf-8", script.Content.Headers.ContentType?.ToString());
        Assert.Contains("<div id=\"root\">", await (await client.GetAsync("/")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listener_rejects_mutating_verbs_end_to_end()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var secrets = new FakeSecrets(ConversationConsoleDefaults.TokenSecretName);
        var host = await StartAsync(
            workspace,
            secrets,
            provider: new FakeServiceProvider().Add<IConversationQuery>(new FakeConversationQuery()));

        using var client = new HttpClient { BaseAddress = new Uri(host.Runtime.Url + "/") };
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/sessions")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secrets.Token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Contains(
            "method.not_allowed",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    private static void AssertAsset(
        ConversationConsoleRequestHandler handler,
        string path,
        string expectedContentType)
    {
        var response = handler.HandleAsync(ConversationConsoleTestFactory.Request(path))
            .AsTask()
            .GetAwaiter()
            .GetResult();

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(expectedContentType, response.ContentType);
        Assert.NotEmpty(response.Body);
    }

    private static string[] ExtractTags(string html, string tag)
    {
        var result = new List<string>();
        var search = "<" + tag;
        var index = html.IndexOf(search, StringComparison.OrdinalIgnoreCase);

        while (index >= 0)
        {
            var end = html.IndexOf('>', index);
            if (end < 0)
                break;

            result.Add(html[index..(end + 1)]);
            index = html.IndexOf(search, end, StringComparison.OrdinalIgnoreCase);
        }

        return result.ToArray();
    }

    private static async Task<RunningConsole> StartAsync(
        ConversationConsoleWorkspace workspace,
        FakeSecrets secrets,
        FakeServiceProvider provider)
    {
        var log = new RecordingPluginLog();

        // Ports are allocated by trying: the console binds real loopback sockets in these tests.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var runtime = new ConversationConsoleRuntime(
                new ConversationConsoleOptions { Port = 52000 + Random.Shared.Next(0, 2000) },
                workspace.PluginDir,
                log,
                TimeProvider.System,
                stateDirectoryProvider: () => workspace.StateDir);

            try
            {
                await runtime.StartAsync(secrets, provider);
                return new RunningConsole(runtime);
            }
            catch (ConversationConsoleStartupException exception) when (exception.Code == "conversation-console.port-in-use")
            {
                await runtime.DisposeAsync();
            }
        }

        throw new InvalidOperationException("Could not bind a loopback port for the conversation console tests.");
    }

    private sealed class RunningConsole(ConversationConsoleRuntime runtime) : IAsyncDisposable
    {
        public ConversationConsoleRuntime Runtime { get; } = runtime;

        public async ValueTask DisposeAsync() => await Runtime.DisposeAsync();
    }
}
