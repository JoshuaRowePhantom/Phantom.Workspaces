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
    public void GetHelpText_DescribesConfigFileOnlyUsage()
    {
        var helpText = CommandLineOptions.GetHelpText();

        Assert.Contains("<config-file>", helpText);
        Assert.Contains("configuration file", helpText);
        Assert.Contains("/?", helpText);
        // Repository parameters are no longer accepted on the command line.
        Assert.DoesNotContain("--data-store", helpText);
        Assert.DoesNotContain("--mongodb-container-name", helpText);
    }

    [Fact]
    public void TryGetConfigurationFilePath_ReturnsFirstNonHelpArgument()
    {
        Assert.True(CommandLineOptions.TryGetConfigurationFilePath(["C:\\configs\\workspace.json"], out var path));
        Assert.Equal("C:\\configs\\workspace.json", path);

        Assert.True(CommandLineOptions.TryGetConfigurationFilePath(["/h", "C:\\configs\\workspace.json"], out var withHelp));
        Assert.Equal("C:\\configs\\workspace.json", withHelp);

        Assert.False(CommandLineOptions.TryGetConfigurationFilePath([], out var none));
        Assert.Null(none);
    }
}
