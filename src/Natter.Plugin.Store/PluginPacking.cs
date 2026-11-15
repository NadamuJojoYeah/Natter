using Natter.Abstractions.Plugins;
using Natter.Plugin.Abstractions;

namespace Natter.Plugin.Store;

/// <summary>One package validation problem.</summary>
public sealed record PluginPackageIssue(string Code, string Message, string? Path = null);

/// <summary>Validated package metadata.</summary>
public sealed record PluginPackageInfo(
    PluginManifest Manifest,
    string ExtractDirectory,
    string PackageHash,
    string? SignerKeyId,
    IReadOnlyList<string> Files,
    string VersionDirectoryName);

/// <summary>Package validation result.</summary>
public sealed record PluginPackageValidationResult(
    bool IsValid,
    PluginPackageInfo? Package,
    IReadOnlyList<PluginPackageIssue> Issues);

/// <summary>Limits and trust policy for package installation.</summary>
public sealed class PluginStoreOptions
{
    public required string RootDirectory { get; init; }

    public string Platform { get; init; } = "android";

    public string HostApiVersion { get; init; } = NatterPluginApi.Version;

    public long MaxPackageBytes { get; init; } = 64 * 1024 * 1024;

    public int MaxEntries { get; init; } = 1024;

    public long MaxTotalUncompressedBytes { get; init; } = 256 * 1024 * 1024;

    public long MaxEntryUncompressedBytes { get; init; } = 128 * 1024 * 1024;

    public double MaxCompressionRatio { get; init; } = 100;

    /// <summary>Debug/local development only; production packages must be signed.</summary>
    public bool AllowUnsigned { get; init; }

    /// <summary>Base64 SubjectPublicKeyInfo (ECDSA P-256) trusted publisher keys.</summary>
    public IReadOnlyList<string> TrustedPublicKeys { get; init; } = [];

    public bool AllowUpdates { get; init; } = true;

    public bool AllowNativeLibraries { get; init; }

    public Func<DateTimeOffset>? Clock { get; init; }
}

/// <summary>Thrown when package validation or installation fails.</summary>
public sealed class PluginStoreException : Exception
{
    public PluginStoreException(string message, IReadOnlyList<PluginPackageIssue> issues)
        : base(issues.Count == 0
            ? message
            : message + " " + string.Join("; ", issues.Select(issue => $"{issue.Code}: {issue.Message}")))
        => Issues = issues;

    public PluginStoreException(string message, Exception innerException)
        : base(message, innerException)
        => Issues = [];

    public IReadOnlyList<PluginPackageIssue> Issues { get; }
}

/// <summary>Existing installed version used for dependency checks.</summary>
public sealed record InstalledPluginVersion(string PluginId, string Version, bool Enabled);

/// <summary>Published package file names.</summary>
public static class PluginPackageConstants
{
    public const string ManifestFile = "plugin.json";
    public const string HashFile = "package.sha256";
    public const string SignatureFile = "package.sig";
}
