using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Services.AgentSessions;

internal interface IAgentSessionAttachAuthorizer
{
    ValueTask<AgentSessionAuthorizationDecision> AuthorizeAsync(
        TransportPeerIdentity peer,
        AgentSessionAuthorizationRequest request,
        CancellationToken ct = default);
}

internal sealed class AgentSessionAttachAuthorizer : IAgentSessionAttachAuthorizer
{
    private readonly IDataAccessLayer dataAccessLayer;
    private readonly IAgentSessionChildRegistry? childRegistry;

    internal AgentSessionAttachAuthorizer(
        IDataAccessLayer dataAccessLayer,
        IAgentSessionChildRegistry? childRegistry = null)
    {
        this.dataAccessLayer = dataAccessLayer ?? throw new ArgumentNullException(nameof(dataAccessLayer));
        this.childRegistry = childRegistry;
    }

    public async ValueTask<AgentSessionAuthorizationDecision> AuthorizeAsync(
        TransportPeerIdentity peer,
        AgentSessionAuthorizationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        if (peer.UserEntityId is null)
            return Denied;

        var query = await this.dataAccessLayer.QueryAsync(new QueryRequest
        {
            Clauses =
            [
                new TopLevelQueryClause
                {
                    ClauseIdentifier = new QueryClauseIdentifier("agent-sessions"),
                    Clause = new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["agent-session"]) },
                },
                new TopLevelQueryClause
                {
                    ClauseIdentifier = new QueryClauseIdentifier("profiles"),
                    Clause = new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["user-computer-profile"]) },
                },
                new TopLevelQueryClause
                {
                    ClauseIdentifier = new QueryClauseIdentifier("users"),
                    Clause = new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["user"]) },
                },
            ],
            Timestamps = [null],
        }, ct).ConfigureAwait(false);

        var entities = query.Batches.SelectMany(batch => batch.Entities).ToArray();
        var session = entities.FirstOrDefault(entity =>
            entity.Data is JsonElement data
            && ReadString(data, "agent-session-id") == request.AgentSessionId);
        if (session?.Data is not JsonElement sessionData
            || (ReadString(sessionData, "owning-profile-entity-id")
                ?? ReadString(sessionData, "host-profile-entity-id")) is not string owner
            || !string.Equals(owner, request.ExpectedOwningProfileEntityId, StringComparison.OrdinalIgnoreCase)
            || ReadInt64(sessionData, "ownership-generation") != request.ExpectedOwnershipGeneration)
            return Denied;

        var ownerProfile = entities.FirstOrDefault(entity =>
            string.Equals(entity.EntityId.ToString(), owner, StringComparison.OrdinalIgnoreCase));
        if (ownerProfile?.Data is not JsonElement ownerData
            || !ProfileBelongsToUser(ownerData, peer.UserEntityId, entities))
            return Denied;

        if (peer.UserComputerProfileEntityId is { } peerProfileId)
        {
            var peerProfile = entities.FirstOrDefault(entity =>
                string.Equals(entity.EntityId.ToString(), peerProfileId, StringComparison.OrdinalIgnoreCase));
            if (peerProfile?.Data is not JsonElement peerData
                || !ProfileBelongsToUser(peerData, peer.UserEntityId, entities))
                return Denied;
        }

        if (request.Operation == AgentSessionAuthorizationOperation.Takeover)
        {
            if (request.NewOwningProfileEntityId is not { } newOwnerId)
                return Denied;
            var newOwner = entities.FirstOrDefault(entity =>
                string.Equals(entity.EntityId.ToString(), newOwnerId, StringComparison.OrdinalIgnoreCase));
            if (newOwner?.Data is not JsonElement newOwnerData
                || !ProfileBelongsToUser(newOwnerData, peer.UserEntityId, entities))
                return Denied;
        }

        if (request.ChildAgentId is { } childAgentId
            && (this.childRegistry is null
                || !await this.childRegistry.ContainsAsync(
                    request.AgentSessionId,
                    request.ExpectedOwnershipGeneration,
                    childAgentId,
                    ct).ConfigureAwait(false)))
            return Denied;

        return new AgentSessionAuthorizationDecision { IsAllowed = true };
    }

    private static AgentSessionAuthorizationDecision Denied => new() { IsAllowed = false };

    private static bool ProfileBelongsToUser(
        JsonElement profile,
        string userEntityId,
        IReadOnlyCollection<QueryEntitySnapshot> entities)
    {
        if (string.Equals(
                ReadString(profile, "user-entity-id"),
                userEntityId,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!profile.TryGetProperty("user-reference", out var reference)
            || reference.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        var components = reference.EnumerateArray()
            .Select(static value =>
                value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null)
            .ToArray();
        if (components.Length == 0 || components.Any(static value => value is null))
        {
            return false;
        }

        return entities.Any(entity =>
            string.Equals(
                entity.EntityId.ToString(),
                userEntityId,
                StringComparison.OrdinalIgnoreCase)
            && entity.Data is JsonElement user
            && HasEntityName(user, components!));
    }

    private static bool HasEntityName(
        JsonElement entity,
        IReadOnlyList<string?> expected)
        => entity.TryGetProperty("names", out var names)
            && names.ValueKind == JsonValueKind.Array
            && names.EnumerateArray().Any(name =>
                name.ValueKind == JsonValueKind.Array
                && name.GetArrayLength() == expected.Count
                && name.EnumerateArray()
                    .Select(static value => value.GetString())
                    .SequenceEqual(expected, StringComparer.Ordinal));

    private static string? ReadString(JsonElement data, string name)
        => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long? ReadInt64(JsonElement data, string name)
        => data.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : null;
}
