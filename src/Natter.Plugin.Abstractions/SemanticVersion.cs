using System.Globalization;
using System.Text.RegularExpressions;

namespace Natter.Plugin.Abstractions;

/// <summary>SemVer 2.0 version with precedence comparison (build metadata is ignored).</summary>
public sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private static readonly Regex Pattern = new(
        @"^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<pre>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+(?<build>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    private SemanticVersion(int major, int minor, int patch, string? prerelease, string? build)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
        Build = build;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public string? Prerelease { get; }

    public string? Build { get; }

    public bool IsPrerelease => !string.IsNullOrEmpty(Prerelease);

    public static bool TryParse(string? value, out SemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var match = Pattern.Match(value.Trim());
        if (!match.Success)
            return false;

        if (!int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        var prerelease = match.Groups["pre"].Success ? match.Groups["pre"].Value : null;
        if (prerelease is not null && prerelease.Split('.').Any(IsInvalidNumericIdentifier))
            return false;

        var build = match.Groups["build"].Success ? match.Groups["build"].Value : null;
        version = new SemanticVersion(major, minor, patch, prerelease, build);
        return true;
    }

    public static SemanticVersion Parse(string value)
        => TryParse(value, out var version)
            ? version!
            : throw new FormatException($"'{value}' is not a valid SemVer 2.0 version.");

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
            return 1;

        var core = Major.CompareTo(other.Major);
        if (core != 0) return core;

        core = Minor.CompareTo(other.Minor);
        if (core != 0) return core;

        core = Patch.CompareTo(other.Patch);
        if (core != 0) return core;

        if (Prerelease is null && other.Prerelease is null) return 0;
        if (Prerelease is null) return 1;
        if (other.Prerelease is null) return -1;

        var left = Prerelease.Split('.');
        var right = other.Prerelease.Split('.');
        var length = Math.Min(left.Length, right.Length);

        for (var index = 0; index < length; index++)
        {
            var comparison = CompareIdentifier(left[index], right[index]);
            if (comparison != 0)
                return comparison;
        }

        return left.Length.CompareTo(right.Length);
    }

    public bool Equals(SemanticVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(Major, Minor, Patch, Prerelease ?? string.Empty);

    public override string ToString()
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
        if (!string.IsNullOrEmpty(Prerelease))
            text += "-" + Prerelease;
        if (!string.IsNullOrEmpty(Build))
            text += "+" + Build;
        return text;
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = IsNumeric(left);
        var rightNumeric = IsNumeric(right);

        if (leftNumeric && rightNumeric)
        {
            var leftTrimmed = left.TrimStart('0');
            var rightTrimmed = right.TrimStart('0');
            leftTrimmed = leftTrimmed.Length == 0 ? "0" : leftTrimmed;
            rightTrimmed = rightTrimmed.Length == 0 ? "0" : rightTrimmed;

            var lengthComparison = leftTrimmed.Length.CompareTo(rightTrimmed.Length);
            return lengthComparison != 0
                ? lengthComparison
                : string.CompareOrdinal(leftTrimmed, rightTrimmed);
        }

        if (leftNumeric) return -1;
        if (rightNumeric) return 1;

        return string.CompareOrdinal(left, right);
    }

    private static bool IsNumeric(string value) => value.Length > 0 && value.All(char.IsAsciiDigit);

    private static bool IsInvalidNumericIdentifier(string value)
        => value.Length > 1 && value[0] == '0' && value.All(char.IsAsciiDigit);
}
