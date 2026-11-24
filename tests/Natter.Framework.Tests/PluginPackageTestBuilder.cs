using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Natter.Plugin.Store;

namespace Natter.Framework.Tests;

internal sealed record TestPluginPackage(
    string PackagePath,
    string PublicKey,
    string PluginId,
    string Version,
    string ManifestJson);

internal static class PluginPackageTestBuilder
{
    public const string SampleEntryType = "Natter.Sample.Plugin.Greetings.GreetingsNpsPlugin";
    public const string DefaultEntryPath = "lib/Natter.Sample.Plugin.Greetings.dll";

    public static ECDsa CreateSigner()
        => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public static TestPluginPackage Create(
        string root,
        string pluginId = "natter.sample.port.greetings",
        string version = "0.1.0",
        string manifestJson = "",
        byte[]? entryBytes = null,
        string entryPath = DefaultEntryPath,
        ECDsa? signer = null,
        bool sign = true,
        bool tamperHash = false,
        bool omitHash = false,
        bool omitSignature = false,
        IReadOnlyDictionary<string, byte[]>? additionalFiles = null,
        Action<ZipArchive>? customizeArchive = null)
    {
        Directory.CreateDirectory(root);
        signer ??= CreateSigner();
        entryBytes ??= File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "plugins", "greetings", "Natter.Sample.Plugin.Greetings.dll"));

        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            manifestJson = $$"""
                {
                  "schemaVersion": 1,
                  "id": "{{pluginId}}",
                  "name": "Test Plugin",
                  "version": "{{version}}",
                  "kind": "feature",
                  "hostApi": ">=0.1.0 <1.0.0",
                  "entry": { "assembly": "{{entryPath}}", "type": "{{SampleEntryType}}" },
                  "permissions": ["cli.commands"],
                  "contributions": [
                    { "type": "port.adapter", "id": "greeting" },
                    { "type": "cli.command", "id": "greet" },
                    { "type": "cli.command", "id": "greetings.recent" }
                  ],
                  "platforms": ["android", "linux", "windows", "macos"]
                }
                """;
        }

        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["plugin.json"] = Encoding.UTF8.GetBytes(manifestJson),
            [entryPath] = entryBytes,
        };

        if (additionalFiles is not null)
        {
            foreach (var (name, data) in additionalFiles)
                files[name] = data;
        }

        var hashText = BuildHashText(files, entryPath, tamperHash);
        var packagePath = Path.Combine(root, $"{pluginId}-{version}-{Guid.NewGuid():N}.natter-plugin.zip");

        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            foreach (var (name, data) in files)
                WriteEntry(archive, name, data);

            if (!omitHash)
                WriteEntry(archive, PluginPackageConstants.HashFile, Encoding.UTF8.GetBytes(hashText));

            if (sign && !omitSignature)
            {
                var signature = signer.SignData(
                    PluginPackageSignatureVerifier.BuildSignedData(pluginId, version, hashText),
                    HashAlgorithmName.SHA256);
                WriteEntry(archive, PluginPackageConstants.SignatureFile, Encoding.UTF8.GetBytes(Convert.ToBase64String(signature)));
            }

            customizeArchive?.Invoke(archive);
        }

        return new TestPluginPackage(
            packagePath,
            Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()),
            pluginId,
            version,
            manifestJson);
    }

    public static string BuildManifest(
        string pluginId = "natter.sample.port.greetings",
        string version = "0.1.0",
        string entryPath = DefaultEntryPath,
        string entryType = SampleEntryType,
        string hostApi = ">=0.1.0 <1.0.0",
        string[]? platforms = null,
        string[]? permissions = null,
        string[]? contributions = null,
        string? dependsOnId = null,
        string? configSchema = null)
    {
        platforms ??= ["android", "linux", "windows", "macos"];
        permissions ??= ["cli.commands"];
        contributions ??= ["port.adapter:greeting", "cli.command:greet", "cli.command:greetings.recent"];

        var contributionJson = string.Join(",", contributions.Select(item =>
        {
            var parts = item.Split(':', 2);
            return $$"""{ "type": "{{parts[0]}}", "id": "{{parts[1]}}" }""";
        }));

        var depends = dependsOnId is null
            ? string.Empty
            : $$""", "dependsOn": [{ "id": "{{dependsOnId}}", "version": ">=0.1.0" }]""";

        var schema = configSchema is null
            ? string.Empty
            : $$""", "configSchema": "{{configSchema}}" """;

        return $$"""
            {
              "schemaVersion": 1,
              "id": "{{pluginId}}",
              "name": "Test Plugin",
              "version": "{{version}}",
              "kind": "feature",
              "hostApi": "{{hostApi}}",
              "entry": { "assembly": "{{entryPath}}", "type": "{{entryType}}" },
              "platforms": [{{string.Join(",", platforms.Select(item => $"\"{item}\""))}}],
              "permissions": [{{string.Join(",", permissions.Select(item => $"\"{item}\""))}}],
              "contributions": [{{contributionJson}}]{{depends}}{{schema}}
            }
            """;
    }

    public static void AddRawEntry(ZipArchive archive, string name, byte[] data, int externalAttributes = 0)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        entry.ExternalAttributes = externalAttributes;
        using var stream = entry.Open();
        stream.Write(data);
    }

    private static string BuildHashText(
        IReadOnlyDictionary<string, byte[]> files,
        string entryPath,
        bool tamperHash)
    {
        var builder = new StringBuilder();

        foreach (var (name, data) in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            if (tamperHash && string.Equals(name, entryPath, StringComparison.Ordinal))
                hash = new string('0', 64);

            builder.Append(hash).Append("  ").Append(name).Append('\n');
        }

        return builder.ToString();
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] data)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(data);
    }
}
