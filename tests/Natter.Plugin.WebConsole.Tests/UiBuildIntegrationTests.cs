using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Natter.Plugin.WebConsole.Tests;

/// <summary>
/// Front-end build integration (task-62): the hand-written wwwroot is gone, `wwwroot` is now Vite
/// output. These tests pin the contract the HttpListener has to honour for a Vite SPA — MIME for
/// every build artifact kind, client-side route fallback, cache policy, traversal rejection, CSP
/// unchanged — plus the packaging guarantees of the real build output.
/// </summary>
public sealed class UiBuildIntegrationTests
{
    private const string Csp =
        "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; "
        + "connect-src 'self'; font-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Natter.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? Directory.GetCurrentDirectory();
    }

    private static string BuiltRoot => Path.Combine(RepositoryRoot(), "plugins", "Natter.Plugin.WebConsole", "wwwroot");

    // ---------------------------------------------------------------- loader contract

    [Fact]
    public void Loader_requires_index_html_and_explains_how_to_build_it()
    {
        using var temp = new TempDirectory();

        var missingDirectory = Assert.Throws<WebConsoleStartupException>(() => { WebConsoleStaticAssets.Load(temp.Combine("nope")); });
        Assert.Equal("webconsole.static-assets-missing", missingDirectory.Code);
        Assert.Contains("npm run build", missingDirectory.Message, StringComparison.Ordinal);

        Directory.CreateDirectory(temp.Combine("empty"));
        var missingIndex = Assert.Throws<WebConsoleStartupException>(() => { WebConsoleStaticAssets.Load(temp.Combine("empty")); });
        Assert.Equal("webconsole.static-assets-missing", missingIndex.Code);
        Assert.Contains("index.html", missingIndex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_types_cover_every_build_artifact_kind()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("dist");
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        File.WriteAllText(Path.Combine(root, "index.html"), "<!doctype html>");

        foreach (var (name, _) in Artifacts())
            File.WriteAllText(Path.Combine(root, "assets", name), "x");

        var assets = WebConsoleStaticAssets.Load(root);
        foreach (var (name, contentType) in Artifacts())
        {
            Assert.True(assets.TryGet($"/assets/{name}", out var response), name);
            Assert.Equal(contentType, response.ContentType);
        }
    }

    [Theory]
    [InlineData(".js", "text/javascript; charset=utf-8")]
    [InlineData(".mjs", "text/javascript; charset=utf-8")]
    [InlineData(".css", "text/css; charset=utf-8")]
    [InlineData(".woff2", "font/woff2")]
    [InlineData(".woff", "font/woff")]
    [InlineData(".svg", "image/svg+xml")]
    [InlineData(".map", "application/json; charset=utf-8")]
    [InlineData(".png", "image/png")]
    [InlineData(".ico", "image/x-icon")]
    public void Mime_map_is_explicit(string extension, string expected)
    {
        Assert.Equal(expected, WebConsoleStaticAssets.ContentTypeFor("asset" + extension));
    }

    [Fact]
    public void Hashed_assets_are_immutable_and_the_document_is_never_cached()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("dist");
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        File.WriteAllText(Path.Combine(root, "index.html"), "<!doctype html>");
        File.WriteAllText(Path.Combine(root, "assets", "index-abc123.js"), "x");

        var assets = WebConsoleStaticAssets.Load(root);

        Assert.True(assets.TryGet("/assets/index-abc123.js", out var script));
        Assert.Equal(WebConsoleStaticAssets.ImmutableCacheControl, script.Headers["Cache-Control"]);

        Assert.True(assets.TryGet("/", out var document));
        Assert.Equal(WebConsoleStaticAssets.NoStoreCacheControl, document.Headers["Cache-Control"]);
    }

    // ---------------------------------------------------------------- SPA routing

    [Fact]
    public async Task Client_routes_fall_back_to_the_document_state()
    {
        using var harness = WebConsoleHarness.Create();
        var login = await harness.LoginAsync();

        foreach (var route in new[] { "/", "/overview", "/tasks/task_abc/trace", "/config/profiles" })
        {
            var response = await harness.Handler.HandleAsync(harness.Request("GET", route, cookie: login.Cookie));
            Assert.Equal(200, response.StatusCode);
            Assert.Equal("text/html; charset=utf-8", response.ContentType);
        }
    }

    [Fact]
    public async Task Api_routes_and_asset_lookalikes_never_fall_back()
    {
        using var harness = WebConsoleHarness.Create();
        var login = await harness.LoginAsync();

        // Unknown API route: authenticated -> 404 route.not_found (no index.html body).
        var api = await harness.Handler.HandleAsync(harness.Request("GET", "/api/unknown", cookie: login.Cookie));
        Assert.Equal(404, api.StatusCode);
        Assert.Equal("application/json; charset=utf-8", api.ContentType);

        // Unauthenticated API route still answers 401 rather than serving the document.
        var anonymous = await harness.Handler.HandleAsync(harness.Request("GET", "/api/snapshot"));
        Assert.Equal(401, anonymous.StatusCode);

        // A missing hashed asset must 404: a 200 with HTML would poison the module loader.
        var asset = await harness.Handler.HandleAsync(harness.Request("GET", "/assets/missing-123.js", cookie: login.Cookie));
        Assert.Equal(404, asset.StatusCode);
    }

    [Theory]
    [InlineData("/../plugin.json")]
    [InlineData("/assets/../../plugin.json")]
    [InlineData("/%2e%2e/plugin.json")]
    [InlineData("/..%2fplugin.json")]
    [InlineData("/assets/..%2f..%2fplugin.json")]
    public async Task Traversal_paths_are_never_served(string path)
    {
        using var harness = WebConsoleHarness.Create();
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request("GET", path, cookie: login.Cookie));

        Assert.NotEqual(200, response.StatusCode);
        Assert.DoesNotContain("plugin.json", Encoding.UTF8.GetString(response.Body), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_files_under_the_asset_root_are_reachable()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("dist");
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        File.WriteAllText(Path.Combine(root, "index.html"), "<!doctype html>");
        File.WriteAllText(Path.Combine(root, "assets", "app-1.js"), "x");
        File.WriteAllText(temp.Combine("outside.json"), "{\"secret\":true}");

        var assets = WebConsoleStaticAssets.Load(root);

        Assert.DoesNotContain(assets.Files, route => route.Contains("..", StringComparison.Ordinal));
        Assert.All(assets.Files, route => Assert.StartsWith("/", route, StringComparison.Ordinal));
        Assert.False(assets.TryGet("/outside.json", out _));
        Assert.False(assets.TryGet("/../outside.json", out _));
    }

    [Fact]
    public async Task Csp_is_still_the_documented_policy()
    {
        using var harness = WebConsoleHarness.Create();
        var response = await harness.Handler.HandleAsync(harness.Request("GET", "/api/session"));

        var csp = response.Headers["Content-Security-Policy"];
        Assert.Equal(Csp, csp);

        // Assert the SHAPE, not the absence of a keyword: script-src must stay strict, while style-src
        // carries 'unsafe-inline' because Ant Design v5 injects <style> at runtime and the bundle ships
        // no static .css. A blanket DoesNotContain("unsafe-inline") would fail on that deliberate
        // choice and invite someone to relax script-src instead — the exact regression to prevent.
        Assert.Contains("script-src 'self';", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("'unsafe-inline'", csp.Replace("style-src 'self' 'unsafe-inline'", string.Empty), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- real build output

    [Fact]
    public void Built_index_html_points_at_a_local_hashed_script()
    {
        if (!File.Exists(Path.Combine(BuiltRoot, "index.html")))
        {
            // Local loop without npm is tolerated; CI sets NATTER_REQUIRE_UI_BUILD=1 so a missing
            // build is a hard failure instead of a silent skip.
            Assert.NotEqual("1", Environment.GetEnvironmentVariable("NATTER_REQUIRE_UI_BUILD"));
            return;
        }

        var html = File.ReadAllText(Path.Combine(BuiltRoot, "index.html"));
        var scripts = Regex.Matches(html, "<script(?![^>]*\\bsrc=)[^>]*>");
        Assert.Empty(scripts);

        var src = Regex.Match(html, "src=\"(/assets/index-[A-Za-z0-9_-]+\\.js)\"");
        Assert.True(src.Success, "index.html must reference a hashed entry chunk");
        Assert.True(File.Exists(Path.Combine(BuiltRoot, src.Groups[1].Value.TrimStart('/'))), src.Groups[1].Value);
    }

    [Fact]
    public void Built_assets_have_no_external_module_imports_and_no_eval()
    {
        if (!Directory.Exists(Path.Combine(BuiltRoot, "assets")))
            return;

        var scripts = Directory.GetFiles(Path.Combine(BuiltRoot, "assets"), "*.js", SearchOption.AllDirectories);
        Assert.NotEmpty(scripts);

        var external = new Regex(
            @"from\s*[""'](?:https?:)?//|import\s*\(\s*[""'](?:https?:)?//|src\s*=\s*[""'](?:https?:)?//",
            RegexOptions.IgnoreCase);

        foreach (var script in scripts)
        {
            var text = File.ReadAllText(script);
            Assert.DoesNotMatch(external, text);
            Assert.DoesNotContain("eval(", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Built_output_contains_no_development_leftovers()
    {
        if (!Directory.Exists(BuiltRoot))
            return;

        var files = Directory.GetFiles(BuiltRoot, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetFileName(file))
            .ToArray();

        Assert.DoesNotContain(files, name => name.EndsWith(".ts", StringComparison.Ordinal));
        Assert.DoesNotContain(files, name => name.EndsWith(".tsx", StringComparison.Ordinal));
        Assert.DoesNotContain(files, name => name.Contains("node_modules", StringComparison.Ordinal));
    }

    /// <summary>Throwaway directory with a <c>Combine</c> shortcut; deleted on dispose.</summary>
    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "natter-ui-assets-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Combine(string relative) => Path.Combine(Root, relative);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    private static IEnumerable<(string Name, string ContentType)> Artifacts()
    {
        yield return ("index-abc.js", "text/javascript; charset=utf-8");
        yield return ("index-abc.css", "text/css; charset=utf-8");
        yield return ("inter-1.woff2", "font/woff2");
        yield return ("logo-2.svg", "image/svg+xml");
        yield return ("index-abc.js.map", "application/json; charset=utf-8");
        yield return ("icon-3.png", "image/png");
    }
}
