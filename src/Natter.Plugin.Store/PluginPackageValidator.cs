using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Natter.Abstractions.Plugins;
using Natter.Plugin.Abstractions;

namespace Natter.Plugin.Store;

/// <summary>
/// Validates a <c>.natter-plugin.zip</c> package: archive safety, manifest, host API,
/// platform, managed-only binaries, SHA-256 manifest, publisher signature and dependencies.
/// </summary>
public sealed class PluginPackageValidator
{
    private readonly PluginStoreOptions _options;
    private readonly PluginPackageSignatureVerifier _signatureVerifier;

    public PluginPackageValidator(PluginStoreOptions options, PluginPackageSignatureVerifier? signatureVerifier = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _signatureVerifier = signatureVerifier ?? new PluginPackageSignatureVerifier();
    }

    public async Task<PluginPackageValidationResult> ValidateAsync(
        string packagePath,
        string extractDirectory,
        IReadOnlyCollection<InstalledPluginVersion>? installedPlugins = null,
        CancellationToken cancellationToken = default)
    {
        var issues = new List<PluginPackageIssue>();

        if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
        {
            return Invalid(new PluginPackageIssue("package-missing", "Package file was not found.", packagePath));
        }

        var packageInfo = new FileInfo(packagePath);
        if (packageInfo.Length <= 0)
            return Invalid(new PluginPackageIssue("package-empty", "Package file is empty."));

        if (packageInfo.Length > _options.MaxPackageBytes)
        {
            return Invalid(new PluginPackageIssue(
                "package-too-large",
                $"Package is {packageInfo.Length} bytes, limit is {_options.MaxPackageBytes}."));
        }

        Directory.CreateDirectory(extractDirectory);
        var extractRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(extractDirectory));

        using (var archive = ZipFile.OpenRead(packagePath))
        {
            ValidateEntries(archive, issues);

            if (issues.Count == 0)
            {
                try
                {
                    await ExtractAsync(archive, extractRoot, issues, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    issues.Add(new PluginPackageIssue("extract-failed", ex.Message));
                }
            }
        }

        if (issues.Count > 0)
            return new PluginPackageValidationResult(false, null, issues);

        var manifestRead = PluginManifestReader.Read(extractRoot);
        if (!manifestRead.IsNps)
        {
            issues.Add(new PluginPackageIssue("manifest-not-nps", "Package plugin.json must declare schemaVersion: 1."));
            return new PluginPackageValidationResult(false, null, issues);
        }

        foreach (var error in manifestRead.Errors)
            issues.Add(new PluginPackageIssue("manifest-" + error.Code, error.Message, error.Path));

        if (!manifestRead.IsValid || manifestRead.Manifest is null)
            return new PluginPackageValidationResult(false, null, issues);

        var manifest = manifestRead.Manifest;

        ValidateHostApiAndPlatform(manifest, issues);
        ValidateEntryAssembly(extractRoot, manifest, issues);
        ValidateConfigSchema(extractRoot, manifest, issues);
        ValidateDependencies(manifest, installedPlugins ?? [], issues);

        string? signerKeyId = null;
        var hashResult = ValidateHashes(extractRoot, manifest, issues);
        if (hashResult is not null)
            ValidateSignature(extractRoot, manifest, hashResult.CanonicalHashText, issues, out signerKeyId);

        if (issues.Count > 0)
            return new PluginPackageValidationResult(false, null, issues);

        var files = Directory.EnumerateFiles(extractRoot, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(extractRoot, file).Replace('\\', '/'))
            .Where(file => ValidateSensitiveFileName(file, issues))
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToArray();

        var packageHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath))).ToLowerInvariant();

        return new PluginPackageValidationResult(
            true,
            new PluginPackageInfo(
                manifest,
                extractRoot,
                packageHash,
                signerKeyId,
                files,
                manifest.Version),
            []);
    }

    private PluginPackageValidationResult Invalid(PluginPackageIssue issue)
        => new(false, null, [issue]);

    private void ValidateEntries(ZipArchive archive, List<PluginPackageIssue> issues)
    {
        if (archive.Entries.Count > _options.MaxEntries)
        {
            issues.Add(new PluginPackageIssue(
                "too-many-entries",
                $"Package has {archive.Entries.Count} entries, limit is {_options.MaxEntries}."));
            return;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalUncompressed = 0;
        long totalCompressed = 0;

        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (!IsSafeEntryPath(name, out var pathError))
            {
                issues.Add(new PluginPackageIssue("unsafe-path", pathError!, entry.FullName));
                continue;
            }

            if (name.EndsWith('/'))
                continue;

            if (!names.Add(name))
            {
                issues.Add(new PluginPackageIssue("duplicate-entry", $"Duplicate entry '{name}'.", name));
                continue;
            }

            if (IsSymbolicLink(entry))
            {
                issues.Add(new PluginPackageIssue("symlink", $"Symbolic link '{name}' is not allowed.", name));
                continue;
            }

            if (entry.Length > _options.MaxEntryUncompressedBytes)
            {
                issues.Add(new PluginPackageIssue(
                    "entry-too-large",
                    $"Entry '{name}' is {entry.Length} bytes, limit is {_options.MaxEntryUncompressedBytes}.",
                    name));
                continue;
            }

            totalUncompressed += entry.Length;
            totalCompressed += Math.Max(1, entry.CompressedLength);

            var extension = Path.GetExtension(name);
            if (!_options.AllowNativeLibraries
                && extension is ".so" or ".dylib" or ".a" or ".exe")
            {
                issues.Add(new PluginPackageIssue("native-library", $"Native file '{name}' is not allowed.", name));
            }
        }

        if (totalUncompressed > _options.MaxTotalUncompressedBytes)
        {
            issues.Add(new PluginPackageIssue(
                "package-uncompressed-too-large",
                $"Package expands to {totalUncompressed} bytes, limit is {_options.MaxTotalUncompressedBytes}."));
        }

        if (totalUncompressed > 0 && totalUncompressed > totalCompressed * _options.MaxCompressionRatio)
        {
            issues.Add(new PluginPackageIssue(
                "compression-ratio",
                $"Package compression ratio exceeds {_options.MaxCompressionRatio}."));
        }
    }

    private static async Task ExtractAsync(
        ZipArchive archive,
        string extractRoot,
        List<PluginPackageIssue> issues,
        CancellationToken cancellationToken)
    {
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = entry.FullName.Replace('\\', '/');
            if (!IsSafeEntryPath(name, out _))
                continue;

            var destination = Path.GetFullPath(Path.Combine(extractRoot, name));
            var prefix = extractRoot + Path.DirectorySeparatorChar;
            if (!destination.StartsWith(prefix, StringComparison.Ordinal))
            {
                issues.Add(new PluginPackageIssue("unsafe-path", $"Entry '{name}' escapes the package.", name));
                continue;
            }

            if (name.EndsWith('/'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            await using var input = entry.Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsSafeEntryPath(string name, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(name) || name.Contains('\\', StringComparison.Ordinal))
        {
            error = "Entry path must be a non-empty forward-slash path.";
            return false;
        }

        if (name.StartsWith('/') || name.StartsWith("./", StringComparison.Ordinal))
        {
            error = "Entry path must be relative.";
            return false;
        }

        var segments = name.TrimEnd('/').Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            error = "Entry path contains an empty, '.' or '..' segment.";
            return false;
        }

        return true;
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        var unixMode = (entry.ExternalAttributes >> 16) & 0xFFFF;
        return (unixMode & 0xF000) == 0xA000;
    }

    private void ValidateHostApiAndPlatform(PluginManifest manifest, List<PluginPackageIssue> issues)
    {
        if (!VersionRange.TryParse(manifest.HostApi, out var range)
            || !SemanticVersion.TryParse(_options.HostApiVersion, out var hostVersion)
            || !range!.IsSatisfiedBy(hostVersion))
        {
            issues.Add(new PluginPackageIssue(
                "host-api",
                $"Plugin hostApi '{manifest.HostApi}' is incompatible with host '{_options.HostApiVersion}'."));
        }

        if (manifest.Platforms is { Count: > 0 }
            && !manifest.Platforms.Contains(_options.Platform, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new PluginPackageIssue(
                "platform",
                $"Plugin does not support platform '{_options.Platform}'."));
        }
    }

    private void ValidateEntryAssembly(string extractRoot, PluginManifest manifest, List<PluginPackageIssue> issues)
    {
        var assembly = manifest.Entry?.Assembly;
        if (string.IsNullOrWhiteSpace(assembly))
        {
            issues.Add(new PluginPackageIssue("entry-missing", "Entry assembly is missing."));
            return;
        }

        var path = Path.GetFullPath(Path.Combine(extractRoot, assembly));
        if (!File.Exists(path))
        {
            issues.Add(new PluginPackageIssue("entry-missing", $"Entry assembly '{assembly}' is missing.", assembly));
            return;
        }

        ValidateManagedBinary(path, assembly, issues);
    }

    private void ValidateManagedBinary(string path, string relativePath, List<PluginPackageIssue> issues)
    {
        if (!_options.AllowNativeLibraries)
        {
            using var stream = File.OpenRead(path);
            if (!ManagedAssemblyInspector.IsManagedAssembly(stream))
            {
                issues.Add(new PluginPackageIssue(
                    "native-library",
                    $"File '{relativePath}' is not a managed assembly.",
                    relativePath));
            }
        }
    }

    private static void ValidateConfigSchema(string extractRoot, PluginManifest manifest, List<PluginPackageIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(manifest.ConfigSchema))
            return;

        var path = Path.GetFullPath(Path.Combine(extractRoot, manifest.ConfigSchema));
        if (!File.Exists(path))
        {
            issues.Add(new PluginPackageIssue("config-schema-missing", "Declared config schema is missing.", manifest.ConfigSchema));
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
                issues.Add(new PluginPackageIssue("config-schema-invalid", "Config schema must be a JSON object.", manifest.ConfigSchema));
        }
        catch (JsonException ex)
        {
            issues.Add(new PluginPackageIssue("config-schema-invalid", ex.Message, manifest.ConfigSchema));
        }
    }

    private static void ValidateDependencies(
        PluginManifest manifest,
        IReadOnlyCollection<InstalledPluginVersion> installedPlugins,
        List<PluginPackageIssue> issues)
    {
        var installed = installedPlugins
            .GroupBy(plugin => plugin.PluginId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

        foreach (var dependency in manifest.DependsOn ?? [])
        {
            if (string.Equals(dependency.Id, manifest.Id, StringComparison.Ordinal))
            {
                if (!VersionRange.TryParse(dependency.Version, out var selfRange)
                    || !SemanticVersion.TryParse(manifest.Version, out var selfVersion)
                    || !selfRange!.IsSatisfiedBy(selfVersion))
                {
                    issues.Add(new PluginPackageIssue(
                        "dependency-self",
                        $"Plugin depends on itself with incompatible range '{dependency.Version}'."));
                }

                continue;
            }

            if (!installed.TryGetValue(dependency.Id, out var target))
            {
                issues.Add(new PluginPackageIssue(
                    "dependency-missing",
                    $"Required plugin '{dependency.Id}' is not installed.",
                    dependency.Id));
                continue;
            }

            if (!target.Enabled)
            {
                issues.Add(new PluginPackageIssue(
                    "dependency-disabled",
                    $"Required plugin '{dependency.Id}' is installed but disabled.",
                    dependency.Id));
            }

            if (!VersionRange.TryParse(dependency.Version, out var range)
                || !SemanticVersion.TryParse(target.Version, out var targetVersion)
                || !range!.IsSatisfiedBy(targetVersion))
            {
                issues.Add(new PluginPackageIssue(
                    "dependency-version",
                    $"Installed '{dependency.Id}' version '{target.Version}' does not satisfy '{dependency.Version}'.",
                    dependency.Id));
            }
        }
    }

    private HashValidation? ValidateHashes(string extractRoot, PluginManifest manifest, List<PluginPackageIssue> issues)
    {
        var hashPath = Path.Combine(extractRoot, PluginPackageConstants.HashFile);
        if (!File.Exists(hashPath))
        {
            issues.Add(new PluginPackageIssue("hash-file-missing", "Package SHA-256 manifest is missing."));
            return null;
        }

        var declared = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = File.ReadAllLines(hashPath);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || parts[0].Length != 64 || !IsHex(parts[0]))
            {
                issues.Add(new PluginPackageIssue("hash-format", $"Invalid hash line '{rawLine}'.", PluginPackageConstants.HashFile));
                continue;
            }

            var relative = parts[1].Replace('\\', '/');
            if (!IsSafeEntryPath(relative, out _))
            {
                issues.Add(new PluginPackageIssue("hash-path", $"Unsafe hash path '{relative}'.", relative));
                continue;
            }

            if (!declared.TryAdd(relative, parts[0].ToLowerInvariant()))
                issues.Add(new PluginPackageIssue("hash-duplicate", $"Duplicate hash entry '{relative}'.", relative));
        }

        var files = Directory.EnumerateFiles(extractRoot, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(extractRoot, file).Replace('\\', '/'))
            .Where(file => ValidateSensitiveFileName(file, issues))
            .Where(file => file is not PluginPackageConstants.HashFile and not PluginPackageConstants.SignatureFile)
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToArray();

        var canonical = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var path = Path.Combine(extractRoot, file);
            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            seen.Add(file);

            if (!declared.TryGetValue(file, out var expected))
            {
                issues.Add(new PluginPackageIssue("unhashed-file", $"File '{file}' is not in package.sha256.", file));
                continue;
            }

            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                issues.Add(new PluginPackageIssue("hash-mismatch", $"SHA-256 mismatch for '{file}'.", file));
                continue;
            }

            canonical.Append(actual).Append("  ").Append(file).Append('\n');
        }

        foreach (var missing in declared.Keys.Where(file => !seen.Contains(file)))
            issues.Add(new PluginPackageIssue("hash-missing-file", $"Hashed file '{missing}' is missing.", missing));

        if (!declared.ContainsKey(PluginPackageConstants.ManifestFile))
            issues.Add(new PluginPackageIssue("hash-manifest", "plugin.json must be hashed.", PluginPackageConstants.ManifestFile));

        if (manifest.Entry?.Assembly is { } entry && !declared.ContainsKey(entry.Replace('\\', '/')))
            issues.Add(new PluginPackageIssue("hash-entry", "Entry assembly must be hashed.", entry));

        if (!_options.AllowNativeLibraries)
        {
            foreach (var dll in files.Where(file => file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                ValidateManagedBinary(Path.Combine(extractRoot, dll), dll, issues);
        }

        return new HashValidation(canonical.ToString());
    }

    private void ValidateSignature(
        string extractRoot,
        PluginManifest manifest,
        string canonicalHashText,
        List<PluginPackageIssue> issues,
        out string? signerKeyId)
    {
        signerKeyId = null;
        var signaturePath = Path.Combine(extractRoot, PluginPackageConstants.SignatureFile);

        if (!File.Exists(signaturePath))
        {
            if (!_options.AllowUnsigned)
                issues.Add(new PluginPackageIssue("signature-missing", "Package is not signed."));
            return;
        }

        if (_options.TrustedPublicKeys.Count == 0)
        {
            issues.Add(new PluginPackageIssue("signature-untrusted", "No trusted publisher keys are configured."));
            return;
        }

        var signature = File.ReadAllBytes(signaturePath);
        var info = new PluginPackageInfo(manifest, extractRoot, string.Empty, null, [], manifest.Version);

        if (!_signatureVerifier.TryVerify(signature, canonicalHashText, info, _options.TrustedPublicKeys, out signerKeyId))
            issues.Add(new PluginPackageIssue("signature-invalid", "Publisher signature verification failed."));
    }

    private static bool ValidateSensitiveFileName(string relativePath, List<PluginPackageIssue> issues)
    {
        string[] sensitiveExtensions = [".pfx", ".p12", ".key", ".keystore", ".jks", ".pem"];
        string[] sensitiveNames = [".env", "token", "tokens", "credentials", "credentials.json", "secrets.json"];

        var name = Path.GetFileName(relativePath);
        var extension = Path.GetExtension(relativePath);

        if (sensitiveExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            || sensitiveNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new PluginPackageIssue("sensitive-file", $"Package contains sensitive file '{relativePath}'.", relativePath));
            return false;
        }

        return true;
    }

    private static bool IsHex(string value)
        => value.All(character => char.IsAsciiHexDigit(character));

    private sealed record HashValidation(string CanonicalHashText);
}
