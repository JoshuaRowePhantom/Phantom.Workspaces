using System.Runtime.InteropServices;
using Phantom.Workspaces.Services.Updates;

namespace Phantom.Workspaces.Tests;

public sealed class UpdateControllerFactoryTests
{
    [Fact]
    public void UpdateController_Arm64Architecture_ReportsUnsupportedArchitecture()
    {
        var exception = Assert.Throws<PlatformNotSupportedException>(
            () => UpdateControllerFactory.ResolveAssetMoniker(Architecture.Arm64));

        Assert.Contains("Microsoft MXC", exception.Message, StringComparison.Ordinal);
        Assert.Contains("win-x64", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateController_X64Architecture_SelectsWinX64()
    {
        Assert.Equal("win-x64", UpdateControllerFactory.ResolveAssetMoniker(Architecture.X64));
    }
}
