using System;
using System.Linq;
using System.Text;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces;

/// <summary>
/// Maps a status value to the theme resource key of the brush used to render its badge. Values listed
/// in the field's <see cref="FieldStatus.GoodStatusValues"/> render green and those in
/// <see cref="FieldStatus.BadStatusValues"/> render red; every other value is given a stable, distinct
/// color from a fixed six-color palette chosen by hashing the value.
/// </summary>
/// <remarks>
/// Matching is case-sensitive: both the schema's status lists and the entity's field value are stored
/// data, which the repository matches case-sensitively. The hash is a deterministic FNV-1a over the
/// value's UTF-8 bytes, so the same value always maps to the same palette color across runs,
/// processes, and machines. <see cref="string.GetHashCode()"/> is deliberately not used because it is
/// randomized per process.
/// </remarks>
public sealed class StatusColorSelector
{
    public const string GoodStatusBrushKey = "Theme.Status.Good";

    public const string BadStatusBrushKey = "Theme.Status.Bad";

    public const string PaletteBrushKeyPrefix = "Theme.Status.Palette.";

    public const int PaletteSize = 6;

    /// <summary>
    /// Returns the theme resource key of the brush for a status value, given the field's status
    /// annotation. The annotation is nullable for callers/tests, but the card only builds badges for
    /// annotated fields, so a badge always carries a non-null annotation.
    /// </summary>
    public string SelectStatusBrushKey(
        string statusValue,
        FieldStatus? fieldStatus)
    {
        ArgumentNullException.ThrowIfNull(statusValue);

        if (fieldStatus is { } status)
        {
            if (status.GoodStatusValues.Contains(statusValue, StringComparer.Ordinal))
            {
                return GoodStatusBrushKey;
            }

            if (status.BadStatusValues.Contains(statusValue, StringComparer.Ordinal))
            {
                return BadStatusBrushKey;
            }
        }

        var index = (int)(StableHash(statusValue) % PaletteSize);
        return $"{PaletteBrushKeyPrefix}{index}";
    }

    private static uint StableHash(
        string value)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        var hash = offsetBasis;
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= prime;
        }

        return hash;
    }
}
