using Phantom.Workspaces.Install;

namespace Phantom.Workspaces.Install.Tests;

public sealed class HealthGateTests
{
    private const string AppRoot = @"C:\sandbox\app";

    private static (InMemoryFileSystem FileSystem, InstallLayout Layout) NewLayout(params string[] versions)
    {
        var fileSystem = new InMemoryFileSystem();
        var layout = new InstallLayout(fileSystem, AppRoot);
        foreach (var version in versions)
        {
            fileSystem.CreateDirectory(layout.GetVersionDirectory(version));
        }

        return (fileSystem, layout);
    }

    [Fact]
    public void EvaluateAndRollback_ReturnsFalseWhenNoPendingState()
    {
        var (fileSystem, layout) = NewLayout("0.1.0");
        layout.RepointCurrent("0.1.0");
        var gate = new HealthGate(fileSystem, layout);

        Assert.False(gate.EvaluateAndRollback());
        Assert.Equal("0.1.0", layout.ResolveCurrentVersion());
    }

    [Fact]
    public void ConfirmHealthy_ClearsPendingSoNoRollbackHappens()
    {
        var (fileSystem, layout) = NewLayout("0.1.0", "0.2.0");
        layout.RepointCurrent("0.2.0");
        var gate = new HealthGate(fileSystem, layout);
        gate.MarkApplied("0.2.0", "0.1.0");

        gate.ConfirmHealthy("0.2.0");

        Assert.Null(gate.Read());
        Assert.False(gate.EvaluateAndRollback());
        Assert.Equal("0.2.0", layout.ResolveCurrentVersion());
    }

    [Fact]
    public void EvaluateAndRollback_RollsBackWhenPendingVersionNeverConfirmed()
    {
        var (fileSystem, layout) = NewLayout("0.1.0", "0.2.0");
        layout.RepointCurrent("0.2.0");
        var gate = new HealthGate(fileSystem, layout);
        gate.MarkApplied("0.2.0", "0.1.0");

        // The 0.2.0 boot never called ConfirmHealthy; the next launch evaluates the gate.
        var rolledBack = gate.EvaluateAndRollback();

        Assert.True(rolledBack);
        Assert.Equal("0.1.0", layout.ResolveCurrentVersion());
        Assert.Null(gate.Read());
    }

    [Fact]
    public void EvaluateAndRollback_DoesNotRollBackWhenPendingIsNoLongerCurrent()
    {
        var (fileSystem, layout) = NewLayout("0.1.0", "0.2.0", "0.3.0");
        layout.RepointCurrent("0.3.0");
        var gate = new HealthGate(fileSystem, layout);
        gate.MarkApplied("0.2.0", "0.1.0");

        Assert.False(gate.EvaluateAndRollback());
        Assert.Equal("0.3.0", layout.ResolveCurrentVersion());
    }
}
