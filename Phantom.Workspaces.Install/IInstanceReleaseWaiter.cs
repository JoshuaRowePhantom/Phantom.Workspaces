namespace Phantom.Workspaces.Install;

/// <summary>
/// Waits for the previous instance to release its single-instance lock before the
/// <c>--apply-update</c> shim repoints <c>current</c>. The seam lets the apply flow be unit-tested
/// without real processes/handles.
/// </summary>
public interface IInstanceReleaseWaiter
{
    /// <summary>
    /// Waits (bounded by <paramref name="timeout"/>) for the lock to become free. Returns
    /// <c>true</c> once released, <c>false</c> on timeout.
    /// </summary>
    Task<bool> WaitForReleaseAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}
