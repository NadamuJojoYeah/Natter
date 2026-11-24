using System.Text.Json;
using System.Text.RegularExpressions;
using Natter.Plugin.Abstractions;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// Naming rule <c>natter.&lt;kind&gt;.&lt;domain&gt;.&lt;name&gt;</c>: the id prefix must equal the
/// manifest kind, and the install directory must equal the full id (scheme A).
/// </summary>
public sealed class PluginNamingTests
{
    private static readonly string[] OldIds =
    [
        "natter.transport.discord",
        "natter.transport.dingtalk",
        "natter.plugin.webconsole",
        "natter.plugin.conversation-console",
        "natter.prompt.library",
        "natter.plugin.state-vitals",
        "natter.plugin.ai-embedding.openai",
        "natter.sample.greetings",
        "natter.sample.metrics",
        "natter.sample.memory",
    ];

    [Fact]
    public void Every_shipped_manifest_id_matches_its_kind()
    {
        var manifests = ShippedManifests();
        Assert.Equal(12, manifests.Count);

        foreach (var manifest in manifests)
        {
            Assert.True(
                Enum.TryParse<PluginKind>(manifest.Kind, ignoreCase: true, out var kind),
                $"{manifest.Path}: unknown kind '{manifest.Kind}'");
            Assert.True(
                PluginNaming.IdMatchesKind(manifest.Id, kind),
                $"{manifest.Path}: id '{manifest.Id}' does not start with '{PluginNaming.ExpectedPrefix(kind)}'");
        }
    }

    [Fact]
    public void Staging_table_uses_the_full_id_as_the_install_directory()
    {
        var rows = StagingRows();
        Assert.NotEmpty(rows);

        foreach (var row in rows)
        {
            var manifestPath = Path.Combine(row.ProjectDirectory, "plugin.json");
            Assert.True(File.Exists(manifestPath), $"missing manifest: {manifestPath}");
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var id = document.RootElement.GetProperty("id").GetString();
            Assert.Equal(id, row.ExpectedId);
            Assert.Equal(id, row.DirectoryName);
        }
    }

    [Fact]
    public void Shipped_plugin_ids_are_unique()
    {
        var duplicates = ShippedManifests()
            .GroupBy(manifest => manifest.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        Assert.Empty(duplicates);
    }

    [Fact]
    public void Old_ids_are_gone_from_scoped_sources()
    {
        var root = RepoRoot();
        foreach (var file in ScopedFiles(root))
        {
            var text = File.ReadAllText(file);
            foreach (var oldId in OldIds)
            {
                // New ids only differ by a suffix/extra segment; require a non-id boundary.
                var pattern = Regex.Escape(oldId) + "(?![a-z0-9._-])";
                Assert.False(
                    Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant),
                    $"{Path.GetRelativePath(root, file)} still references '{oldId}'");
            }
        }
    }

    private static List<ManifestInfo> ShippedManifests()
    {
        var root = RepoRoot();
        var manifests = new List<ManifestInfo>();
        foreach (var directory in new[] { "plugins", "samples", "src" })
        {
            var full = Path.Combine(root, directory);
            if (!Directory.Exists(full))
                continue;
            foreach (var manifest in Directory.GetFiles(full, "plugin.json", SearchOption.AllDirectories))
            {
                if (manifest.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || manifest.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                    continue;
                using var document = JsonDocument.Parse(File.ReadAllText(manifest));
                manifests.Add(new ManifestInfo(
                    manifest,
                    document.RootElement.GetProperty("id").GetString()!,
                    document.RootElement.GetProperty("kind").GetString()!));
            }
        }

        return manifests;
    }

    private static List<StagingRow> StagingRows()
    {
        var root = RepoRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "stage-tier1-plugins.sh"));
        var rows = new List<StagingRow>();
        foreach (var line in script.Split('\n'))
        {
            var match = Regex.Match(line, "^\\s*\"([^|\"]+)\\|([^|\"]+)\\|([^|\"]+)\\|([^|\"]+)\\|");
            if (!match.Success)
                continue;
            var projectDirectory = Path.GetDirectoryName(Path.Combine(root, match.Groups[2].Value))!;
            rows.Add(new StagingRow(projectDirectory, match.Groups[3].Value, match.Groups[4].Value));
        }

        return rows;
    }

    private static IEnumerable<string> ScopedFiles(string root)
    {
        foreach (var directory in new[] { "plugins", "samples", "src", "tests", "scripts", "deploy", "config" })
        {
            var full = Path.Combine(root, directory);
            if (!Directory.Exists(full))
                continue;
            foreach (var file in Directory.GetFiles(full, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith("PluginNamingTests.cs", StringComparison.Ordinal))
                    continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                    continue;
                yield return file;
            }
        }
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Natter.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private sealed record ManifestInfo(string Path, string Id, string Kind);

    private sealed record StagingRow(string ProjectDirectory, string DirectoryName, string ExpectedId);
}
