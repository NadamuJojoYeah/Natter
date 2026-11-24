using Natter.Abstractions.Plugins;
using Natter.Infrastructure.Plugins.Nps;
using Natter.Plugin.Abstractions;

namespace Natter.Infrastructure.Composition;

/// <summary>How much of an installed package the host could actually verify at startup.</summary>
public enum PluginSignatureState
{
    /// <summary>No verified signature (unsigned package, or the signature could not be re-checked).</summary>
    Unverified = 0,

    /// <summary>Signed package whose bytes still match the hash manifest.</summary>
    Verified = 1,

    /// <summary>Hash manifest mismatch or undeclared files: tampering detected.</summary>
    Tampered = 2,
}

/// <summary>
/// What the loader knows about one directory. A plain directory (no store record) can only report
/// <see cref="PluginSignatureState.Unverified"/> — it must never be silently upgraded to signed.
/// </summary>
public sealed record PluginTrustRegistration(
    string Directory,
    PluginSignatureState Signature = PluginSignatureState.Unverified,
    string? SignerKeyId = null,
    string? PluginId = null,
    string? Detail = null);

/// <summary>Host policy for trust at activation time (S1-12). Defaults keep existing deployments working.</summary>
public sealed class PluginTrustOptions
{
    /// <summary>Reject every package without a verified signature. Default false = current behaviour.</summary>
    public bool RequireSignedPlugins { get; set; }

    /// <summary>
    /// Allow packages whose integrity could not be verified (no <c>package.sha256</c>, e.g. plain
    /// directories and embedded samples). They are loaded with an explicit "unverified" warning.
    /// </summary>
    public bool AllowUnverifiedIntegrity { get; set; } = true;
}

public static class PluginTrustCodes
{
    public const string UnsignedRejected = "plugin.unsigned-rejected";
    public const string SignatureRequired = "plugin.signature-required";
    public const string Tampered = "plugin.integrity-mismatch";
    public const string IntegrityUnverified = "plugin.integrity-unverified";
}

/// <summary>Outcome of the trust policy for one candidate.</summary>
public sealed record PluginTrustDecision(
    bool Allowed,
    PluginTrustLevel Trust,
    string Code,
    string Message,
    bool SignatureVerified,
    string? SignerKeyId,
    IReadOnlyList<string> Warnings)
{
    public static PluginTrustDecision Reject(string code, string message, PluginSignatureState signature, string? signerKeyId)
        => new(
            false,
            PluginTrustLevel.Untrusted,
            code,
            message,
            signature == PluginSignatureState.Verified,
            signerKeyId,
            []);
}

/// <summary>
/// Activation-time trust policy. It turns the installer's verdict (<c>installed.json</c> trust +
/// <c>SignerKeyId</c>) and the startup integrity check into a <see cref="PluginTrustLevel"/>, so
/// <c>FilePluginTrustLevel</c> and <c>RequireSignedPlugins</c> actually decide instead of every file
/// plugin being stamped "local".
/// </summary>
public static class PluginTrustPolicy
{
    public static PluginTrustDecision Evaluate(
        NpsPluginOptions options,
        PluginTrustOptions trustOptions,
        PluginTrustRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(trustOptions);
        ArgumentNullException.ThrowIfNull(registration);

        var baseline = options.FilePluginTrustLevel;
        var warnings = new List<string>();

        // 1. Tampering always wins, whatever the trust configuration says.
        if (registration.Signature == PluginSignatureState.Tampered)
        {
            return PluginTrustDecision.Reject(
                PluginTrustCodes.Tampered,
                $"Plugin package integrity check failed ({registration.Detail ?? "package.sha256 mismatch"}); "
                + "the installed files no longer match the signed hash manifest.",
                registration.Signature,
                registration.SignerKeyId);
        }

        var integrityVerified = registration.Signature == PluginSignatureState.Verified;

        // 2. Verified signature: trusted as signed, never downgraded to the local baseline.
        if (integrityVerified)
        {
            return new PluginTrustDecision(
                true,
                PluginTrustLevel.Signed,
                "plugin.signed",
                "Signature verified; the package is loaded with signed trust.",
                true,
                registration.SignerKeyId,
                warnings);
        }

        // 3. Unverified signature: the policy decides whether it may still load.
        var reason = registration.Detail ?? "no verified signature is available for this directory";

        if (trustOptions.RequireSignedPlugins)
        {
            return PluginTrustDecision.Reject(
                PluginTrustCodes.UnsignedRejected,
                $"RequireSignedPlugins is enabled and the package has no verified signature ({reason}).",
                registration.Signature,
                registration.SignerKeyId);
        }

        if (baseline == PluginTrustLevel.Signed)
        {
            return PluginTrustDecision.Reject(
                PluginTrustCodes.SignatureRequired,
                $"FilePluginTrustLevel='Signed' requires a verified signature ({reason}).",
                registration.Signature,
                registration.SignerKeyId);
        }

        if (registration.Signature == PluginSignatureState.Unverified && !trustOptions.AllowUnverifiedIntegrity)
        {
            return PluginTrustDecision.Reject(
                PluginTrustCodes.IntegrityUnverified,
                $"Package integrity could not be verified and AllowUnverifiedIntegrity is disabled ({reason}).",
                registration.Signature,
                registration.SignerKeyId);
        }

        // Explicitly record the "unverified" state instead of pretending the package is trusted.
        warnings.Add($"{PluginTrustCodes.IntegrityUnverified}: {reason}; loaded as '{baseline}' per FilePluginTrustLevel.");

        return new PluginTrustDecision(
            true,
            baseline,
            PluginTrustCodes.IntegrityUnverified,
            $"Loaded without a verified signature as '{baseline}' trust.",
            false,
            registration.SignerKeyId,
            warnings);
    }

    /// <summary>Descriptor-level label for <c>plugins list</c>/doctor (never a secret).</summary>
    public static string TrustLabel(PluginTrustDecision decision)
        => decision.SignatureVerified
            ? "signed"
            : decision.Trust switch
            {
                PluginTrustLevel.Signed => "signed",
                PluginTrustLevel.Bundled => "bundled",
                PluginTrustLevel.Local => "local(unverified)",
                _ => "untrusted",
            };
}
