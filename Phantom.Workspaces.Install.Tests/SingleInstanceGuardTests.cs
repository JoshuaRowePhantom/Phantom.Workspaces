using Phantom.Workspaces.Install;

namespace Phantom.Workspaces.Install.Tests;

public sealed class SingleInstanceGuardTests
{
    private static string UniqueKey() => $"test-{Guid.NewGuid():N}";

    [Fact]
    public void Acquire_FirstInstanceIsPrimary_SecondIsSecondary()
    {
        var key = UniqueKey();
        using var primary = SingleInstanceGuard.Acquire(configFilePath: null, explicitInstanceKey: key);
        using var secondary = SingleInstanceGuard.Acquire(configFilePath: null, explicitInstanceKey: key);

        Assert.True(primary.IsPrimaryInstance);
        Assert.False(secondary.IsPrimaryInstance);
    }

    [Fact]
    public void Acquire_DifferentConfigKeysAreBothPrimary()
    {
        using var first = SingleInstanceGuard.Acquire(configFilePath: null, explicitInstanceKey: UniqueKey());
        using var second = SingleInstanceGuard.Acquire(configFilePath: null, explicitInstanceKey: UniqueKey());

        Assert.True(first.IsPrimaryInstance);
        Assert.True(second.IsPrimaryInstance);
    }

    [Fact]
    public async Task SignalActivation_RaisesActivationRequestedOnPrimary()
    {
        var key = UniqueKey();
        using var primary = SingleInstanceGuard.Acquire(configFilePath: null, explicitInstanceKey: key);
        using var secondary = SingleInstanceGuard.Acquire(configFilePath: null, explicitInstanceKey: key);

        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.ActivationRequested += (_, _) => activated.TrySetResult();
        primary.StartActivationListener();

        var delivered = await secondary.SignalActivationAsync(TimeSpan.FromSeconds(30));
        Assert.True(delivered);

        // Event-driven: the success path completes immediately; the timeout only guards a failure.
        using var failSafe = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await activated.Task.WaitAsync(failSafe.Token);
    }
}
