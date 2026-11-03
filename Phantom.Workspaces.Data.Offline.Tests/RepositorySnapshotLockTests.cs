using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Testing;

namespace Phantom.Workspaces.Data.Offline.Tests;

public sealed class RepositorySnapshotLockTests
{
    [Fact]
    public void GetName_CanonicalEquivalentPaths_UseSameRepositoryLock()
    {
        using var directory = TestPathFactory.CreateIsolatedTempDirectory("snapshot-lock-name");
        Assert.Equal(RepositorySnapshotLock.GetName(directory.Path),
            RepositorySnapshotLock.GetName(System.IO.Path.Combine(directory.Path, ".")));
        if (OperatingSystem.IsWindows())
            Assert.Equal(RepositorySnapshotLock.GetName(directory.Path),
                RepositorySnapshotLock.GetName(directory.Path.ToUpperInvariant()));
    }

    [Fact(Timeout = 30000)]
    public async Task Run_AbandonedOwner_RecoversAndReleasesMutex()
    {
        using var directory = TestPathFactory.CreateIsolatedTempDirectory("snapshot-abandoned");
        using var observer = new Mutex(false, RepositorySnapshotLock.GetName(directory.Path));
        var thread = new Thread(() =>
        {
            using var owner = new Mutex(false, RepositorySnapshotLock.GetName(directory.Path));
            owner.WaitOne();
        });
        thread.Start();
        await Task.Run(thread.Join);
        Assert.Equal(42, await RepositorySnapshotLock.RunAsync(directory.Path, () => 42, CancellationToken.None));
        Assert.Equal(43, await RepositorySnapshotLock.RunAsync(directory.Path, () => 43, CancellationToken.None));
    }

    [Fact(Timeout = 30000)]
    public async Task Run_CancelledWait_DoesNotExecuteOrReleaseAnotherOwnersMutex()
    {
        using var directory = TestPathFactory.CreateIsolatedTempDirectory("snapshot-cancel");
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEvent(false);
        var holder = Task.Run(() =>
        {
            using var mutex = new Mutex(false, RepositorySnapshotLock.GetName(directory.Path));
            mutex.WaitOne();
            acquired.SetResult();
            release.WaitOne();
            mutex.ReleaseMutex();
        });
        await acquired.Task;
        using var cancellation = new CancellationTokenSource();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var waiter = Task.Run(() =>
                RepositorySnapshotLock.Run<int>(directory.Path, () => throw new InvalidOperationException("Must not run"),
                    cancellation.Token, () => waiting.SetResult()));
            await waiting.Task;
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        }
        finally
        {
            release.Set();
            await holder;
        }
        Assert.Equal(1, await RepositorySnapshotLock.RunAsync(directory.Path, () => 1, CancellationToken.None));
    }
}
