using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Shared resolver that both hosts (the GUI shortcut path and the Copilot / running-agent path) use
/// to build a fully populated <see cref="CurrentSessionContext"/>. Centralizing the user / computer /
/// profile lookups here means the <c>get_current_session</c> tool reports the same identity regardless
/// of which host started the session (issue #1236) — previously the Copilot path constructed a context
/// carrying only the session id, so every other member serialized as <c>null</c>.
/// </summary>
public static class CurrentSessionContextFactory
{
    /// <summary>
    /// Resolves the host's current <c>user</c>, <c>computer</c>, and <c>computer-user-profiles</c>
    /// entities from <paramref name="dataAccessLayer"/> and returns a <see cref="CurrentSessionContext"/>
    /// carrying them alongside <paramref name="agentSessionId"/>. Any entity that cannot be resolved is
    /// left <c>null</c> (the tool renders those members as an explicit JSON null, never dropping them).
    /// </summary>
    public static async Task<CurrentSessionContext> CreateForHostAsync(
        string agentSessionId,
        IDataAccessLayer dataAccessLayer,
        string userName,
        string computerName,
        string effectiveComputerName,
        EntityName? agentDefinitionReference = null,
        CancellationToken cancellationToken = default)
    {
        var userEntityName = new EntityName("users", "username", userName);
        var computerEntityName = new EntityName("computers", "hostname", computerName);
        var profileEntityName = new EntityName(
            "computer-user-profiles",
            "users", "username", userName,
            "computers", "hostname", effectiveComputerName);

        var userComputerProfile = await ResolveEntityAsync(dataAccessLayer, profileEntityName, cancellationToken);
        var user = await ResolveEntityAsync(dataAccessLayer, userEntityName, cancellationToken);
        var computer = await ResolveEntityAsync(dataAccessLayer, computerEntityName, cancellationToken);

        return new CurrentSessionContext
        {
            AgentSessionId = agentSessionId,
            UserComputerProfile = userComputerProfile,
            User = user,
            Computer = computer,
            AgentDefinitionReference = agentDefinitionReference,
        };
    }

    private static async Task<EntitySnapshot?> ResolveEntityAsync(
        IDataAccessLayer dataAccessLayer,
        EntityName entityName,
        CancellationToken cancellationToken)
    {
        var getResult = await dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest { EntityName = entityName },
                ],
            },
            cancellationToken);

        return getResult.Batches
            .SelectMany(static batch => batch.Entities)
            .FirstOrDefault(static entity => entity.Data is not null);
    }
}
