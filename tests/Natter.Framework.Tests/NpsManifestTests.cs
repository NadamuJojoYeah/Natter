using System.Text.Json;
using Natter.Plugin.Abstractions;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class NpsManifestTests
{
    [Fact]
    public void Semver_precedence_and_ranges_follow_the_spec_subset()
    {
        Assert.True(SemanticVersion.TryParse("1.2.3-alpha.1+build.5", out var alpha));
        Assert.True(SemanticVersion.TryParse("1.2.3", out var release));
        Assert.True(alpha!.CompareTo(release) < 0);
        Assert.True(SemanticVersion.TryParse("1.0.0", out var one));
        Assert.True(SemanticVersion.TryParse("1.0.0-alpha", out var oneAlpha));
        Assert.True(oneAlpha!.CompareTo(one) < 0);

        Assert.True(VersionRange.TryParse(">=0.1.0 <1.0.0", out var range));
        Assert.True(range!.IsSatisfiedBy(SemanticVersion.Parse("0.4.2")));
        Assert.False(range.IsSatisfiedBy(SemanticVersion.Parse("1.0.0")));
        Assert.False(range.IsSatisfiedBy(SemanticVersion.Parse("0.2.0-alpha.1")));

        Assert.True(VersionRange.TryParse(">=0.1.0-alpha <0.2.0", out var prereleaseRange));
        Assert.True(prereleaseRange!.IsSatisfiedBy(SemanticVersion.Parse("0.1.0-beta.1")));

        Assert.True(VersionRange.TryParse("1.2.3 || >=2.0.0 <3.0.0", out var union));
        Assert.True(union!.IsSatisfiedBy(SemanticVersion.Parse("1.2.3")));
        Assert.True(union.IsSatisfiedBy(SemanticVersion.Parse("2.5.0")));
        Assert.False(union.IsSatisfiedBy(SemanticVersion.Parse("1.2.4")));
    }

    [Fact]
    public void Manifest_reader_accepts_a_valid_nps_manifest()
    {
        var json = """
            {
              "schemaVersion": 1,
              "id": "natter.transport.discord",
              "name": "Discord Transport",
              "version": "0.1.0",
              "kind": "transport",
              "hostApi": ">=0.1.0 <1.0.0",
              "entry": { "assembly": "lib/Discord.dll", "type": "Discord.DiscordPlugin" },
              "dependsOn": [{ "id": "natter.chat.core", "version": ">=0.1.0 <1.0.0" }],
              "permissions": ["network", "chat.read", "chat.send"],
              "contributions": [{ "type": "chat.upstream", "id": "discord" }],
              "platforms": ["linux", "windows"]
            }
            """;

        var result = PluginManifestReader.Parse(json);

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(error => error.Message)));
        Assert.Equal("natter.transport.discord", result.Manifest!.Id);
        Assert.Equal(PluginKind.Transport, result.Manifest.Kind);
        Assert.Equal("discord", result.Manifest.FindContribution(PluginContributionTypes.ChatUpstream, "discord")!.Id);
    }

    [Fact]
    public void Manifest_reader_reports_unknown_properties_and_invalid_paths()
    {
        var json = """
            {
              "schemaVersion": 1,
              "id": "Bad Id",
              "name": "Bad",
              "version": "not-semver",
              "kind": "feature",
              "hostApi": "not-a-range",
              "entry": { "assembly": "../escape.dll", "type": "Bad.Plugin" },
              "unexpected": true,
              "configSchema": "../schema.json"
            }
            """;

        var result = PluginManifestReader.Parse(json);
        var paths = result.Errors.Select(error => error.Path).ToList();

        Assert.False(result.IsValid);
        Assert.Contains("/id", paths);
        Assert.Contains("/version", paths);
        Assert.Contains("/hostApi", paths);
        Assert.Contains("/entry/assembly", paths);
        Assert.Contains("/unexpected", paths);
        Assert.Contains("/configSchema", paths);
    }

    [Fact]
    public void Manifest_reader_requires_secret_permission_for_required_secrets()
    {
        var json = """
            {
              "schemaVersion": 1,
              "id": "natter.feature.ai",
              "name": "AI",
              "version": "0.1.0",
              "kind": "feature",
              "hostApi": ">=0.1.0",
              "entry": { "assembly": "ai.dll", "type": "Ai.Plugin" },
              "secrets": [{ "name": "OPENAI_API_KEY", "required": true }],
              "permissions": ["network"]
            }
            """;

        var result = PluginManifestReader.Parse(json);

        Assert.Contains(result.Errors, error => error.Code == "missing-secret-permission");
    }

    [Fact]
    public void Config_schema_subset_validates_and_applies_defaults()
    {
        var schema = JsonSerializer.Deserialize<JsonElement>("""
            {
              "type": "object",
              "required": ["name"],
              "additionalProperties": false,
              "properties": {
                "name": { "type": "string", "pattern": "^[a-z]+$" },
                "count": { "type": "integer", "default": 3, "minimum": 1, "maximum": 10 },
                "mode": { "type": "string", "enum": ["fast", "safe"] },
                "tags": { "type": "array", "items": { "type": "string" } }
              }
            }
            """);
        var config = JsonSerializer.Deserialize<JsonElement>("""{ "name": "ok" }""");

        var effective = PluginConfigSchema.ApplyDefaults(schema, config);
        Assert.Equal(3, effective.GetProperty("count").GetInt32());
        Assert.Empty(PluginConfigSchema.Validate(schema, effective));

        var invalid = JsonSerializer.Deserialize<JsonElement>("""{ "name": "BAD", "count": 20, "unknown": true }""");
        var issues = PluginConfigSchema.Validate(schema, invalid);
        var pointers = issues.Select(issue => issue.Pointer).ToList();

        Assert.Contains("/name", pointers);
        Assert.Contains("/count", pointers);
        Assert.Contains("/unknown", pointers);
    }

    [Fact]
    public void Config_schema_oneof_and_pointer_escaping()
    {
        var schema = JsonSerializer.Deserialize<JsonElement>("""
            {
              "type": "object",
              "properties": {
                "a/b": {
                  "oneOf": [
                    { "type": "string" },
                    { "type": "integer" }
                  ]
                }
              }
            }
            """);
        var config = JsonSerializer.Deserialize<JsonElement>("""{ "a/b": true }""");

        var issues = PluginConfigSchema.Validate(schema, config);

        Assert.Contains(issues, issue => issue.Pointer == "/a~1b");
    }

    [Fact]
    public void Secret_reference_round_trips_without_exposing_a_value()
    {
        Assert.True(SecretReference.TryParse("${secret:MY_TOKEN}", out var reference));
        Assert.Equal("MY_TOKEN", reference.Name);
        Assert.Equal("${secret:MY_TOKEN}", reference.ToString());
        Assert.False(SecretReference.TryParse("plain", out _));
    }

    // ---------------------------------------------------------------- dataDirs
    [Fact]
    public void DataDirs_are_accepted_and_relative_paths_are_kept()
    {
        // Regression: `dataDirs` was added to a real manifest before the reader knew it, and the strict
        // unknown-property whitelist then marked the plugin `manifest-invalid` — which took WebConsole
        // and ConversationConsole offline on 2026-11-23.
        using var plugin = ManifestDir("""
        {
          "schemaVersion": 1, "id": "natter.test.datadirs", "name": "D", "version": "0.1.0",
          "kind": "tool", "hostApi": ">=0.1.0 <1.0.0",
          "entry": { "assembly": "lib/D.dll", "type": "D.Plugin" },
          "dataDirs": [ { "path": "audit", "purpose": "audit trail" } ]
        }
        """);

        var result = PluginManifestReader.Read(plugin.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.Code + "@" + e.Path)));
        var declared = Assert.Single(result.Manifest!.DataDirs!);
        Assert.Equal("audit", declared.Path);
    }

    [Theory]
    [InlineData("/etc")]
    [InlineData("../escape")]
    [InlineData("nested/../../escape")]
    public void DataDirs_reject_absolute_and_traversal_paths(string path)
    {
        // dataDirs become writable by the service user at deploy time; a manifest must not be able to
        // aim that grant outside its own package.
        using var plugin = ManifestDir($$"""
        {
          "schemaVersion": 1, "id": "natter.test.datadirs", "name": "D", "version": "0.1.0",
          "kind": "tool", "hostApi": ">=0.1.0 <1.0.0",
          "entry": { "assembly": "lib/D.dll", "type": "D.Plugin" },
          "dataDirs": [ { "path": "{{path}}" } ]
        }
        """);

        var result = PluginManifestReader.Read(plugin.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path.StartsWith("/dataDirs/", StringComparison.Ordinal));
    }

    /// <summary>
    /// Writes a manifest into a throwaway plugin directory. <see cref="PluginManifestReader.Read"/>
    /// takes a directory, not a JSON string, so the file has to exist on disk.
    /// </summary>
    private static TempDir ManifestDir(string manifestJson, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"natter-manifest-{line}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), manifestJson);
        return new TempDir(dir);
    }

    private sealed class TempDir(string path) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }

}
