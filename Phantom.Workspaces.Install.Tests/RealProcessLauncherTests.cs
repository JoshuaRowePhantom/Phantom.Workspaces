using Phantom.Workspaces.Install;

namespace Phantom.Workspaces.Install.Tests;

public sealed class RealProcessLauncherTests
{
    [Fact]
    public void CreateStartInfo_CopiesFileNameArgumentsAndWorkingDirectory()
    {
        var request = new ProcessStartRequest
        {
            FileName = @"C:\app\current\Phantom.Workspaces.exe",
            Arguments = new[] { "--apply-update", @"C:\app\versions\0.2.0", "--relaunch" },
            WorkingDirectory = @"C:\app",
        };

        var startInfo = RealProcessLauncher.CreateStartInfo(request);

        Assert.Equal(request.FileName, startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(request.Arguments, startInfo.ArgumentList);
        Assert.Equal(@"C:\app", startInfo.WorkingDirectory);
    }

    [Fact]
    public void CreateStartInfo_RejectsEmptyFileName()
    {
        var request = new ProcessStartRequest { FileName = "   " };
        Assert.Throws<ArgumentException>(() => RealProcessLauncher.CreateStartInfo(request));
    }

    [Fact]
    public void CreateStartInfo_DetachedRequest_UsesShellExecute()
    {
        // Regression for #1302: detached fire-and-forget launches must not inherit the parent
        // console's stdio handles. Using UseShellExecute=true is Windows's built-in way to launch
        // without handle inheritance (matches the other fire-and-forget spawns in the codebase).
        var request = new ProcessStartRequest
        {
            FileName = @"C:\app\current\Phantom.Workspaces.exe",
            Arguments = new[] { "--startup" },
            Detached = true,
        };

        var startInfo = RealProcessLauncher.CreateStartInfo(request);

        Assert.True(startInfo.UseShellExecute);
    }

    [Fact]
    public void CreateStartInfo_NonDetachedRequest_KeepsUseShellExecuteFalse()
    {
        var request = new ProcessStartRequest
        {
            FileName = @"C:\app\current\Phantom.Workspaces.exe",
        };

        var startInfo = RealProcessLauncher.CreateStartInfo(request);

        Assert.False(startInfo.UseShellExecute);
    }
}
