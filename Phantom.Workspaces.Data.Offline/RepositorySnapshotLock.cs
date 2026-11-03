using System.Security.Cryptography;
using System.Text;

namespace Phantom.Workspaces.Data.Offline;

internal static class RepositorySnapshotLock
{
    internal static string GetName(string path)
    {
        var canonical = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        if (OperatingSystem.IsWindows())
            canonical = canonical.ToUpperInvariant();
        return "Phantom.Workspaces.Snapshot." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    internal static Task<T> RunAsync<T>(string path, Func<T> operation, CancellationToken cancellationToken)
        => Task.Run(() => Run(path, operation, cancellationToken), cancellationToken);

    // All callers execute synchronous disk operations on a worker. Keeping acquisition, work and
    // release on that thread preserves Mutex ownership; no UI thread or async continuation holds it.
    internal static T Run<T>(string path, Func<T> operation, CancellationToken cancellationToken)
    {
        using var mutex = new Mutex(false, GetName(path));
        var acquired = false;
        try
        {
            try
            {
                acquired = WaitHandle.WaitAny([mutex, cancellationToken.WaitHandle]) == 0;
            }
            catch (AbandonedMutexException exception) when (exception.MutexIndex == 0)
            {
                acquired = true;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!acquired)
                throw new OperationCanceledException(cancellationToken);
            return operation();
        }
        finally
        {
            if (acquired)
                mutex.ReleaseMutex();
        }
    }
}
