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
}
