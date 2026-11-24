using System.Security.Cryptography;
using System.Text;

namespace Natter.Plugin.Store;

/// <summary>Verifies the publisher ECDSA P-256 signature over the canonical package content.</summary>
public sealed class PluginPackageSignatureVerifier
{
    public const string Algorithm = "ECDSA-P256-SHA256";

    /// <summary>
    /// Canonical text format version. v2 uses length prefixes so a file name can never shift the
    /// payload boundaries; v1 relied on "hash + two spaces + name + newline" (S2-C7).
    /// </summary>
    public const int CanonicalFormatVersion = 2;

    public const string CanonicalV2Header = "nps-hash-v2";

    /// <summary>Builds the unambiguous canonical hash payload used for signing.</summary>
    public static string BuildCanonicalText(IReadOnlyList<(string Path, string Hash)> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var builder = new StringBuilder();
        builder.Append(CanonicalV2Header).Append('\n');
        builder.Append(files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');

        foreach (var (path, hash) in files)
        {
            var encodedPath = Uri.EscapeDataString(path.Replace('\\', '/'));
            builder.Append(encodedPath.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(':')
                .Append(encodedPath)
                .Append('\n')
                .Append(hash)
                .Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Legacy (v1) canonical text: <c>hash + "  " + path + "\n"</c>. Kept only to detect and
    /// explicitly reject packages signed with the ambiguous format.
    /// </summary>
    public static string BuildLegacyCanonicalText(IReadOnlyList<(string Path, string Hash)> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var builder = new StringBuilder();
        foreach (var (path, hash) in files)
            builder.Append(hash).Append("  ").Append(path).Append('\n');

        return builder.ToString();
    }

    /// <summary>Constant-time comparison for hex hashes (S2-C6).</summary>
    public static bool HashesMatch(string? expected, string? actual)
    {
        if (expected is null || actual is null)
            return false;

        var left = Encoding.UTF8.GetBytes(expected.ToLowerInvariant());
        var right = Encoding.UTF8.GetBytes(actual.ToLowerInvariant());
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    public bool TryVerify(
        byte[] signature,
        string canonicalHashText,
        PluginPackageInfo package,
        IReadOnlyList<string> trustedPublicKeys,
        out string? signerKeyId)
    {
        signerKeyId = null;

        if (signature.Length == 0 || string.IsNullOrEmpty(canonicalHashText))
            return false;

        var signedData = BuildSignedData(package.Manifest.Id, package.Manifest.Version, canonicalHashText);
        var normalizedSignature = NormalizeSignature(signature);

        foreach (var keyText in trustedPublicKeys)
        {
            if (string.IsNullOrWhiteSpace(keyText))
                continue;

            byte[] keyBytes;
            try
            {
                keyBytes = Convert.FromBase64String(keyText.Trim());
            }
            catch (FormatException)
            {
                continue;
            }

            using var ecdsa = ECDsa.Create();
            try
            {
                ecdsa.ImportSubjectPublicKeyInfo(keyBytes, out _);
            }
            catch (CryptographicException)
            {
                continue;
            }

            if (!ecdsa.VerifyData(signedData, normalizedSignature, HashAlgorithmName.SHA256))
                continue;

            signerKeyId = Convert.ToHexString(SHA256.HashData(keyBytes))[..16].ToLowerInvariant();
            return true;
        }

        return false;
    }

    public static byte[] BuildSignedData(string pluginId, string version, string canonicalHashText)
        => Encoding.UTF8.GetBytes($"{pluginId}\n{version}\n{canonicalHashText}");

    private static byte[] NormalizeSignature(byte[] signature)
    {
        // Signatures may be stored as raw bytes or as base64 text (optionally newline-terminated).
        try
        {
            var text = Encoding.UTF8.GetString(signature).Trim();
            if (text.Length > 0 && text.All(character => char.IsAsciiLetterOrDigit(character) || character is '+' or '/' or '='))
                return Convert.FromBase64String(text);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
        }

        return signature;
    }
}
