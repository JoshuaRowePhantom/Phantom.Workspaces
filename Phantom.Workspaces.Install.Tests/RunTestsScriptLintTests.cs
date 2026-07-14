namespace Phantom.Workspaces.Install.Tests;

/// <summary>
/// Lints <c>scripts/run-tests.ps1</c> to ensure the crash-detection guard is present.
/// Addresses issue #433 (missing guard from #147) and #378 (TRX-based crash detection).
/// </summary>
public sealed class RunTestsScriptLintTests
{
    private static string ScriptContent { get; } = File.ReadAllText(
        Path.Combine(FindRepositoryRoot().FullName, "scripts", "run-tests.ps1"));

    [Fact]
    public void RunTestsScript_ContainsCrashDetectionGuard_TrxLogger()
    {
        Assert.Contains("--logger", ScriptContent);
        Assert.Contains("trx", ScriptContent);
    }

    [Fact]
    public void RunTestsScript_ContainsCrashDetectionGuard_TrxParsing()
    {
        Assert.Contains("$trxFiles = Get-ChildItem", ScriptContent);
        Assert.Contains("$trxOutcome", ScriptContent);
    }

    [Fact]
    public void RunTestsScript_ContainsCrashDetectionGuard_AbortedOutcome()
    {
        Assert.Contains("$trxOutcome -eq 'Aborted'", ScriptContent);
    }

    [Fact]
    public void RunTestsScript_ContainsCrashDetectionGuard_BenignEmptyMatchFilter()
    {
        Assert.Contains("Could not find files for the given pattern", ScriptContent);
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
