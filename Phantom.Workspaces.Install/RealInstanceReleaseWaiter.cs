namespace Phantom.Workspaces.Install;

/// <summary>
/// The production <see cref="IInstanceReleaseWaiter"/>: it waits on the previous instance's named
/// mutex handle (event-driven, not a busy-wait). When the named mutex no longer exists the lock is
/// already free.
/// </summary>
public sealed class RealInstanceReleaseWaiter : IInstanceReleaseWaiter
{
    private readonly string mutexName;

    /// <summary>Creates a waiter for the instance identified by <paramref name="configFilePath"/>.</summary>
    public RealInstanceReleaseWaiter(string? configFilePath, string? explicitInstanceKey = null)
    {
        this.mutexName = SingleInstanceKey.MutexName(configFilePath, explicitInstanceKey);
    }

    /// <inheritdoc />
    public Task<bool> WaitForReleaseAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Mutex mutex;
        try
        {
            mutex = Mutex.OpenExisting(this.mutexName);
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The lock is not held by anyone; the previous instance has already exited.
            return Task.FromResult(true);
        }

        try
        {
            // WaitOne blocks on the handle until the lock is free or the timeout elapses.
            var acquired = mutex.WaitOne(timeout);
            if (acquired)
            {
                mutex.ReleaseMutex();
            }

            return Task.FromResult(acquired);
        }
        catch (AbandonedMutexException)
        {
            // The owner exited without releasing; the lock is effectively free.
            return Task.FromResult(true);
        }
        finally
        {
            mutex.Dispose();
        }
    }
}
