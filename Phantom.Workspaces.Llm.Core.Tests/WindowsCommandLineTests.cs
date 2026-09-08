using Phantom.Workspaces.Llm.Processes;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class WindowsCommandLineTests
{
    [Fact]
    public void WindowsCommandLine_EmbeddedQuotes_RoundTrips()
    {
        Assert.Equal("\"say \\\"hello\\\"\"", WindowsCommandLine.QuoteArgument("say \"hello\""));
    }

    [Fact]
    public void WindowsCommandLine_TrailingBackslashes_RoundTrips()
    {
        Assert.Equal("\"C:\\path with space\\\\\"", WindowsCommandLine.QuoteArgument(@"C:\path with space\"));
    }

    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("雪", "雪")]
    public void QuoteArgument_PreservesOtherArgumentShapes(string argument, string expected)
    {
        Assert.Equal(expected, WindowsCommandLine.QuoteArgument(argument));
    }
}
