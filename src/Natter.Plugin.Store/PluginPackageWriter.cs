using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Natter.Plugin.Abstractions;

namespace Natter.Plugin.Store;

/// <summary>
/// Creates a signed .natter-plugin.zip from a published plugin directory. Intended for
/// release tooling; the publisher private key never belongs in the package or the app.
/// </summary>
public static class PluginPackageWriter
{
    public static async Task<PluginPackageInfo> CreateAsync(
        string sourceDirectory,
        string outputPackagePath,
        ECDsa publisherKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPackagePath);
        ArgumentNullException.ThrowIfNull(publisherKey);

        var source = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"Plugin source directory '{source}' was not found.");

        var manifestRead = PluginManifestReader.Read(source);
        if (!manifestRead.IsValid || manifestRead.Manifest is null)
        {
            throw new PluginStoreException(
                "Plugin manifest is invalid.",
                manifestRead.Errors.Select(error => new PluginPackageIssue("manifest-" + error.Code, error.Message, error.Path)).ToArray());
        }

        var manifest = manifestRead.Manifest;
        var output = Path.GetFullPath(outputPackagePath);
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Where(file => !string.Equals(Path.GetFullPath(file), output, StringComparison.Ordinal))
            .Where(file => !string.Equals(Path.GetFileName(file), PluginPackageConstants.HashFile, StringComparison.Ordinal))
            .Where(file => !string.Equals(Path.GetFileName(file), PluginPackageConstants.SignatureFile, StringComparison.Ordinal))
            .OrderBy(file => Path.GetRelativePath(source, file).Replace('\\', '/'), StringComparer.Ordinal)
            .ToArray();

        var hashText = new StringBuilder();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false))).ToLowerInvariant();
            hashText.Append(hash).Append("  ").Append(relative).Append('\n');
        }

        var signature = publisherKey.SignData(
            PluginPackageSignatureVerifier.BuildSignedData(manifest.Id, manifest.Version, hashText.ToString()),
            HashAlgorithmName.SHA256);

        var directory = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temp = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
                    var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
                    await using var entryStream = entry.Open();
                    await using var fileStream = File.OpenRead(file);
                    await fileStream.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
                }

                WriteText(archive, PluginPackageConstants.HashFile, hashText.ToString());
                WriteText(archive, PluginPackageConstants.SignatureFile, Convert.ToBase64String(signature));
            }

            File.Move(temp, output, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }

        var packageHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(output, cancellationToken).ConfigureAwait(false))).ToLowerInvariant();
        return new PluginPackageInfo(
            manifest,
            source,
            packageHash,
            Convert.ToHexString(SHA256.HashData(publisherKey.ExportSubjectPublicKeyInfo()))[..16].ToLowerInvariant(),
            files.Select(file => Path.GetRelativePath(source, file).Replace('\\', '/')).ToArray(),
            manifest.Version);
    }

    private static void WriteText(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: false);
        writer.Write(text);
    }
}
