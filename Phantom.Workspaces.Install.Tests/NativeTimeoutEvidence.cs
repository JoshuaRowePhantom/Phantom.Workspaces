namespace Phantom.Workspaces.Install.Tests;

internal static class NativeTimeoutEvidence
{
    internal static async Task<string> CaptureAsync(
        IReadOnlyList<int> ownedProcessIds,
        string stage,
        string outputDirectory,
        CancellationToken cancellationToken,
        string nativeCompilerName = "crossgen2.exe",
        string? captureScriptPath = null)
    {
        var result = await MxcRepositoryTestSupport.InvokeAsync(
            "pwsh",
            new MxcRepositoryTestSupport.InvocationOptions
            {
                Timeout = TimeSpan.FromSeconds(30),
                CancellationToken = cancellationToken
            },
            "-NoProfile", "-NonInteractive", "-File",
            captureScriptPath ?? Path.Combine(MxcRepositoryTestSupport.Root.FullName, "scripts",
                "capture-owned-timeout-evidence.ps1"),
            "-OwnedProcessIds", string.Join(",", ownedProcessIds),
            "-Stage", stage, "-OutputDirectory", outputDirectory,
            "-NativeCompilerName", nativeCompilerName);
        if (result.ExitCode != 0)
            throw new IOException($"Native evidence capture failed in {outputDirectory}:\n{result.StandardOutput}\n{result.StandardError}");
        return result.StandardOutput;
    }
}
