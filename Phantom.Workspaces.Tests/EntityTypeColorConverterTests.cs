using System.Globalization;
using Avalonia.Media;
using Phantom.Workspaces.Converters;

namespace Phantom.Workspaces.Tests;

public sealed class EntityTypeColorConverterTests
{
    [Fact]
    public void Convert_NullValue_ReturnsTransparent()
    {
        var brush = EntityTypeColorConverter.Instance.Convert(null, typeof(IBrush), null, CultureInfo.InvariantCulture);

        Assert.Same(Brushes.Transparent, brush);
    }

    [Fact]
    public void Convert_EmptyEnumerable_ReturnsTransparent()
    {
        var brush = EntityTypeColorConverter.Instance.Convert(Array.Empty<string>(), typeof(IBrush), null, CultureInfo.InvariantCulture);

        Assert.Same(Brushes.Transparent, brush);
    }

    [Fact]
    public void StableHash_UsesFnv1aAlgorithm()
    {
        Assert.Equal(2442791997u, EntityTypeColorConverter.StableHash("note"));
    }

    [Fact]
    public void StableHash_SameSortedTypeSet_ReturnsSameHash()
    {
        static uint Hash(params string[] names) =>
            EntityTypeColorConverter.StableHash(string.Join('\u0001', names.Distinct().Order(StringComparer.Ordinal)));

        Assert.Equal(Hash("note", "task"), Hash("task", "note", "task"));
    }
}
