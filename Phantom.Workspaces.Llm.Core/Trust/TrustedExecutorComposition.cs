namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// Composes the single application-wide <see cref="ITrustedExecutorSelector"/> used to choose where an
/// agent runs under a trust profile. The selector is composed of the registry-backed reverse executor
/// (for connected instances reachable over a reverse tunnel) and the local executor (for the local
/// instance), so the running server can route execution to a connected instance or run it locally.
/// </summary>
public static class TrustedExecutorComposition
{
    /// <summary>
    /// Creates the trusted-executor selector for the running application from the reverse-execution
    /// registry. Reverse (registry-backed) execution is preferred for connected remote instances; the
    /// local executor handles the local instance.
    /// </summary>
    public static ITrustedExecutorSelector CreateSelector(ReverseExecutionRegistry reverseExecutionRegistry)
    {
        ArgumentNullException.ThrowIfNull(reverseExecutionRegistry);

        return new TrustedExecutorSelector(
        [
            new ReverseTrustedExecutor(reverseExecutionRegistry),
            new LocalTrustedExecutor(),
        ]);
    }
}
