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
}
