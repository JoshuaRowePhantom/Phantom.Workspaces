namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// Selects the appropriate <see cref="ITrustedExecutor"/> for a trust profile and target client
/// instance, enforcing the profile's permitted-computer set in the process.
/// </summary>
public interface ITrustedExecutorSelector
{
    /// <summary>
    /// Selects an executor for the given trust profile and target client instance.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the trust profile does not permit the target instance, or when no registered
    /// executor can run on it.
    /// </exception>
    ITrustedExecutor SelectExecutor(TrustProfile trustProfile, string targetClientInstance);
}
