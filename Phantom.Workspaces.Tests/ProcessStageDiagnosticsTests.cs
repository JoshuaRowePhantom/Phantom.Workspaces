using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.ProcessAcceptance;

namespace Phantom.Workspaces.Tests;

public sealed class ProcessStageDiagnosticsTests
{
    [Fact]
    public void DeadlineDiagnostic_IdentifiesCurrentAwaitWithoutCompletingIt()
    {
        var lines = new List<string>();
        var clock = new FakeTimeProvider();
        var diagnostics = new ProcessStageDiagnostics(lines.Add, clock);
        diagnostics.Report("caller-gui-bootstrap");
        clock.Advance(TimeSpan.FromMilliseconds(17));
        diagnostics.Report("caller-gui-initialization");
        clock.Advance(TimeSpan.FromSeconds(90));
        diagnostics.Fail(new OperationCanceledException("private endpoint"));

        Assert.Contains(lines, line => line.Contains("completed caller-gui-bootstrap elapsed-ms 17"));
        Assert.Contains(lines, line => line.Contains("failed caller-gui-initialization elapsed-ms 90000"));
        Assert.DoesNotContain(lines, line => line.Contains("completed caller-gui-initialization"));
        Assert.DoesNotContain(lines, line => line.Contains("private endpoint"));
        Assert.All(lines, line => Assert.True(DateTimeOffset.TryParse(line.Split(' ')[1], out _)));
    }

    [Fact]
    public void RetainedDiagnostics_RedactCredentialsEndpointsAndFixtureIdentities()
    {
        const string entity = "10101010-1010-4010-8010-000000001611";
        var sanitized = ProcessStageDiagnostics.Sanitize(
            $"STAGE caller-gui-initialization {entity} https://private.example/path "
            + "credential-secret fixture-user fixture-host fixture-session",
            "credential-secret", "fixture-user", "fixture-host", "fixture-session");
        Assert.Contains("caller-gui-initialization", sanitized);
        foreach (var secret in new[] { entity, "private.example", "credential-secret",
                     "fixture-user", "fixture-host", "fixture-session" })
            Assert.DoesNotContain(secret, sanitized);
    }

    [Fact]
    public void StageIdentifiers_CannotCarryPrivatePayloads()
    {
        var diagnostics = new ProcessStageDiagnostics(_ => { });
        Assert.Throws<ArgumentException>(() => diagnostics.Report("https://private.example"));
    }

    [Fact]
    public void GitFixturePaths_DoNotInheritDeepBinaryOutputDirectory()
    {
        var path = ProcessStageDiagnostics.GetFixtureDirectory(AppContext.BaseDirectory);
        Assert.EndsWith("TestResults", Path.GetFileName(Path.GetDirectoryName(path)));
        Assert.StartsWith("split-", Path.GetFileName(path));
        Assert.DoesNotContain(Path.Combine("bin", "Debug"), path);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(path))!,
            "Phantom.Workspaces.slnx")));
    }

    [Fact]
    public void DeadlineDiagnostic_ReportsDeepestUncompletedStartupOperation()
    {
        var now = DateTimeOffset.Parse("2026-10-10T01:00:10Z");
        var lines = new[]
        {
            "STARTUP 2026-10-10T01:00:00Z started schema-update thread 8",
            "STARTUP 2026-10-10T01:00:01Z started git-filesystem-update thread 8",
            "STARTUP 2026-10-10T01:00:02Z completed git-filesystem-update elapsed-ms 1000 thread 8",
            "STARTUP 2026-10-10T01:00:03Z started git-commit thread 8",
        };
        Assert.Equal("git-commit; active elapsed-ms 7000",
            ProcessStageDiagnostics.DescribeActiveStartup(lines, now));
        Assert.Equal("schema-update; active elapsed-ms 10000",
            ProcessStageDiagnostics.DescribeActiveStartup(
                lines.Append("STARTUP 2026-10-10T01:00:09Z completed git-commit elapsed-ms 6000 thread 8"), now));
    }

    [Theory]
    [InlineData("caller-window-construction")]
    [InlineData("worker-transport-start")]
    [InlineData("caller-gui-initialization")]
    public async Task OperationalFailure_IsCapturedBeforeCleanupAndCannotBeOverwritten(string stage)
    {
        var clock = new FakeTimeProvider();
        var operations = new List<string>();
        var cleanups = new List<string>();
        var diagnostics = new ProcessStageDiagnostics(operations.Add, clock);
        var cleanup = new ProcessStageDiagnostics(cleanups.Add, clock);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var lifetime = new DiagnosticAsyncLifetime(
                new ClockAdvancingDisposable(clock), cleanup, "shutdown");
            if (stage.EndsWith("construction", StringComparison.Ordinal))
                diagnostics.Run<object>(stage, () =>
                {
                    clock.Advance(TimeSpan.FromMilliseconds(17));
                    throw new InvalidOperationException();
                });
            else
                await diagnostics.RunAsync(stage, () =>
                {
                    clock.Advance(TimeSpan.FromMilliseconds(17));
                    return Task.FromException(new InvalidOperationException());
                });
        });
        diagnostics.Fail(new InvalidOperationException());
        diagnostics.Complete();
        Assert.Single(operations, line => line.Contains($"failed {stage} elapsed-ms 17"));
        Assert.DoesNotContain(operations, line => line.Contains("completed " + stage));
        Assert.DoesNotContain(operations, line => line.Contains("shutdown"));
        Assert.Contains(cleanups, line => line.Contains("completed shutdown elapsed-ms 5000"));
    }

    [Fact]
    public void RetainedArtifacts_ContainOnlySanitizedRoleLogsNotEntityDocumentsOrGitHistory()
    {
        var fixture = ProcessStageDiagnostics.GetFixtureDirectory(AppContext.BaseDirectory);
        var artifact = Path.Combine(Path.GetDirectoryName(fixture)!, "diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(fixture, "caller-logs"));
        Directory.CreateDirectory(Path.Combine(fixture, "entities", ".git", "logs"));
        File.WriteAllText(Path.Combine(fixture, "caller-logs", "process.log"),
            "private-user private-machine credential-secret");
        File.WriteAllText(Path.Combine(fixture, "entities", "private.json"), "private-user private-machine");
        File.WriteAllText(Path.Combine(fixture, "entities", ".git", "logs", "HEAD"), "private-user private-machine");
        ProcessStageDiagnostics.RetainRoleLogs(fixture, artifact,
            "private-user", "private-machine", "credential-secret");

        var log = Assert.Single(Directory.EnumerateFiles(artifact, "*", SearchOption.AllDirectories));
        Assert.Equal(Path.Combine(artifact, "caller-logs", "process.log"), log);
        var retained = File.ReadAllText(log);
        Assert.DoesNotContain("private-user", retained);
        Assert.DoesNotContain("private-machine", retained);
        Assert.DoesNotContain("credential-secret", retained);
        Assert.True(File.Exists(Path.Combine(fixture, "entities", "private.json")));
        Assert.True(File.Exists(Path.Combine(fixture, "entities", ".git", "logs", "HEAD")));
    }

    [Fact]
    public void GuardedOperation_DoesNotFalselyCompleteOrResetAnAlreadyStartedStage()
    {
        var clock = new FakeTimeProvider();
        var lines = new List<string>();
        var diagnostics = new ProcessStageDiagnostics(lines.Add, clock);
        diagnostics.Report("caller-window-construction");
        clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Throws<InvalidOperationException>(() => diagnostics.Run<object>("caller-window-construction", () =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(17));
            throw new InvalidOperationException();
        }));
        Assert.Single(lines, line => line.Contains("started caller-window-construction"));
        Assert.Single(lines, line => line.Contains("failed caller-window-construction elapsed-ms 67"));
        Assert.DoesNotContain(lines, line => line.Contains("completed caller-window-construction"));
    }

    [Theory]
    [InlineData("caller", false)]
    [InlineData("worker", false)]
    [InlineData("caller", true)]
    [InlineData("worker", true)]
    public async Task NestedOwnershipGuards_ClassifyCleanupExceptionsWithoutReplacingOperationalFailure(
        string role, bool operationFails)
    {
        var clock = new FakeTimeProvider();
        var operations = new List<string>();
        var cleanups = new List<string>();
        var diagnostics = new ProcessStageDiagnostics(operations.Add, clock);
        var cleanup = new ProcessStageDiagnostics(cleanups.Add, clock);
        var operationalError = new InvalidOperationException("operational failure");
        var innerCleanupError = new IOException("inner cleanup failure");
        var outerCleanupError = new IOException("outer cleanup failure");
        async Task RunNestedAsync()
        {
            await using var outer = new DiagnosticAsyncLifetime(
                new ThrowingDisposable(clock, outerCleanupError), cleanup, role + "-outer-shutdown");
            try
            {
                await using var inner = new DiagnosticAsyncLifetime(
                    new ThrowingDisposable(clock, innerCleanupError), cleanup, role + "-inner-shutdown");
                try
                {
                    diagnostics.Report(role + "-initialization");
                    clock.Advance(TimeSpan.FromMilliseconds(17));
                    if (operationFails)
                        throw operationalError;
                    diagnostics.Complete();
                }
                catch (Exception error)
                {
                    diagnostics.CaptureFailure(error, cleanup);
                    throw;
                }
            }
            catch (Exception error)
            {
                diagnostics.CaptureFailure(error, cleanup);
                throw;
            }
        }
        var error = await Assert.ThrowsAsync<IOException>(() => RunNestedAsync());
        Assert.Same(outerCleanupError, error);
        diagnostics.CaptureFailure(error, cleanup);
        if (operationFails)
        {
            Assert.Single(operations, line => line.Contains($"failed {role}-initialization elapsed-ms 17"));
            Assert.DoesNotContain(operations, line => line.Contains("completed " + role + "-initialization"));
        }
        else
        {
            Assert.False(diagnostics.HasFailure);
            Assert.DoesNotContain(operations, line => line.Contains(" failed "));
            Assert.Single(operations, line => line.Contains($"completed {role}-initialization elapsed-ms 17"));
        }
        Assert.Contains(cleanups, line => line.Contains($"failed {role}-inner-shutdown elapsed-ms 5000"));
        Assert.DoesNotContain(operations, line => line.Contains("failed none"));
    }

    [Fact]
    public void EarlierCleanupFailure_DoesNotMisclassifyADifferentOperationalException()
    {
        var operations = new List<string>();
        var diagnostics = new ProcessStageDiagnostics(operations.Add);
        var cleanup = new ProcessStageDiagnostics(_ => { });
        cleanup.Fail(new IOException());
        diagnostics.Report("caller-profile-validation");
        diagnostics.CaptureFailure(new InvalidOperationException(), cleanup);
        Assert.Contains(operations, line => line.Contains("failed caller-profile-validation"));
    }

    private sealed class ThrowingDisposable(FakeTimeProvider clock, Exception error) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            return ValueTask.FromException(error);
        }
    }

    private sealed class ClockAdvancingDisposable(FakeTimeProvider clock) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            return ValueTask.CompletedTask;
        }
    }

        [Theory]
        [InlineData("caller-gui-initialization")]
        [InlineData("caller-persisted-session-query")]
        public async Task ResultValidationFailure_IsLatchedInsideLifetimeBeforeGuiTaskPropagation(string stage)
        {
            var clock = new FakeTimeProvider();
            var lines = new List<string>();
            var cleanupLines = new List<string>();
            var diagnostics = new ProcessStageDiagnostics(lines.Add, clock);
            var cleanup = new ProcessStageDiagnostics(cleanupLines.Add, clock);
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task RunGuiAsync()
            {
                await using var lifetime = new DiagnosticAsyncLifetime(
                    new ClockAdvancingDisposable(clock), cleanup, "caller-window-shutdown");
                try
                {
                    var result = await diagnostics.RunAsync(stage, () =>
                    {
                        clock.Advance(TimeSpan.FromMilliseconds(17));
                        return Task.FromResult(Array.Empty<string>());
                    });
                    if (stage == "caller-gui-initialization")
                    {
                        if (result.Contains("fixture-profile") != true)
                            throw new InvalidOperationException("Profile mismatch.");
                    }
                    else
                        _ = result.Single();
                }
                catch (Exception error)
                {
                    diagnostics.Fail(error);
                    throw;
                }
            }

            try { await RunGuiAsync(); }
            catch (Exception error) { finished.SetException(error); }
            await Assert.ThrowsAsync<InvalidOperationException>(() => finished.Task);
            diagnostics.Fail(new InvalidOperationException());
            Assert.Single(lines, line => line.Contains($"failed {stage} elapsed-ms 17"));
            Assert.DoesNotContain(lines, line => line.Contains("completed " + stage));
            Assert.DoesNotContain(lines, line => line.Contains("caller-gui-finish"));
            Assert.Contains(cleanupLines, line => line.Contains("completed caller-window-shutdown elapsed-ms 5000"));
        }
}
