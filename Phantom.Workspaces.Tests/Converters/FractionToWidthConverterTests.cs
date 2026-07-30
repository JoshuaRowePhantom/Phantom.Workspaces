using System.Globalization;
using Phantom.Workspaces.Converters;
using Xunit;

namespace Phantom.Workspaces.Tests.Converters;

public sealed class FractionToWidthConverterTests
{
    [Fact]
    public void FractionToWidthConverter_NullFraction_ReturnsZero()
    {
        var converter = FractionToWidthConverter.Instance;

        var result = converter.Convert(null, typeof(double), "120", CultureInfo.InvariantCulture);

        Assert.Equal(0.0, (double)result!);
    }
}
