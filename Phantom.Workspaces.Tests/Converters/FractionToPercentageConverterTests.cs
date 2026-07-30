using System.Globalization;
using Phantom.Workspaces.Converters;
using Xunit;

namespace Phantom.Workspaces.Tests.Converters;

public sealed class FractionToPercentageConverterTests
{
    [Fact]
    public void FractionToPercentageConverter_NullFraction_ReturnsEmDash()
    {
        var converter = FractionToPercentageConverter.Instance;

        var result = converter.Convert(null, typeof(string), null, CultureInfo.InvariantCulture);

        Assert.Equal("—", (string)result!);
    }
}
