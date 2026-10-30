using Microsoft.Extensions.Time.Testing;

namespace Phantom.Workspaces.Tests;

public sealed partial class ProcessIsolatedSplitAcceptanceTests
{
    [Theory]
    [InlineData("eof")]
    [InlineData("nonzero")]
    public async Task RoleUnexpectedExitReportsExpectedProtocolAndNonzeroExit(string behavior)
    {
        await using var role = new RoleProcess("lifetime-probe", behavior);
        var cancellation = TestContext.Current.CancellationToken;
        Assert.Equal("READY probe", await role.NextLineAsync(cancellation));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            role.NextLineAsync(cancellation, "RESULT"));
        Assert.Contains("RESULT", failure.Message);
        if (behavior == "nonzero")
            await Assert.ThrowsAsync<InvalidOperationException>(() => role.WaitForExitAsync(cancellation));
        else
            Assert.Equal(0, await role.WaitForExitAsync(cancellation));
    }

    [Fact]
    public async Task RoleFailedGracefulStopIsNotMistakenForSuccessfulResult()
    {
        await using var role = new RoleProcess("lifetime-probe", "fail-stop");
        var cancellation = TestContext.Current.CancellationToken;
        Assert.Equal("READY probe", await role.NextLineAsync(cancellation));
        await role.SendAsync("STOP", cancellation);
        Assert.Equal("STOP accepted", await role.NextLineAsync(cancellation));
        await Assert.ThrowsAsync<InvalidOperationException>(() => role.WaitForExitAsync(cancellation));
    }

    [Fact]
    public async Task RoleCleanupReportsUnobservedNonzeroExit()
    {
        var role = new RoleProcess("lifetime-probe", "fail-stop");
        var cancellation = TestContext.Current.CancellationToken;
        Assert.Equal("READY probe", await role.NextLineAsync(cancellation));
        await role.SendAsync("STOP", cancellation);
        Assert.Equal("STOP accepted", await role.NextLineAsync(cancellation));
        await role.WaitForTerminationAsync(cancellation);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => role.DisposeAsync().AsTask());
        Assert.Contains("24", failure.Message);
    }

    [Fact]
    public async Task RoleCleanupDeadlineKillsAndReapsOnlyOwnedProcess()
    {
        var clock = new FakeTimeProvider();
        var role = new RoleProcess(clock, "lifetime-probe", "hang-cleanup");
        Assert.Equal("READY probe", await role.NextLineAsync(TestContext.Current.CancellationToken));
        var cleanup = role.DisposeAsync().AsTask();
        await role.CleanupEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<TimeoutException>(() => cleanup);
        Assert.True(role.WasTerminated);
    }
}
