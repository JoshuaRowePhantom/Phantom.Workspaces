using System;
using System.Globalization;
using Phantom.Workspaces.Converters;

namespace Phantom.Workspaces.Tests;

public sealed class DateTimeOffsetToStringConverterTests
{
    [Fact]
    public void Convert_TodayWithinLast24Hours_ReturnsTimeOnly()
    {
        var now = DateTimeOffset.Now;
        var today = now.AddHours(-2);
        
        var result = DateTimeOffsetToStringConverter.Instance.Convert(today, typeof(string), null, CultureInfo.InvariantCulture);
        
        Assert.IsType<string>(result);
        var str = (string)result;
        Assert.Contains(":", str);
        Assert.DoesNotContain(today.Year.ToString(), str);
    }

    [Fact]
    public void Convert_ThisWeekWithinLast7Days_ReturnsDayAndTime()
    {
        var now = DateTimeOffset.Now;
        var thisWeek = now.AddDays(-3);
        
        var result = DateTimeOffsetToStringConverter.Instance.Convert(thisWeek, typeof(string), null, CultureInfo.InvariantCulture);
        
        Assert.IsType<string>(result);
        var str = (string)result;
        Assert.Contains(":", str);
    }

    [Fact]
    public void Convert_ThisYearMoreThan7DaysAgo_ReturnsMonthDayTime()
    {
        var now = DateTimeOffset.Now;
        var thisYear = new DateTimeOffset(now.Year, 1, 15, 10, 30, 0, now.Offset);
        
        var result = DateTimeOffsetToStringConverter.Instance.Convert(thisYear, typeof(string), null, CultureInfo.InvariantCulture);
        
        Assert.IsType<string>(result);
        var str = (string)result;
        Assert.DoesNotContain(thisYear.Year.ToString(), str);
    }

    [Fact]
    public void Convert_PreviousYear_ReturnsYearMonthDay()
    {
        var previousYear = new DateTimeOffset(2020, 6, 15, 14, 30, 0, TimeSpan.Zero);
        
        var result = DateTimeOffsetToStringConverter.Instance.Convert(previousYear, typeof(string), null, CultureInfo.InvariantCulture);
        
        Assert.IsType<string>(result);
        var str = (string)result;
        Assert.Contains("2020", str);
    }

    [Fact]
    public void Convert_MinValue_ReturnsEmptyString()
    {
        var result = DateTimeOffsetToStringConverter.Instance.Convert(DateTimeOffset.MinValue, typeof(string), null, CultureInfo.InvariantCulture);
        
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Convert_NonDateTimeOffset_ReturnsEmptyString()
    {
        var result = DateTimeOffsetToStringConverter.Instance.Convert("not a date", typeof(string), null, CultureInfo.InvariantCulture);
        
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Convert_Null_ReturnsEmptyString()
    {
        var result = DateTimeOffsetToStringConverter.Instance.Convert(null, typeof(string), null, CultureInfo.InvariantCulture);
        
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void ConvertBack_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(() =>
            DateTimeOffsetToStringConverter.Instance.ConvertBack("2024-01-01", typeof(DateTimeOffset), null, CultureInfo.InvariantCulture));
    }
}
