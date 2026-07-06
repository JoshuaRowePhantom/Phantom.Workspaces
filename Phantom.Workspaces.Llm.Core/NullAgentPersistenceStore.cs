using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// An <see cref="IAgentPersistenceStore"/> that discards all writes and returns empty results for
/// all reads. Used for the local display-facade <see cref="AgentChat"/> in remote-execution scenarios
/// (see <c>RemoteTrustedExecutor</c>, <c>ReverseTrustedExecutor</c>) where the remote side owns the
/// authoritative history and the local side must never prepend stored history to outbound messages.
/// </summary>
public sealed class NullAgentPersistenceStore : IAgentPersistenceStore
{
    /// <summary>Shared singleton instance.</summary>
    public static readonly NullAgentPersistenceStore Instance = new();

    private NullAgentPersistenceStore()
    {
    }

    /// <inheritdoc />
    public ValueTask StoreAsync(StoreRequestAgent request, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask<PersistedAgent?> RestoreAsync(
        RestoreRequest request,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<PersistedAgent?>(null);

    /// <inheritdoc />
    public ValueTask<ChatMessage[]> ReadMessagesAsync(
        ReadMessagesRequest request,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Array.Empty<ChatMessage>());

    /// <inheritdoc />
    public ValueTask AddSubAgentLinkAsync(
        string parentSessionId,
        string childSessionId,
        CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AgentSessionId>> ReadSubAgentChildIdsAsync(
        string parentSessionId,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<AgentSessionId>>(Array.Empty<AgentSessionId>());
}
