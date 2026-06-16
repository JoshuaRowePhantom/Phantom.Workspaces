using Phantom.Workspaces;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class CommandLineOptionsTests
{
    [Theory]
    [InlineData("/?")]
    [InlineData("-?")]
    [InlineData("/h")]
    [InlineData("-h")]
    [InlineData("/help")]
    [InlineData("--help")]
    [InlineData("--HELP")]
    public void IsHelpRequested_TrueForHelpFlags(string flag)
    {
        Assert.True(CommandLineOptions.IsHelpRequested([flag]));
    }

    [Fact]
    public void IsHelpRequested_DetectsFlagAmongOtherArguments()
    {
        Assert.True(CommandLineOptions.IsHelpRequested(["--data-store", "mongodb", "/?"]));
    }

    [Fact]
    public void IsHelpRequested_FalseForNormalArguments()
    {
        Assert.False(CommandLineOptions.IsHelpRequested(["--data-store", "mongodb"]));
        Assert.False(CommandLineOptions.IsHelpRequested([]));
        Assert.False(CommandLineOptions.IsHelpRequested(["C:\\repos\\workspace"]));
    }

    [Fact]
    public void GetHelpText_DescribesKeyOptions()
    {
        var helpText = CommandLineOptions.GetHelpText();

        Assert.Contains("--data-store mongodb", helpText);
        Assert.Contains("--mongodb-container-name", helpText);
        Assert.Contains("--mongodb-root-collection-name", helpText);
        Assert.Contains("/?", helpText);
    }
}
