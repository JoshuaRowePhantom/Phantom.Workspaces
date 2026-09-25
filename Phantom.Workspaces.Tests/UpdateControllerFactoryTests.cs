using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Install;
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

    [Fact]
    public void UpdateControllerFactory_NonInstallLayout_ReportsUnsupportedReason()
    {
        var layout = new InstallLayout(new RealFileSystem(), Path.Combine("app", "managed"));
        var executable = Path.Combine("app", "development", InstallLayout.ApplicationExecutableName);

        var reason = UpdateControllerFactory.GetUnavailableReason(layout, executable, currentVersion: null);

        Assert.Contains("run/build", reason, StringComparison.Ordinal);
        Assert.Contains("run/build",
            UpdateControllerFactory.GetUnavailableReason(layout, executable, currentVersion: "0.0.22"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateControllerFactory_DevelopmentProcess_DoesNotCreateController()
    {
        var result = UpdateControllerFactory.TryCreate(
            new WorkspacesConfiguration(),
            () => { },
            NullLoggerFactory.Instance,
            installRootOverride: Path.Combine("uninstalled", "managed"));

        Assert.Null(result.Controller);
        Assert.Contains("run/build", result.UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateControllerFactory_InstalledLayoutMissingLink_ReportsRepairReason()
    {
        var layout = new InstallLayout(new RealFileSystem(), Path.Combine("app", "managed"));
        var executable = layout.CurrentExecutablePath;

        var reason = UpdateControllerFactory.GetUnavailableReason(layout, executable, currentVersion: null);

        Assert.Contains("reinstall", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(UpdateControllerFactory.GetUnavailableReason(layout, executable, currentVersion: "0.0.22"));
    }
}
