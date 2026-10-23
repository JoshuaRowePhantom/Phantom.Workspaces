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
        Assert.Contains("Results File:", ScriptContent);
        Assert.Contains("Select-Object -Unique", ScriptContent);
        Assert.Contains("$trxOutcome", ScriptContent);
    }

    [Fact]
    public void RunTestsScript_PreservesDiagnosticDumps()
    {
        Assert.DoesNotContain("Remove-Item", ScriptContent);
        Assert.Contains("test-execution-plan.ps1", ScriptContent);
    }

    [Theory]
    [InlineData("LifecycleSeconds", 2340)]
    [InlineData("WatchdogSeconds", 2400)]
    public async Task ExecutionPlan_RejectsIncoherentBudgets(string property, int seconds)
    {
        await using var fixture = new MxcRepositoryTestSupport.TestDirectory();
        var scripts = Path.Combine(FindRepositoryRoot().FullName, "scripts");
        var script = Path.Combine(fixture.Path, "test-execution-plan.ps1");
        File.Copy(Path.Combine(scripts, "test-execution-plan.ps1"), script);
        var contract = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(Path.Combine(scripts, "cold-publish-test-contract.json")))!;
        contract[property] = seconds;
        File.WriteAllText(Path.Combine(fixture.Path, "cold-publish-test-contract.json"),
            contract.ToJsonString());
        var result = await MxcRepositoryTestSupport.InvokeAsync(
            "pwsh", "-NoProfile", "-NonInteractive", "-Command",
            $"$ErrorActionPreference='Stop'; . '{script}'; " +
            "Get-TestExecutionPlan -Target test.slnx -Mode full -PerTestHangTimeout 240s -RepositoryRoot .");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Cold publish watchdog must outlast", result.StandardError);
    }

    [Theory]
    [InlineData("full", "", "", 2)]
    [InlineData("fast", "", "", 1)]
    [InlineData("full", "RunTestsScriptLintTests", "", 1)]
    [InlineData("fast", "CopilotWrapperNestedPublishTests", "", 2)]
    [InlineData("full", "", "Phantom.Workspaces.Core.Tests", 1)]
    public async Task ExecutionPlan_PreservesSelectionAndIsolatesColdPublish(
        string mode, string names, string project, int partitions)
    {
        var root = FindRepositoryRoot().FullName;
        var target = project.Length == 0
            ? Path.Combine(root, "Phantom.Workspaces.slnx")
            : Path.Combine(root, project, project + ".csproj");
        var script = Path.Combine(root, "scripts", "test-execution-plan.ps1");
        var result = await MxcRepositoryTestSupport.InvokeAsync(
            "pwsh", "-NoProfile", "-NonInteractive", "-Command",
            $". '{script}'; @(Get-TestExecutionPlan -Target '{target}' " +
            $"-Filter 'Category!=Integration' -TestNames @('{names}') -Mode {mode} " +
            $"-PerTestHangTimeout 240s -RepositoryRoot '{root}') | ConvertTo-Json -AsArray -Depth 5");
        Assert.True(result.ExitCode == 0, result.StandardError);
        using var json = System.Text.Json.JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(partitions, json.RootElement.GetArrayLength());
        Assert.Equal("240s", json.RootElement[0].GetProperty("HangTimeout").GetString());
        Assert.Contains("Category!=Integration",
            json.RootElement[0].GetProperty("Filter").GetString());
        if (partitions == 2)
        {
            var cold = json.RootElement[1];
            Assert.EndsWith("Phantom.Workspaces.Install.Tests.csproj",
                cold.GetProperty("Target").GetString());
            Assert.Contains("FullyQualifiedName!=", json.RootElement[0].GetProperty("Filter").GetString());
            Assert.Contains("FullyQualifiedName=", cold.GetProperty("Filter").GetString());
            Assert.Contains("Category!=Integration", cold.GetProperty("Filter").GetString());
            using var contract = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(Path.Combine(root, "scripts", "cold-publish-test-contract.json")));
            var values = contract.RootElement;
            Assert.True(values.GetProperty("LifecycleSeconds").GetInt32() >
                2 * values.GetProperty("CleanSeconds").GetInt32() +
                values.GetProperty("PrepareSeconds").GetInt32() +
                values.GetProperty("PublishSeconds").GetInt32());
            Assert.Equal(values.GetProperty("WatchdogSeconds").GetInt32() + "s",
                cold.GetProperty("HangTimeout").GetString());
            Assert.True(values.GetProperty("WatchdogSeconds").GetInt32() >
                values.GetProperty("LifecycleSeconds").GetInt32());
        }
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
