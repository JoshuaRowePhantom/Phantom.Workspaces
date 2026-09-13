namespace Phantom.Workspaces.Install.Tests;

[Collection(MxcIntegrationCollection.Name)]
public sealed class CopilotWrapperPackagingTests
{
    [Fact]
    public void RuntimePayload_CommandUsesFocusedIsolatedNonReusableBuild()
    {
        var arguments = MxcRepositoryTestSupport.CreateCopilotRuntimePayloadArguments(
            "payload",
            "isolated-artifacts");

        Assert.Contains("--disable-build-servers", arguments);
        Assert.Contains("-m:1", arguments);
        Assert.Contains("/nodeReuse:false", arguments);
        Assert.Contains("-p:UseSharedCompilation=false", arguments);
        Assert.Contains("-p:UseArtifactsOutput=true", arguments);
        Assert.Contains("-p:ArtifactsPath=isolated-artifacts", arguments);
        Assert.Contains("-t:PublishCopilotRuntimeLoose;PublishMxcRuntimeLoose", arguments);
        Assert.Contains("-p:SkipCopilotWrapperPublish=true", arguments);
        Assert.DoesNotContain("publish", arguments);
    }

    [Fact]
    public async Task RuntimePayload_WinX64_IncludesWrapperCliMxcAndLicenses()
    {
        await using var payload = new MxcRepositoryTestSupport.TestDirectory(
            cleanupProgress: message => Console.WriteLine($"Copilot payload {message}"));
        await using var buildArtifacts = new MxcRepositoryTestSupport.TestDirectory(
            cleanupProgress: message => Console.WriteLine($"Copilot build artifacts {message}"));
        using var sharedOutputLock = MxcRepositoryTestSupport.LockSharedRuntimeConfig();

        var wrapperPublish = await MxcRepositoryTestSupport.InvokeAsync(
            "dotnet",
            MxcRepositoryTestSupport.CreateCopilotWrapperPublishArguments(
                payload.Path,
                buildArtifacts.Path));
        Assert.True(
            wrapperPublish.ExitCode == 0,
            $"Copilot wrapper publish failed.\nSTDOUT:\n{wrapperPublish.StandardOutput}\nSTDERR:\n{wrapperPublish.StandardError}");
        Assert.True(wrapperPublish.DirectProcessInJob);
        Assert.Equal(0U, wrapperPublish.ActiveJobProcessesAfterCleanup);
        var wrapperPath = Path.Combine(
            payload.Path,
            "runtimes",
            "win-x64",
            "native",
            "phantom-copilot-wrapper.exe");
        var publishedWrapperHash = await HashFileAsync(wrapperPath);

        var payloadAssembly = await MxcRepositoryTestSupport.InvokeAsync(
            "dotnet",
            MxcRepositoryTestSupport.CreateCopilotRuntimePayloadArguments(
                payload.Path,
                buildArtifacts.Path));

        Assert.True(
            payloadAssembly.ExitCode == 0,
            $"Copilot payload assembly failed.\nSTDOUT:\n{payloadAssembly.StandardOutput}\nSTDERR:\n{payloadAssembly.StandardError}");
        Assert.True(payloadAssembly.DirectProcessInJob);
        Assert.Equal(0U, payloadAssembly.ActiveJobProcessesAfterCleanup);
        Assert.Equal(publishedWrapperHash, await HashFileAsync(wrapperPath));

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

    private static async Task<byte[]> HashFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return await System.Security.Cryptography.SHA256.HashDataAsync(stream);
    }
}
