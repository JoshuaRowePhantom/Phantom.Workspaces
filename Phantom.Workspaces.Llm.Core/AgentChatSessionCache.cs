using System.Collections.Concurrent;
using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Caches one <see cref="AgentChat"/> per <c>AgentSessionId</c> and routes single-turn requests to
/// the cached instance so stateful <see cref="IChatClient"/> implementations (such as
/// <c>CopilotSdkChatClient</c>) can maintain their session across turns.
/// </summary>
/// <remarks>
/// The cache is the authoritative owner of each <see cref="AgentChat"/>: it creates, reuses, and
/// finally disposes them. Each cached <see cref="AgentChat"/> carries a real
/// <see cref="Phantom.Workspaces.Llm.Interfaces.IAgentPersistenceStore"/> so history and SDK
/// session state survive remote restarts. Dispose the cache (e.g. on server shutdown) to release
/// all cached sessions.
/// </remarks>
public sealed class AgentChatSessionCache : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Task<AgentChat>> sessions =
        new(StringComparer.Ordinal);

    private readonly AgentServices? defaultServices;
    private int disposed;

    /// <summary>
    /// Creates a cache with an optional set of default <see cref="AgentServices"/> applied to
    /// every new session (e.g. a shared persistence store or logger factory).
    /// </summary>
    public AgentChatSessionCache(AgentServices? defaultServices = null)
    {
        this.defaultServices = defaultServices;
    }

    /// <summary>
    /// Runs a single turn on the <see cref="AgentChat"/> for <paramref name="sessionId"/>,
    /// creating a new one if none exists, and streams the <see cref="ChatResponseUpdate"/>s back.
    /// </summary>
    public async IAsyncEnumerable<ChatResponseUpdate> RunTurnAsync(
        AgentChatTurnRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var chat = await this.GetOrCreateAsync(request, cancellationToken).ConfigureAwait(false);

        await foreach (var update in chat.RunSingleTurnAsync(request.Messages, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private Task<AgentChat> GetOrCreateAsync(
        AgentChatTurnRequest request,
        CancellationToken cancellationToken)
    {
        if (this.sessions.TryGetValue(request.AgentSessionId, out var existingTask))
        {
            return existingTask;
        }

        var newTask = this.sessions.GetOrAdd(
            request.AgentSessionId,
            _ => CreateSessionAsync(request, cancellationToken));

        return newTask;
    }

    private async Task<AgentChat> CreateSessionAsync(
        AgentChatTurnRequest request,
        CancellationToken cancellationToken)
    {
        var services = this.defaultServices;
        return await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = PhantomAgentSchema.AgentDefinitionFromJson(request.AgentDefinitionJson),
            AgentSessionId = request.AgentSessionId,
            AgentServices = services,
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            return;
        }

        var sessionTasks = this.sessions.Values.ToArray();
        this.sessions.Clear();

        foreach (var task in sessionTasks)
        {
            AgentChat? chat = null;
            try
            {
                chat = await task.ConfigureAwait(false);
            }
            catch
            {
                // Session creation may have failed; nothing to dispose.
            }

            if (chat is not null)
            {
                await chat.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
