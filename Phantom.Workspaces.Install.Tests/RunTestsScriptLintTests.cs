namespace Phantom.Workspaces.Install.Tests;

/// <summary>
/// Lints <c>scripts/run-tests.ps1</c> to ensure the crash-detection guard is present.
/// Addresses issue #433 (missing guard from #147).
/// </summary>
public sealed class RunTestsScriptLintTests
{
    private static string ScriptContent { get; } = File.ReadAllText(
        Path.Combine(FindRepositoryRoot().FullName, "scripts", "run-tests.ps1"));

    [Fact]
    public void RunTestsScript_ContainsCrashDetectionGuard_AbortString()
    {
        Assert.Contains("Test Run was aborted", ScriptContent);
    }

    [Fact]
    public void RunTestsScript_ContainsCrashDetectionGuard_HostExitString()
    {
        Assert.Contains("host process exited unexpectedly", ScriptContent);
    }

    [Fact]
    public void RunTestsScript_ContainsCrashDetectionGuard_ExitCodeOverride()
    {
        Assert.Contains("$exitCode -eq 0 -and $hostCrashed", ScriptContent);
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

        throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
    }
}
