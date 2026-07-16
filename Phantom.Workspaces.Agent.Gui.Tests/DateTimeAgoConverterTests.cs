using System;
using System.Globalization;
using Phantom.Workspaces.Agent.Gui.Converters;
using Xunit;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class DateTimeAgoConverterTests
{
    private static string Convert(DateTime value)
        => (string)DateTimeAgoConverter.Instance.Convert(value, typeof(string), null, CultureInfo.InvariantCulture)!;

    [Fact]
    public void DateTimeAgoConverter_LessThanOneMinute_ReturnsJustNow()
    {
        var value = DateTime.UtcNow.AddSeconds(-5);
        Assert.Equal("just now", Convert(value));
    }

    [Fact]
    public void DateTimeAgoConverter_Minutes_ReturnsMinutesAgo()
    {
        Assert.Equal("1 minute ago", Convert(DateTime.UtcNow.AddMinutes(-1)));
        Assert.Equal("5 minutes ago", Convert(DateTime.UtcNow.AddMinutes(-5)));
    }

    [Fact]
    public void DateTimeAgoConverter_Hours_ReturnsHoursAgo()
    {
        Assert.Equal("1 hour ago", Convert(DateTime.UtcNow.AddHours(-1)));
        Assert.Equal("3 hours ago", Convert(DateTime.UtcNow.AddHours(-3)));
    }

    [Fact]
    public void DateTimeAgoConverter_Days_ReturnsDaysAgo()
    {
        Assert.Equal("1 day ago", Convert(DateTime.UtcNow.AddDays(-1)));
        Assert.Equal("4 days ago", Convert(DateTime.UtcNow.AddDays(-4)));
    }

    [Fact]
    public void DateTimeAgoConverter_FutureValue_ReturnsJustNow()
    {
        Assert.Equal("just now", Convert(DateTime.UtcNow.AddMinutes(5)));
    }

    [Fact]
    public void DateTimeAgoConverter_Null_ReturnsEmptyString()
    {
        var result = DateTimeAgoConverter.Instance.Convert(null, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void DateTimeAgoConverter_LocalKind_ComparesAgainstLocalNow()
    {
        // A local-kind timestamp must compare against DateTime.Now, not DateTime.UtcNow.
        var value = DateTime.Now.AddMinutes(-2);
        Assert.Equal("2 minutes ago", Convert(value));
    }

    [Fact]
    public void DateTimeAgoConverter_ConvertBack_Throws()
    {
        Assert.Throws<NotSupportedException>(
            () => DateTimeAgoConverter.Instance.ConvertBack("x", typeof(DateTime), null, CultureInfo.InvariantCulture));
    }
}
