namespace Phantom.Workspaces.Install.Tests;

[Collection(MxcIntegrationCollection.Name)]
public sealed class CopilotWrapperPackagingTests
{
    [Fact]
    public async Task RuntimePayload_WinX64_IncludesWrapperCliMxcAndLicenses()
    {
        await using var payload = new MxcRepositoryTestSupport.TestDirectory(
            cleanupProgress: message => Console.WriteLine($"Copilot payload {message}"));
        await using var buildArtifacts = new MxcRepositoryTestSupport.TestDirectory(
            cleanupProgress: message => Console.WriteLine($"Copilot build artifacts {message}"));
        using var sharedOutputLock = MxcRepositoryTestSupport.LockSharedRuntimeConfig();

        var publish = await MxcRepositoryTestSupport.InvokeAsync(
            "dotnet",
            MxcRepositoryTestSupport.CreateCopilotRuntimePublishArguments(
                payload.Path,
                buildArtifacts.Path));

        Assert.True(
            publish.ExitCode == 0,
            $"Copilot payload publish failed.\nSTDOUT:\n{publish.StandardOutput}\nSTDERR:\n{publish.StandardError}");
        Assert.True(publish.DirectProcessInJob);
        Assert.Equal(0U, publish.ActiveJobProcessesAfterCleanup);

        var nativeDirectory = Path.Combine(payload.Path, "runtimes", "win-x64", "native");
        foreach (var fileName in new[]
                 {
                     "phantom-copilot-wrapper.exe",
                     "copilot.exe",
                     "mxc_ffi.dll",
                     "plm.exe",
                     "LICENSE.md",
                     "MXC-LICENSE.md",
                 })
        {
            var file = new FileInfo(Path.Combine(nativeDirectory, fileName));
            Assert.True(file.Exists, $"Published payload is missing '{fileName}'.");
            Assert.True(file.Length > 0, $"Published payload contains an empty '{fileName}'.");
        }

        var validation = await MxcRepositoryTestSupport.InvokePowerShellAsync(
            "packaging",
            "validate",
            "Assert-CopilotRuntimePayload.ps1",
            "-PayloadDirectory", payload.Path,
            "-RuntimeIdentifier", "win-x64",
            "-SkipStartupSmoke");
        Assert.Equal(0, validation.ExitCode);
        Assert.Contains(
            "Copilot runtime payload validation passed",
            validation.StandardOutput,
            StringComparison.Ordinal);
    }
}
