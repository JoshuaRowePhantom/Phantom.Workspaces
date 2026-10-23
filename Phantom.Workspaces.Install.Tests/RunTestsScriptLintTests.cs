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
    [InlineData(0, "Only explicit owned job process IDs")]
    [InlineData(int.MaxValue, "Owned command exited before native evidence capture")]
    public async Task NativeEvidence_ReportsInvalidOrExitedTargetsWithoutDumpingOthers(
        int processId, string message)
    {
        await using var fixture = new MxcRepositoryTestSupport.TestDirectory();
        var error = await Assert.ThrowsAsync<IOException>(() =>
            NativeTimeoutEvidence.CaptureAsync([processId], "synthetic missing target",
                fixture.Path, CancellationToken.None));
        Assert.Contains(message, error.Message);
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.dmp"));
        if (processId != 0)
            Assert.True(File.Exists(Path.Combine(fixture.Path, "processes.json")));
    }

    [Theory]
    [InlineData("Completed", 3, 3, 0, 0, false, false, 0)]
    [InlineData("Failed", 3, 2, 1, 1, false, false, 1)]
    [InlineData("Failed", 3, 3, 0, 1, false, false, 1)]
    [InlineData("Aborted", 3, 3, 0, 1, false, false, 1)]
    [InlineData("Aborted", 0, 0, 0, 1, false, false, 1)]
    [InlineData("Aborted", 0, 0, 0, 1, true, false, 1)]
    [InlineData("Completed", 3, 3, 0, 0, false, true, 1)]
    public async Task StockRunner_AccountsOnlyEmittedOriginalsAndFailsClosed(
        string outcome, int executed, int passed, int failed, int nativeExit,
        bool benignEmptyMatch, bool buildError, int expectedExit)
    {
        await using var fixture = new MxcRepositoryTestSupport.TestDirectory();
        string Trx(string result, int count, int successes, int failures) =>
            $"""
            <TestRun><Results><UnitTestResult testName="synthetic" outcome="{(failures == 0 ? "Passed" : "Failed")}" /></Results>
            <ResultSummary outcome="{result}"><Counters total="{count}" executed="{count}" passed="{successes}" failed="{failures}" /></ResultSummary></TestRun>
            """;
        var ordinary = Path.Combine(fixture.Path, "ordinary.trx");
        var cold = Path.Combine(fixture.Path, "cold.trx");
        var archive = Path.Combine(fixture.Path, "archival-copy.trx");
        var dump = Path.Combine(fixture.Path, "diagnostic-sentinel.dmp");
        File.WriteAllText(ordinary, Trx(outcome, executed, passed, failed));
        File.WriteAllText(cold, Trx("Completed", 1, 1, 0));
        File.WriteAllText(archive, Trx("Completed", 999, 999, 0));
        File.SetLastWriteTimeUtc(archive, DateTime.UtcNow.AddDays(1));
        File.WriteAllText(dump, "synthetic preservation sentinel, not a native dump");
        var log = Path.Combine(fixture.Path, "synthetic-runner.log");
        var script = Path.Combine(FindRepositoryRoot().FullName, "scripts", "run-tests.ps1");
        var target = Path.Combine(FindRepositoryRoot().FullName,
            "Phantom.Workspaces.Install.Tests", "Phantom.Workspaces.Install.Tests.csproj");
        var harness = Path.Combine(fixture.Path, "synthetic-dotnet-harness.ps1");
        File.WriteAllText(harness, $$"""
            # Synthetic native output for behavioral tests; not an acceptance run.
            function global:dotnet {
                if (($args -join ' ') -like '*FullyQualifiedName!=*') {
                    'Results File: {{ordinary}}'
                    'Results File: {{ordinary}}'
                    {{(benignEmptyMatch ? "'Could not find files for the given pattern'" : "")}}
                    {{(buildError ? "'synthetic.cs: error CS0000: synthetic build error'" : "")}}
                    $global:LASTEXITCODE = {{nativeExit}}
                } else {
                    'Results File: {{cold}}'
                    $global:LASTEXITCODE = 0
                }
            }
            & '{{script}}' -NoBuild -Mode full -TestProject '{{target}}' -TestResultsPath '{{log}}'
            exit $LASTEXITCODE
            """);
        var result = await MxcRepositoryTestSupport.InvokeAsync(
            "pwsh", "-NoProfile", "-NonInteractive", "-File", harness);
        Assert.True(expectedExit == result.ExitCode,
            $"Expected synthetic runner exit {expectedExit}; actual {result.ExitCode}.\n{result.StandardOutput}\n{result.StandardError}");
        var output = File.ReadAllText(log);
        Assert.Contains($"Executed : {executed + 1}", output);
        Assert.Contains($"Passed   : {passed + 1}", output);
        Assert.Contains($"Failed   : {failed}", output);
        Assert.True(File.Exists(dump));
        if (benignEmptyMatch)
            Assert.Contains("dotnet test exited with code 1", result.StandardOutput);
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
