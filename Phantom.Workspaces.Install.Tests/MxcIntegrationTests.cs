using System.Diagnostics;

namespace Phantom.Workspaces.Install.Tests;

public sealed class BuildIntegrationTests
{
    [Fact]
    public void BuildSolution_MxcSourceDependency_BuildsThroughStandardEntryPoint()
    {
        var gitModules = MxcRepositoryTestSupport.Read(".gitmodules");
        var solution = MxcRepositoryTestSupport.Read("Phantom.Workspaces.slnx");
        var coreProject = MxcRepositoryTestSupport.Read(
            "Phantom.Workspaces.Llm.Core", "Phantom.Workspaces.Llm.Core.csproj");

        Assert.Contains("https://github.com/microsoft/mxc", gitModules, StringComparison.Ordinal);
        Assert.Contains("microsoft/mxc/sdk/dotnet/Microsoft.Mxc.Sdk/Microsoft.Mxc.Sdk.csproj", solution, StringComparison.Ordinal);
        Assert.Contains(@"..\microsoft\mxc\sdk\dotnet\Microsoft.Mxc.Sdk\Microsoft.Mxc.Sdk.csproj", coreProject, StringComparison.Ordinal);
        Assert.Equal(
            "29702c3a408462a4e6be0f265328693a0d2169eb",
            MxcRepositoryTestSupport.InvokeGit(
                "-C", Path.Combine(MxcRepositoryTestSupport.Root.FullName, "microsoft", "mxc"),
                "rev-parse", "HEAD"));
    }
}

public sealed class ReleasePackagingTests
{
    [Fact]
    public void ReleaseArtifacts_CurrentMatrix_ContainsOnlyWinX64()
    {
        var applicationProject = MxcRepositoryTestSupport.Read("Phantom.Workspaces", "Phantom.Workspaces.csproj");
        var release = MxcRepositoryTestSupport.Read(".github", "workflows", "release.yml");
        var validation = MxcRepositoryTestSupport.Read(".github", "workflows", "publish-validation.yml");

        Assert.Contains("<RuntimeIdentifiers>win-x64</RuntimeIdentifiers>", applicationProject, StringComparison.Ordinal);
        Assert.DoesNotContain("win-arm64", release, StringComparison.Ordinal);
        Assert.DoesNotContain("win-arm64", validation, StringComparison.Ordinal);
    }
}

public sealed class MxcNativeUnitTests
{
    [Fact]
    public void MxcNativeUnit_WinX64Build_UpstreamTestsPass()
    {
        var setup = MxcRepositoryTestSupport.Read(".github", "actions", "setup-build", "action.yml");
        var release = MxcRepositoryTestSupport.Read(".github", "workflows", "release.yml");

        Assert.Contains("rustup toolchain install 1.93.0", setup, StringComparison.Ordinal);
        Assert.Contains(".cargo", setup, StringComparison.Ordinal);
        Assert.Contains("microsoft/mxc/src/Cargo.lock", setup, StringComparison.Ordinal);
        Assert.Contains("29702c3a408462a4e6be0f265328693a0d2169eb", setup, StringComparison.Ordinal);
        Assert.Contains(
            "cargo test --manifest-path microsoft/mxc/src/Cargo.toml -p mxc_ffi --features dotnetsdk",
            release,
            StringComparison.Ordinal);
    }
}

public sealed class MxcSdkVersionTests
{
    [Fact]
    public void MxcSdkVersion_ManagedAndNativeUnits_Match()
    {
        var script = MxcRepositoryTestSupport.Read("packaging", "validate", "Assert-MxcSdkVersion.ps1");
        Assert.Contains("$managedVersion -ne $nativeSourceVersion", script, StringComparison.Ordinal);
        Assert.Contains("ProductVersion", script, StringComparison.Ordinal);
    }
}

public sealed class MxcRuntimePayloadTests
{
    [Fact]
    public void MxcRuntimePayload_RequiredNativeUnit_IsPresent()
    {
        var project = MxcRepositoryTestSupport.Read("Phantom.Workspaces", "Phantom.Workspaces.csproj");
        var validator = MxcRepositoryTestSupport.Read("packaging", "validate", "Assert-MxcRuntimePayload.ps1");

        Assert.Contains(@"runtimes\$(RuntimeIdentifier)\native", project, StringComparison.Ordinal);
        Assert.Contains("mxc_ffi.dll", project, StringComparison.Ordinal);
        Assert.Contains("plm.exe", project, StringComparison.Ordinal);
        Assert.Contains("@('mxc_ffi.dll', 'plm.exe', 'MXC-LICENSE.md')", validator, StringComparison.Ordinal);
    }

    [Fact]
    public void MxcRuntimePayload_RequiredLicenseNotice_IsPresent()
    {
        var project = MxcRepositoryTestSupport.Read("Phantom.Workspaces", "Phantom.Workspaces.csproj");
        var validator = MxcRepositoryTestSupport.Read("packaging", "validate", "Assert-MxcRuntimePayload.ps1");

        Assert.Contains(@"microsoft\mxc\LICENSE.md", project, StringComparison.Ordinal);
        Assert.Contains("MXC-LICENSE.md", validator, StringComparison.Ordinal);
        Assert.Contains("Unexpected mxc.lic", validator, StringComparison.Ordinal);
    }
}

public sealed class InstallScriptTests
{
    [Fact]
    public void Install_Arm64Architecture_ReportsUnsupportedArchitecture()
    {
        var script = Path.Combine(MxcRepositoryTestSupport.Root.FullName, "install.ps1");
        var result = MxcRepositoryTestSupport.Invoke(
            "pwsh",
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            $". '{script}' -RuntimeIdentifier win-arm64; try {{ Resolve-RuntimeIdentifier }} catch {{ [Console]::Error.Write($_.Exception.Message); exit 42 }}");

        Assert.Equal(42, result.ExitCode);
        Assert.Contains("Microsoft MXC", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("win-x64", result.StandardError, StringComparison.Ordinal);
    }
}

internal static class MxcRepositoryTestSupport
{
    internal static DirectoryInfo Root { get; } = FindRepositoryRoot();

    internal static string Read(params string[] relativePath)
        => File.ReadAllText(Path.Combine([Root.FullName, .. relativePath]));

    internal static string InvokeGit(params string[] arguments)
    {
        var result = Invoke("git", arguments);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return result.StandardOutput.Trim();
    }

    internal static ProcessResult Invoke(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, standardOutput, standardError);
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Phantom.Workspaces.slnx")))
            {
                return current;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
