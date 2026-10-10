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

    private sealed class ClockAdvancingDisposable(FakeTimeProvider clock) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            return ValueTask.CompletedTask;
        }
    }
}
