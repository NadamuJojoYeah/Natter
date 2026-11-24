using System.Security.Cryptography;

namespace Natter.Plugin.Store;

/// <summary>Result of re-checking an installed package against its <c>package.sha256</c> manifest.</summary>
public enum InstalledPluginIntegrity
{
    /// <summary>Every declared file matched and no undeclared file was added.</summary>
    Verified = 0,

    /// <summary>No hash manifest / unreadable: explicitly <em>not</em> verified.</summary>
    Unverified = 1,

    /// <summary>Hash mismatch, missing hashed file or undeclared file: treated as tampering.</summary>
    Tampered = 2,
}

/// <summary>
/// One activation candidate from the plugin store. Trust and integrity travel with the directory so
/// the host policy can decide per plugin instead of stamping every file plugin as "local".
/// </summary>
public sealed record InstalledPluginCandidate(
    string PluginId,
    string Version,
    string Directory,
    string Trust,
    string? SignerKeyId,
    InstalledPluginIntegrity Integrity,
    string IntegrityCode,
    string? IntegrityDetail,
    bool HasSignature,
    IReadOnlyList<string> Warnings)
{
    public bool Signed => Integrity == InstalledPluginIntegrity.Verified
                          && string.Equals(Trust, "signed", StringComparison.Ordinal)
                          && !string.IsNullOrEmpty(SignerKeyId);
}

public static class InstalledPluginIntegrityCodes
{
    public const string Verified = "plugin.integrity-verified";
    public const string ManifestMissing = "plugin.integrity-manifest-missing";
    public const string ManifestInvalid = "plugin.integrity-manifest-invalid";
    public const string Mismatch = "plugin.integrity-mismatch";
    public const string Unreadable = "plugin.integrity-unreadable";

    public static InstalledPluginCandidate Candidate(
        string pluginId,
        string version,
        string directory,
        string trust,
        string? signerKeyId)
    {
        var (integrity, code, detail) = Verify(directory);
        var warnings = new List<string>();

        if (integrity == InstalledPluginIntegrity.Unverified)
            warnings.Add($"{code}: {detail}");
        if (integrity == InstalledPluginIntegrity.Tampered)
            warnings.Add($"{code}: {detail}");

        return new InstalledPluginCandidate(
            pluginId,
            version,
            directory,
            trust,
            signerKeyId,
            integrity,
            code,
            detail,
            File.Exists(Path.Combine(directory, PluginPackageConstants.SignatureFile)),
            warnings);
    }

    /// <summary>
    /// Re-computes the SHA-256 manifest shipped inside the installed package. This is the only
    /// tamper check that still holds after install: the signature is verified once at install time,
    /// while the hash manifest can be re-checked every start.
    /// </summary>
    public static (InstalledPluginIntegrity Integrity, string Code, string? Detail) Verify(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var hashPath = Path.Combine(directory, PluginPackageConstants.HashFile);
        if (!File.Exists(hashPath))
            return (InstalledPluginIntegrity.Unverified, ManifestMissing, "no package.sha256 in the installed package");

        var declared = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var rawLine in File.ReadAllLines(hashPath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                    continue;

                var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || parts[0].Length != 64 || !IsHex(parts[0]))
                    return (InstalledPluginIntegrity.Unverified, ManifestInvalid, "package.sha256 contains an invalid line");

                var relative = parts[1].Replace('\\', '/');
                if (!declared.TryAdd(relative, parts[0].ToLowerInvariant()))
                    return (InstalledPluginIntegrity.Unverified, ManifestInvalid, $"duplicate hash entry '{relative}'");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            return (InstalledPluginIntegrity.Unverified, Unreadable, $"package.sha256 could not be read ({exception.GetType().Name})");
        }

        if (declared.Count == 0)
            return (InstalledPluginIntegrity.Unverified, ManifestInvalid, "package.sha256 is empty");

        try
        {
            var mismatched = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
                if (relative is PluginPackageConstants.HashFile or PluginPackageConstants.SignatureFile)
                    continue;

                seen.Add(relative);

                if (!declared.TryGetValue(relative, out var expected))
                {
                    mismatched.Add($"undeclared:{relative}");
                    continue;
                }

                var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
                if (!string.Equals(expected, actual, StringComparison.Ordinal))
                    mismatched.Add($"changed:{relative}");
            }

            foreach (var missing in declared.Keys.Where(file => !seen.Contains(file)))
                mismatched.Add($"missing:{missing}");

            if (mismatched.Count > 0)
                return (InstalledPluginIntegrity.Tampered, Mismatch, Summarize(mismatched));

            return (InstalledPluginIntegrity.Verified, Verified, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (InstalledPluginIntegrity.Unverified, Unreadable, $"installed package could not be read ({exception.GetType().Name})");
        }
    }

    private static string Summarize(IReadOnlyList<string> problems)
        => problems.Count <= 3
            ? string.Join("; ", problems)
            : string.Join("; ", problems.Take(3)) + $"; +{problems.Count - 3} more";

    private static bool IsHex(string value)
        => value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
}
