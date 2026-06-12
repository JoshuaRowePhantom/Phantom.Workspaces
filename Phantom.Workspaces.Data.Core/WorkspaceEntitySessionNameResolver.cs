using System.Text.Json;

namespace Phantom.Workspaces.Data;

internal readonly record struct ResolvedWorkspaceEntitySessionNames(
    IReadOnlyCollection<EntityName> UserEntityNames,
    IReadOnlyCollection<EntityName> ComputerEntityNames,
    IReadOnlyCollection<EntityName> UserComputerProfileEntityNames);

internal sealed class WorkspaceEntitySessionNameResolver
{
    private readonly IDataAccessLayer dataAccessLayer;
    private readonly WorkspaceEntitySession workspaceEntitySession;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ResolvedWorkspaceEntitySessionNames? cachedNames;

    public WorkspaceEntitySessionNameResolver(
        IDataAccessLayer dataAccessLayer,
        WorkspaceEntitySession workspaceEntitySession)
    {
        this.dataAccessLayer = dataAccessLayer;
        this.workspaceEntitySession = workspaceEntitySession;
    }

    public bool HasMetaVariables(
        EntityName? entityName)
    {
        if (entityName is not EntityName value)
        {
            return false;
        }

        return value.Components.Any(IsMetaVariableComponent);
    }

    public IReadOnlyCollection<EntityName> RewriteMetaVariables(
        EntityName entityName,
        ResolvedWorkspaceEntitySessionNames resolvedNames)
    {
        var rewritten = new List<EntityName> { EntityName.Root };
        foreach (var component in entityName.Components)
        {
            if (string.Equals(component, WorkspaceEntityMetaVariables.User, StringComparison.Ordinal))
            {
                rewritten = ExpandByNames(rewritten, resolvedNames.UserEntityNames);
                continue;
            }

            if (string.Equals(component, WorkspaceEntityMetaVariables.Computer, StringComparison.Ordinal))
            {
                rewritten = ExpandByNames(rewritten, resolvedNames.ComputerEntityNames);
                continue;
            }

            if (string.Equals(component, WorkspaceEntityMetaVariables.UserProfile, StringComparison.Ordinal))
            {
                rewritten = ExpandByNames(rewritten, resolvedNames.UserComputerProfileEntityNames);
                continue;
            }

            rewritten = AppendComponent(rewritten, component);
        }

        return rewritten.Distinct().ToArray();
    }

    public async Task<ResolvedWorkspaceEntitySessionNames> GetResolvedNamesAsync(
        CancellationToken cancellationToken = default)
    {
        if (this.cachedNames is ResolvedWorkspaceEntitySessionNames names)
        {
            return names;
        }

        await this.gate.WaitAsync(cancellationToken);
        try
        {
            if (this.cachedNames is ResolvedWorkspaceEntitySessionNames cachedNames)
            {
                return cachedNames;
            }

            var getResult = await this.dataAccessLayer.GetAsync(
                new GetRequest
                {
                    Entities =
                    [
                        new GetEntityRequest
                        {
                            EntityId = this.workspaceEntitySession.UserEntityId,
                        },
                        new GetEntityRequest
                        {
                            EntityId = this.workspaceEntitySession.ComputerEntityId,
                        },
                        new GetEntityRequest
                        {
                            EntityId = this.workspaceEntitySession.UserComputerProfileEntityId,
                        },
                    ],
                },
                cancellationToken);

            var entitiesById = getResult.Batches
                .SelectMany(static batch => batch.Entities)
                .ToDictionary(static entity => entity.EntityId);
            if (!entitiesById.TryGetValue(this.workspaceEntitySession.UserEntityId, out var userEntity)
                || !TryReadAllEntityNames(userEntity.Data, out var userEntityNames))
            {
                throw new InvalidOperationException("Workspace entity session user entity does not have names.");
            }

            if (!entitiesById.TryGetValue(this.workspaceEntitySession.ComputerEntityId, out var computerEntity)
                || !TryReadAllEntityNames(computerEntity.Data, out var computerEntityNames))
            {
                throw new InvalidOperationException("Workspace entity session computer entity does not have names.");
            }

            if (!entitiesById.TryGetValue(this.workspaceEntitySession.UserComputerProfileEntityId, out var userComputerProfileEntity)
                || !TryReadAllEntityNames(userComputerProfileEntity.Data, out var userComputerProfileEntityNames))
            {
                throw new InvalidOperationException("Workspace entity session user computer profile entity does not have names.");
            }

            var resolvedNames = new ResolvedWorkspaceEntitySessionNames(
                userEntityNames,
                computerEntityNames,
                userComputerProfileEntityNames);
            this.cachedNames = resolvedNames;
            return resolvedNames;
        }
        finally
        {
            this.gate.Release();
        }
    }

    private static bool IsMetaVariableComponent(
        string component)
    {
        return string.Equals(component, WorkspaceEntityMetaVariables.User, StringComparison.Ordinal)
            || string.Equals(component, WorkspaceEntityMetaVariables.Computer, StringComparison.Ordinal)
            || string.Equals(component, WorkspaceEntityMetaVariables.UserProfile, StringComparison.Ordinal);
    }

    private static List<EntityName> ExpandByNames(
        IEnumerable<EntityName> currentNames,
        IReadOnlyCollection<EntityName> replacementNames)
    {
        var expandedNames = new List<EntityName>();
        foreach (var currentName in currentNames)
        {
            foreach (var replacementName in replacementNames)
            {
                expandedNames.Add(new EntityName([.. currentName.Components, .. replacementName.Components]));
            }
        }

        return expandedNames;
    }

    private static List<EntityName> AppendComponent(
        IEnumerable<EntityName> currentNames,
        string component)
    {
        return currentNames
            .Select(currentName => new EntityName([.. currentName.Components, component]))
            .ToList();
    }

    internal static bool TryReadAllEntityNames(
        JsonElement? entityData,
        out IReadOnlyCollection<EntityName> entityNames)
    {
        entityNames = [];
        if (entityData is not JsonElement entityDataElement
            || !entityDataElement.TryGetProperty("names", out var names)
            || names.ValueKind != JsonValueKind.Array
            || names.GetArrayLength() == 0)
        {
            return false;
        }

        var parsedEntityNames = new List<EntityName>();
        foreach (var nameElement in names.EnumerateArray())
        {
            var parsedName = nameElement.TryReadEntityName();
            if (parsedName is not null)
            {
                parsedEntityNames.Add(parsedName.Value);
            }
        }

        if (parsedEntityNames.Count == 0)
        {
            return false;
        }

        entityNames = parsedEntityNames;
        return true;
    }
}
