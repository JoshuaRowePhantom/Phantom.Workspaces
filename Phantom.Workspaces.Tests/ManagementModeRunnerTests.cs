using System.Runtime.InteropServices;

namespace Phantom.Workspaces.Tests;

public sealed class ManagementModeRunnerTests
{
    [Fact]
    public void ManagementMode_Arm64Architecture_ReportsUnsupportedArchitecture()
    {
        var exception = Assert.Throws<PlatformNotSupportedException>(
            () => ManagementModeDispatcher.ResolveAssetMoniker(Architecture.Arm64));

        Assert.Contains("Microsoft MXC", exception.Message, StringComparison.Ordinal);
        Assert.Contains("win-x64", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagementMode_X64Architecture_SelectsWinX64()
    {
        Assert.Equal("win-x64", ManagementModeDispatcher.ResolveAssetMoniker(Architecture.X64));
    }
}
