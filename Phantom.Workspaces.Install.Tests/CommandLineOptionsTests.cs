using Phantom.Workspaces.Install;

namespace Phantom.Workspaces.Install.Tests;

public sealed class CommandLineOptionsTests
{
    [Fact]
    public void Parse_NoArguments_SelectsGuiMode()
    {
        var options = CommandLineOptions.Parse();

        Assert.True(options.IsValid);
        Assert.Equal(LaunchMode.Gui, options.Mode);
        Assert.Equal(ExitCode.Success, options.ExitCode);
    }

    [Fact]
    public void Parse_Install_SelectsInstallMode()
    {
        var options = CommandLineOptions.Parse("--install");

        Assert.True(options.IsValid);
        Assert.Equal(LaunchMode.Install, options.Mode);
        Assert.False(options.Silent);
    }

    [Fact]
    public void Parse_InstallSilent_SetsSilent()
    {
        var options = CommandLineOptions.Parse("--install", "--silent");

        Assert.True(options.IsValid);
        Assert.Equal(LaunchMode.Install, options.Mode);
        Assert.True(options.Silent);
    }

    [Fact]
    public void Parse_Startup_SelectsStartupMode()
    {
        var options = CommandLineOptions.Parse("--startup");

        Assert.True(options.IsValid);
        Assert.Equal(LaunchMode.Startup, options.Mode);
    }

    [Fact]
    public void Parse_Minimized_SelectsMinimizedMode()
    {
        var options = CommandLineOptions.Parse("--minimized");

        Assert.True(options.IsValid);
        Assert.Equal(LaunchMode.Minimized, options.Mode);
    }

    [Fact]
    public void Parse_ApplyUpdateWithDirectory_CapturesDirectory()
    {
        var options = CommandLineOptions.Parse("--apply-update", @"C:\sandbox\versions\0.2.0", "--relaunch");

        Assert.True(options.IsValid);
        Assert.Equal(LaunchMode.ApplyUpdate, options.Mode);
        Assert.Equal(@"C:\sandbox\versions\0.2.0", options.ApplyUpdateDirectory);
        Assert.True(options.Relaunch);
    }

    [Fact]
    public void Parse_ApplyUpdateWithoutDirectory_IsInvalid()
    {
        var options = CommandLineOptions.Parse("--apply-update");

        Assert.False(options.IsValid);
        Assert.Equal(ExitCode.BadArguments, options.ExitCode);
    }

    [Fact]
    public void Parse_UninstallPurge_SetsPurge()
    {
        var options = CommandLineOptions.Parse("--uninstall", "--purge");

        Assert.True(options.IsValid);
        Assert.Equal(LaunchMode.Uninstall, options.Mode);
        Assert.True(options.Purge);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Parse_Help_SelectsHelpMode(string argument)
    {
        var options = CommandLineOptions.Parse(argument);

        Assert.True(options.IsValid);
        Assert.Equal(LaunchMode.Help, options.Mode);
    }

    [Fact]
    public void Parse_InstallRootOverride_AppliesAcrossModes()
    {
        var options = CommandLineOptions.Parse("--install", "--install-root", @"C:\sandbox\app");

        Assert.True(options.IsValid);
        Assert.Equal(LaunchMode.Install, options.Mode);
        Assert.Equal(@"C:\sandbox\app", options.InstallRootOverride);
    }

    [Fact]
    public void Parse_InstallRootWithoutPath_IsInvalid()
    {
        var options = CommandLineOptions.Parse("--install-root");

        Assert.False(options.IsValid);
        Assert.Equal(ExitCode.BadArguments, options.ExitCode);
    }

    [Fact]
    public void Parse_UnknownArgument_IsInvalidWithBadArguments()
    {
        var options = CommandLineOptions.Parse("--nope");

        Assert.False(options.IsValid);
        Assert.Equal(ExitCode.BadArguments, options.ExitCode);
        Assert.NotNull(options.Error);
    }

    [Fact]
    public void Parse_ConflictingModes_IsInvalid()
    {
        var options = CommandLineOptions.Parse("--install", "--uninstall");

        Assert.False(options.IsValid);
        Assert.Equal(ExitCode.BadArguments, options.ExitCode);
    }

    [Fact]
    public void Parse_SilentWithoutInstall_IsInvalid()
    {
        var options = CommandLineOptions.Parse("--startup", "--silent");

        Assert.False(options.IsValid);
        Assert.Equal(ExitCode.BadArguments, options.ExitCode);
    }

    [Fact]
    public void Parse_UpdatePositional_SelectsUpdateMode()
    {
        var options = CommandLineOptions.Parse("update");

        Assert.True(options.IsValid);
        Assert.Equal(LaunchMode.Update, options.Mode);
    }

    [Fact]
    public void Parse_UpdateFlag_SelectsUpdateMode()
    {
        var options = CommandLineOptions.Parse("--update");

        Assert.True(options.IsValid);
        Assert.Equal(LaunchMode.Update, options.Mode);
    }
}
