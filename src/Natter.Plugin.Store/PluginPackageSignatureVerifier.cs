using System.Security.Cryptography;
using System.Text;

namespace Natter.Plugin.Store;

/// <summary>Verifies the publisher ECDSA P-256 signature over the canonical package content.</summary>
public sealed class PluginPackageSignatureVerifier
{
    public const string Algorithm = "ECDSA-P256-SHA256";

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
