using System.Globalization;

namespace Phantom.Workspaces.Install;

/// <summary>
/// A minimal semantic version (major.minor.patch with an optional pre-release label) used to
/// compare release tags against the running version. A leading <c>v</c>/<c>V</c> and any
/// <c>+build</c> metadata are ignored. A version carrying a pre-release label sorts below the
/// same core version without one.
/// </summary>
public readonly struct SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private SemanticVersion(int major, int minor, int patch, string? prerelease)
    {
        this.Major = major;
        this.Minor = minor;
        this.Patch = patch;
        this.Prerelease = prerelease;
    }

    /// <summary>The major component.</summary>
    public int Major { get; }

    /// <summary>The minor component.</summary>
    public int Minor { get; }

    /// <summary>The patch component.</summary>
    public int Patch { get; }

    /// <summary>The pre-release label (without the leading hyphen), or <c>null</c> for a release.</summary>
    public string? Prerelease { get; }

    /// <summary>Whether this version carries a pre-release label.</summary>
    public bool IsPrerelease => this.Prerelease is not null;

    /// <summary>
    /// Parses a version such as <c>v1.2.3</c>, <c>1.2</c>, or <c>1.2.3-beta.1+build</c>. Returns
    /// <c>false</c> for unparseable input rather than throwing.
    /// </summary>
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.Trim();
        if (span.Length > 0 && (span[0] == 'v' || span[0] == 'V'))
        {
            span = span[1..];
        }

        var plusIndex = span.IndexOf('+');
        if (plusIndex >= 0)
        {
            span = span[..plusIndex];
        }

        string? prerelease = null;
        var dashIndex = span.IndexOf('-');
        if (dashIndex >= 0)
        {
            prerelease = span[(dashIndex + 1)..];
            span = span[..dashIndex];
            if (prerelease.Length == 0)
            {
                return false;
            }
        }

        var parts = span.Split('.');
        if (parts.Length is < 1 or > 3)
        {
            return false;
        }

        var components = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                || value < 0)
            {
                return false;
            }

            components[i] = value;
        }

        version = new SemanticVersion(components[0], components[1], components[2], prerelease);
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(SemanticVersion other)
    {
        var coreComparison = this.Major.CompareTo(other.Major);
        if (coreComparison != 0)
        {
            return coreComparison;
        }

        coreComparison = this.Minor.CompareTo(other.Minor);
        if (coreComparison != 0)
        {
            return coreComparison;
        }

        coreComparison = this.Patch.CompareTo(other.Patch);
        if (coreComparison != 0)
        {
            return coreComparison;
        }

        if (this.Prerelease is null && other.Prerelease is null)
        {
            return 0;
        }

        if (this.Prerelease is null)
        {
            return 1;
        }

        if (other.Prerelease is null)
        {
            return -1;
        }

        return string.CompareOrdinal(this.Prerelease, other.Prerelease);
    }

    /// <inheritdoc />
    public bool Equals(SemanticVersion other) => this.CompareTo(other) == 0;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SemanticVersion other && this.Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(this.Major, this.Minor, this.Patch, this.Prerelease);

    /// <inheritdoc />
    public override string ToString()
    {
        var core = $"{this.Major}.{this.Minor}.{this.Patch}";
        return this.Prerelease is null ? core : $"{core}-{this.Prerelease}";
    }

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    public static bool operator ==(SemanticVersion left, SemanticVersion right) => left.Equals(right);

    public static bool operator !=(SemanticVersion left, SemanticVersion right) => !left.Equals(right);
}
