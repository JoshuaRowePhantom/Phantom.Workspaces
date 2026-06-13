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

/// <summary>
/// Default <see cref="ITrustedExecutorSelector"/> that enforces the trust profile's computer set
/// and chooses among the registered executors (local and remoting).
/// </summary>
public sealed class TrustedExecutorSelector : ITrustedExecutorSelector
{
    private readonly IReadOnlyList<ITrustedExecutor> executors;

    /// <summary>Creates a selector over the supplied executors.</summary>
    public TrustedExecutorSelector(IEnumerable<ITrustedExecutor> executors)
    {
        ArgumentNullException.ThrowIfNull(executors);
        this.executors = [.. executors];
    }

    /// <inheritdoc />
    public ITrustedExecutor SelectExecutor(TrustProfile trustProfile, string targetClientInstance)
    {
        ArgumentNullException.ThrowIfNull(trustProfile);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetClientInstance);

        if (!trustProfile.AllowsClientInstance(targetClientInstance))
        {
            throw new InvalidOperationException(
                $"Trust profile does not permit execution on client instance '{targetClientInstance}'.");
        }

        foreach (var executor in this.executors)
        {
            if (executor.CanExecute(targetClientInstance))
            {
                return executor;
            }
        }

        throw new InvalidOperationException(
            $"No trusted executor is available for client instance '{targetClientInstance}'.");
    }
}
