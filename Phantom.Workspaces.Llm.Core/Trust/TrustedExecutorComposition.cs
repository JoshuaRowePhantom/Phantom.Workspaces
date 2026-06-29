using System.Collections.Generic;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// Composes the single application-wide <see cref="ITrustedExecutorSelector"/> used to choose where an
/// agent runs under a trust profile. The selector is composed of the registry-backed reverse executor
/// (for connected instances reachable over a reverse tunnel), an optional remote executor (for instances
/// that announced an HTTP endpoint), and the local executor (for the local instance).
/// </summary>
public static class TrustedExecutorComposition
{
    /// <summary>
    /// Creates the trusted-executor selector for the running application from the reverse-execution
    /// registry. Reverse (WebSocket) execution is preferred for connected remote instances; the
    /// optional <paramref name="remoteExecutor"/> handles instances that announced an HTTP endpoint;
    /// the local executor handles the local instance.
    /// </summary>
    /// <param name="reverseExecutionRegistry">The reverse-connection registry (inbound WebSocket connections).</param>
    /// <param name="remoteExecutor">
    /// Optional executor for dynamically-registered remote HTTP endpoints (e.g. a
    /// <c>DynamicRemoteTrustedExecutor</c> backed by a <c>RemoteExecutionRegistry</c>). When
    /// <see langword="null"/>, outbound HTTP remote execution is not available through this selector.
    /// </param>
    public static ITrustedExecutorSelector CreateSelector(
        ReverseExecutionRegistry reverseExecutionRegistry,
        ITrustedExecutor? remoteExecutor = null)
    {
        ArgumentNullException.ThrowIfNull(reverseExecutionRegistry);

        var executors = new List<ITrustedExecutor>
        {
            new ReverseTrustedExecutor(reverseExecutionRegistry),
        };

        if (remoteExecutor is not null)
        {
            executors.Add(remoteExecutor);
        }

        executors.Add(new LocalTrustedExecutor());

        return new TrustedExecutorSelector(executors);
    }
}
