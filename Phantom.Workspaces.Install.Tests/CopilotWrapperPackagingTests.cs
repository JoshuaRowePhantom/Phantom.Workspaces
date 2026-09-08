namespace Phantom.Workspaces.Install.Tests;

[Collection(MxcIntegrationCollection.Name)]
public sealed class CopilotWrapperPackagingTests
{
    [Fact]
    public void RuntimePayload_WinX64_IncludesWrapperCliMxcAndLicenses()
    {
        var applicationProject = MxcRepositoryTestSupport.Read(
            "Phantom.Workspaces", "Phantom.Workspaces.csproj");
        var validation = MxcRepositoryTestSupport.Read(
            "packaging", "validate", "Assert-CopilotRuntimePayload.ps1");

        Assert.Contains("phantom-copilot-wrapper.exe", applicationProject, StringComparison.Ordinal);
        Assert.Contains("phantom-copilot-wrapper.exe", validation, StringComparison.Ordinal);
        Assert.Contains("copilot.exe", validation, StringComparison.Ordinal);
        Assert.Contains("mxc_ffi.dll", validation, StringComparison.Ordinal);
        Assert.Contains("plm.exe", validation, StringComparison.Ordinal);
        Assert.Contains("LICENSE.md", validation, StringComparison.Ordinal);
        Assert.Contains("MXC-LICENSE.md", validation, StringComparison.Ordinal);
    }
}
