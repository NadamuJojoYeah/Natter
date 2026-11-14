using System.Globalization;

namespace Natter.Chat;

/// <summary>
/// NLMP model version and compatibility rules. The model version is independent of the
/// Satori protocol version and of any platform version.
/// </summary>
public static class Nlmp
{
    public const int Major = 0;

    public const int Minor = 1;

    public const string Version = "0.1";

    /// <summary>Parses a <c>major.minor</c> version string.</summary>
    public static bool TryParse(string? version, out int major, out int minor)
    {
        major = 0;
        minor = 0;

        if (string.IsNullOrWhiteSpace(version))
            return false;

        var parts = version.Split('.', StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return false;

        return int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out major)
               && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor)
               && major >= 0
               && minor >= 0;
    }

    /// <summary>Minor versions are compatible; a different major version is not.</summary>
    public static bool IsCompatible(string? version)
        => TryParse(version, out var major, out _) && major == Major;

    /// <summary>
    /// Validates an envelope version before deserialization. Unknown/absent versions and
    /// major mismatches are rejected; the JSON binding requires an explicit version.
    /// </summary>
    public static void EnsureCompatible(string? version)
    {
        if (!TryParse(version, out var major, out _))
        {
            throw new NlmpVersionException(
                version,
                $"NLMP envelope version '{version ?? "<missing>"}' is missing or malformed; expected 'major.minor'.");
        }

        if (major != Major)
        {
            throw new NlmpVersionException(
                version,
                $"NLMP major version '{major}' is not supported by this build (expected major {Major}).");
        }
    }
}

/// <summary>Raised when an envelope carries a missing or unsupported NLMP version.</summary>
public sealed class NlmpVersionException : Exception
{
    public NlmpVersionException(string? version, string message)
        : base(message)
        => Version = version;

    public string? Version { get; }
}

/// <summary>Raised when a model object violates a MUST from the NLMP specification.</summary>
public sealed class NlmpValidationException : Exception
{
    public NlmpValidationException(string message)
        : this(message, [message])
    {
    }

    public NlmpValidationException(string message, IReadOnlyList<string> errors)
        : base(message)
        => Errors = errors;

    public IReadOnlyList<string> Errors { get; }
}
