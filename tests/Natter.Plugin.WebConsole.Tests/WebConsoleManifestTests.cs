using System.Reflection;
using System.Text.Json;
using Natter.Plugin.Abstractions;
using Xunit;

namespace Natter.Plugin.WebConsole.Tests;

public sealed class WebConsoleManifestTests
{
    private static string PackagePath(string name)
        => Path.Combine(AppContext.BaseDirectory, "plugin-package", name);

    [Fact]
    public void Embedded_manifest_is_valid_and_matches_package_manifest()
    {
        using var embeddedStream = typeof(WebConsolePlugin).Assembly
            .GetManifestResourceStream("Natter.Plugin.WebConsole.plugin.json");
        Assert.NotNull(embeddedStream);

        using var reader = new StreamReader(embeddedStream!);
        var embeddedJson = reader.ReadToEnd();
        var packagedJson = File.ReadAllText(PackagePath("plugin.json"));

        var embedded = PluginManifestReader.Parse(embeddedJson);
        var packaged = PluginManifestReader.Parse(packagedJson);

        Assert.True(embedded.IsValid);
        Assert.True(packaged.IsValid);
        Assert.Empty(embedded.Errors);
        Assert.Empty(packaged.Errors);

        Assert.Equal("natter.plugin.webconsole", packaged.Manifest!.Id);
        Assert.Equal(PluginKind.Tool, packaged.Manifest.Kind);
        Assert.Equal("lib/Natter.Plugin.WebConsole.dll", packaged.Manifest.Entry.Assembly);
        Assert.Equal(typeof(WebConsolePlugin).FullName, packaged.Manifest.Entry.Type);
        Assert.DoesNotContain("android", packaged.Manifest.Platforms!);
        Assert.Contains("WEBCONSOLE_TOKEN", packaged.Manifest.Secrets!.Select(secret => secret.Name));
        Assert.Contains("secrets:WEBCONSOLE_TOKEN", packaged.Manifest.Permissions!);
        Assert.NotNull(packaged.Manifest.FindContribution("cli.command", "web.status"));
    }

    [Fact]
    public void Packaged_manifest_metadata_is_stable()
    {
        var manifest = WebConsolePlugin.Manifest;

        Assert.Equal("0.1.0", manifest.Version);
        Assert.Equal(">=0.1.0 <1.0.0", manifest.HostApi);
        Assert.Equal(1, manifest.SchemaVersion);
        Assert.NotNull(manifest.ConfigSchema);
    }

    [Fact]
    public void Sample_config_passes_schema_validation()
    {
        var schema = JsonDocument.Parse(File.ReadAllText(PackagePath("config.schema.json"))).RootElement;
        var sample = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "plugin-package", "config.sample.json"))).RootElement;

        var effective = PluginConfigSchema.ApplyDefaults(schema, sample);
        var issues = PluginConfigSchema.Validate(schema, effective);

        Assert.Empty(issues);
        Assert.Equal("127.0.0.1", effective.GetProperty("bind").GetString());
        Assert.Equal(5090, effective.GetProperty("port").GetInt32());
        Assert.Equal("${secret:WEBCONSOLE_TOKEN}", effective.GetProperty("token").GetString());
        Assert.Equal("http://127.0.0.1:5099/health", effective.GetProperty("hostHealthUrl").GetString());
    }

    [Fact]
    public void Schema_rejects_plaintext_token()
    {
        var schema = JsonDocument.Parse(File.ReadAllText(PackagePath("config.schema.json"))).RootElement;
        var config = JsonSerializer.SerializeToElement(new
        {
            token = "plaintext-token",
            bind = "127.0.0.1",
            port = 5090,
        });

        var issues = PluginConfigSchema.Validate(schema, config);

        Assert.Contains(issues, issue => issue.Pointer == "/token");
    }

    [Fact]
    public void Schema_rejects_non_loopback_bind_and_out_of_range_port()
    {
        var schema = JsonDocument.Parse(File.ReadAllText(PackagePath("config.schema.json"))).RootElement;
        var config = JsonSerializer.SerializeToElement(new
        {
            token = "${secret:WEBCONSOLE_TOKEN}",
            bind = "0.0.0.0",
            port = 70000,
        });

        var issues = PluginConfigSchema.Validate(schema, config);

        Assert.Contains(issues, issue => issue.Pointer == "/bind");
        Assert.Contains(issues, issue => issue.Pointer == "/port");
    }

    [Fact]
    public void Options_bind_from_schema_defaults()
    {
        var schema = JsonDocument.Parse(File.ReadAllText(PackagePath("config.schema.json"))).RootElement;
        var effective = PluginConfigSchema.ApplyDefaults(schema, JsonSerializer.SerializeToElement(new { }));
        var options = effective.Deserialize<WebConsoleOptions>(new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        Assert.NotNull(options);
        Assert.Equal("127.0.0.1", options!.Bind);
        Assert.Equal(5090, options.Port);
        Assert.Equal(WebConsoleDefaults.TokenReference, options.Token);
        Assert.True(options.ReadOnly);
        Assert.Empty(WebConsoleOptionsValidator.Validate(options));
    }

    [Fact]
    public void Manifest_entry_type_exists()
    {
        var assembly = typeof(WebConsolePlugin).Assembly;
        var type = assembly.GetType(WebConsolePlugin.Manifest.Entry.Type, throwOnError: false);

        Assert.NotNull(type);
        Assert.True(typeof(INatterPlugin).IsAssignableFrom(type));
    }
}
