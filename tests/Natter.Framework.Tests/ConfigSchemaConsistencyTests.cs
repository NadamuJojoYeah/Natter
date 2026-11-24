using System.Text.Json;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Feature.Ai.DeepSeek;
using Natter.Feature.Ai.Spark;
using Natter.Infrastructure.Chat;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// Locks the "one fact, one definition" cleanups from the S3 architecture review:
/// C-04 (schema defaults), D-04 (schema bounds vs activation bounds), C-02 (decay modes) and
/// C-05 (single database path). Each test fails when the duplication is reintroduced.
/// </summary>
public sealed class ConfigSchemaConsistencyTests
{
    [Theory]
    [InlineData("src/Natter.Feature.Ai.DeepSeek/config.schema.json", typeof(DeepSeekNumericBounds))]
    [InlineData("src/Natter.Feature.Ai.Spark/config.schema.json", typeof(SparkNumericBounds))]
    public void Schema_bounds_match_the_activation_bounds(string schemaPath, Type boundsType)
    {
        var bounds = ReadBounds(boundsType);
        Assert.NotEmpty(bounds);

        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), schemaPath)));
        var properties = schema.RootElement.GetProperty("properties");

        foreach (var (name, (min, max)) in bounds)
        {
            Assert.True(properties.TryGetProperty(name, out var spec), $"{schemaPath}: '{name}' missing from schema");

            var schemaMin = spec.TryGetProperty("minimum", out var minElement) ? minElement.GetInt64() : (long?)null;
            var schemaMax = spec.TryGetProperty("maximum", out var maxElement) ? maxElement.GetInt64() : (long?)null;

            // D-04: the schema is what an operator validates against, the constant is what activation
            // enforces. If they drift, the schema accepts a config that then throws at startup.
            Assert.Equal(schemaMin, min);
            Assert.Equal(schemaMax, max);
        }
    }

    [Theory]
    [InlineData("src/Natter.Feature.Ai.DeepSeek/config.schema.json")]
    [InlineData("src/Natter.Feature.Ai.Spark/config.schema.json")]
    [InlineData("plugins/Natter.Plugin.AiEmbedding.OpenAi/config.schema.json")]
    [InlineData("samples/Natter.Sample.Plugin.Discord/config.schema.json")]
    [InlineData("samples/Natter.Sample.Plugin.DingTalk/config.schema.json")]
    public void Every_settable_property_documents_its_default(string schemaPath)
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), schemaPath)));

        var missing = new List<string>();
        var secretLike = new[] { "apiKey", "token", "clientId", "clientSecret", "botId", "robotCode", "selfId", "devGuildId" };

        foreach (var property in schema.RootElement.GetProperty("properties").EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
                continue;

            if (property.Value.TryGetProperty("default", out _))
                continue;

            // Secrets legitimately have no default: an empty default would look like a usable config.
            if (secretLike.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                continue;

            // A nullable property's "unset" is a documented state of its own, not a missing default.
            if (AllowsNull(property.Value))
                continue;

            // A property with no code initialiser and no schema default is documented drift.
            missing.Add(property.Name);
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void Every_plugin_schema_uses_the_same_default_convention()
    {
        // C-04: before this cleanup two conventions coexisted — 4 schemas declared defaults, 6 declared
        // none, so the effective default was only readable from C#.
        var schemas = Directory
            .EnumerateFiles(RepoRoot(), "config.schema.json", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           // Build/staging output is not source; a stale copy must not fail the check.
                           && !path.Contains($"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(schemas);

        var withoutDefaults = new List<string>();

        foreach (var path in schemas)
        {
            using var schema = JsonDocument.Parse(File.ReadAllText(path));
            if (!schema.RootElement.TryGetProperty("properties", out var properties))
                continue;

            // An empty property bag (the sample memory plugin) has nothing to document.
            if (properties.EnumerateObject().Any() && !schema.RootElement.GetRawText().Contains("\"default\"", StringComparison.Ordinal))
                withoutDefaults.Add(Path.GetRelativePath(RepoRoot(), path));
        }

        Assert.Empty(withoutDefaults);
    }

    [Fact]
    public void Legacy_decay_matches_the_historical_exponential()
    {
        // C-02: parameterising the mode must not change the shipped curve. These are the values the v1
        // path produced before the refactor, asserted per value so a "cleanup" cannot drift it.
        Assert.Equal(1d, MemoryDecay.Decay(0, 30, DecayMode.Legacy), 12);
        Assert.Equal(Math.Exp(-1d), MemoryDecay.Decay(30, 30, DecayMode.Legacy), 12);
        Assert.Equal(Math.Exp(-2d), MemoryDecay.Decay(60, 30, DecayMode.Legacy), 12);
        Assert.Equal(Math.Exp(-10d), MemoryDecay.Decay(300, 30, DecayMode.Legacy), 12);
    }

    [Fact]
    public void Power_law_mode_keeps_ninety_percent_at_one_window()
    {
        // The two curves are far apart on purpose (0.368 vs 0.9 at one window); the mode must select
        // one of them explicitly rather than by which function a caller happened to call.
        Assert.Equal(MemoryDecay.TargetRetention, MemoryDecay.Decay(30, 30, DecayMode.PowerLaw), 9);
        Assert.Equal(1d, MemoryDecay.Decay(0, 30, DecayMode.PowerLaw), 12);
        Assert.True(MemoryDecay.Decay(30, 30, DecayMode.PowerLaw) > MemoryDecay.Decay(30, 30, DecayMode.Legacy));
    }

    [Fact]
    public void Unknown_decay_mode_is_rejected_instead_of_silently_defaulting()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MemoryDecay.Decay(1, 30, (DecayMode)99));
    }

    [Fact]
    public void Stability_window_drives_the_score_and_defaults_to_the_shipped_curve()
    {
        var now = DateTimeOffset.UnixEpoch.AddDays(100);
        var updated = now.AddDays(-30);

        var legacy = MemoryScoring.Score("apple pie", "apple", updated, now, TimeSpan.FromDays(30));
        var explicitLegacy = MemoryScoring.Score("apple pie", "apple", updated, now, TimeSpan.FromDays(30), DecayMode.Legacy);
        var powerLaw = MemoryScoring.Score("apple pie", "apple", updated, now, TimeSpan.FromDays(30), DecayMode.PowerLaw);

        // Default argument is the shipped behaviour.
        Assert.Equal(explicitLegacy, legacy, 12);
        Assert.True(powerLaw > legacy);
    }

    [Fact]
    public void Database_path_is_defined_once_and_shared_by_every_store()
    {
        // C-05: six stores live in one file. This pins the single definition: a rename must change
        // ChatCompositionModule.DatabaseFileName and nothing else.
        Assert.Equal("natter.db", ChatCompositionModule.DatabaseFileName);

        var fromRoot = ChatCompositionModule.ResolveDatabasePath(
            new Natter.Abstractions.Composition.CompositionOptions { StateDirectory = "/var/lib/natter" });
        Assert.Equal(Path.Combine("/var/lib/natter", "natter.db"), fromRoot);

        // Relative state directories resolve to an absolute path, otherwise two stores could open
        // different files depending on the process working directory.
        var fromRelative = ChatCompositionModule.ResolveDatabasePath(
            new Natter.Abstractions.Composition.CompositionOptions { StateDirectory = "state" });
        Assert.NotNull(fromRelative);
        Assert.True(Path.IsPathRooted(fromRelative));

        // No state directory means every store falls back to memory, not to the current directory.
        Assert.Null(ChatCompositionModule.ResolveDatabasePath(
            new Natter.Abstractions.Composition.CompositionOptions { StateDirectory = null }));
        Assert.Null(ChatCompositionModule.ResolveDatabasePath(
            new Natter.Abstractions.Composition.CompositionOptions { StateDirectory = "   " }));
    }

    [Fact]
    public void The_database_file_name_appears_once_in_the_composition_module()
    {
        // The literal must exist only at the definition; six copies is exactly what caused the risk
        // of renaming the database and silently splitting storage in two.
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Natter.Infrastructure", "Chat", "ChatCompositionModule.cs"));

        var occurrences = source.Split("\"natter.db\"").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void Every_chat_upstream_that_sends_text_calls_the_chunker()
    {
        // C-06: chunking was a convention, not a contract — each adapter had to remember to call
        // ChatTextChunker.Chunk. This is a ratchet: a NEW adapter that forgets it fails here, and an
        // adapter listed as a known gap that starts chunking also fails (remove it from the list).
        var knownGaps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["src/Natter.Chat.Satori/SatoriChatUpstream.cs"] =
                "Satori sends the mapped request un-chunked; declared MaxMessageLength is not enforced. "
                + "Needs a fix in Natter.Chat.Satori (out of scope for the S3 cleanup batch).",
        };

        var adapters = Directory
            .EnumerateFiles(RepoRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => System.Text.RegularExpressions.Regex.IsMatch(
                File.ReadAllText(path), @":\s*IChatUpstream\b"))
            .Select(path => Path.GetRelativePath(RepoRoot(), path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(adapters);

        var unchunked = new List<string>();

        foreach (var adapter in adapters)
        {
            var chunks = File.ReadAllText(Path.Combine(RepoRoot(), adapter))
                .Contains("ChatTextChunker.Chunk", StringComparison.Ordinal);

            if (knownGaps.TryGetValue(adapter, out var reason))
            {
                // If the gap is closed, the entry must go: a stale exemption hides the next regression.
                Assert.False(chunks, $"{adapter} now chunks text — remove it from knownGaps ({reason})");
                continue;
            }

            if (!chunks)
                unchunked.Add(adapter);
        }

        Assert.Empty(unchunked);
    }

    /// <summary>True when the property's type permits null, i.e. "not set" is a documented state.</summary>
    private static bool AllowsNull(JsonElement spec)
    {
        if (!spec.TryGetProperty("type", out var type))
            return false;

        return type.ValueKind switch
        {
            JsonValueKind.String => string.Equals(type.GetString(), "null", StringComparison.Ordinal),
            JsonValueKind.Array => type.EnumerateArray()
                .Any(entry => entry.ValueKind == JsonValueKind.String
                              && string.Equals(entry.GetString(), "null", StringComparison.Ordinal)),
            _ => false,
        };
    }

    private static IReadOnlyDictionary<string, (long Min, long Max)> ReadBounds(Type boundsType)
        => (IReadOnlyDictionary<string, (long Min, long Max)>)boundsType
            .GetField("All")!
            .GetValue(null)!;

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Natter.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
