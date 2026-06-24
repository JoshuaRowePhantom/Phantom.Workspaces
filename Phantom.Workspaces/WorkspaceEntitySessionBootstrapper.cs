using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Tools;

namespace Phantom.Workspaces;

internal static class WorkspaceEntitySessionBootstrapper
{
    public static async Task<WorkspaceEntitySession> InitializeAsync(
        IDataAccessLayer dataAccessLayer,
        string? userComputerProfileOverride = null,
        CancellationToken cancellationToken = default)
    {
        var currentExecutionContextProvider = new CurrentExecutionContextProvider(userComputerProfileOverride);
        var userEntityName = new EntityName("users", "username", currentExecutionContextProvider.UserName);
        var computerEntityName = new EntityName("computers", "hostname", currentExecutionContextProvider.ComputerName);
        var profileComputerEntityName = new EntityName("computers", "hostname", currentExecutionContextProvider.EffectiveComputerName);
        var userComputerProfileEntityName = new EntityName(
            "computer-user-profiles",
            userEntityName.Components[0],
            userEntityName.Components[1],
            userEntityName.Components[2],
            profileComputerEntityName.Components[0],
            profileComputerEntityName.Components[1],
            profileComputerEntityName.Components[2]);

        var userDiscoveryTool = new UserDiscoveryTool(currentExecutionContextProvider);
        await userDiscoveryTool.ExecuteAsync(CreateExecutionContext(dataAccessLayer, cancellationToken));
        var computerDiscoveryTool = new ComputerDiscoveryTool(currentExecutionContextProvider);
        await computerDiscoveryTool.ExecuteAsync(CreateExecutionContext(dataAccessLayer, cancellationToken));
        var computerUserProfileDiscoveryTool = new ComputerUserProfileDiscoveryTool(currentExecutionContextProvider);
        await computerUserProfileDiscoveryTool.ExecuteAsync(CreateExecutionContext(dataAccessLayer, cancellationToken));

        var userEntity = await RequireEntityByNameAsync(dataAccessLayer, userEntityName, cancellationToken);
        var computerEntity = await RequireEntityByNameAsync(dataAccessLayer, computerEntityName, cancellationToken);
        var userComputerProfileEntity = await RequireEntityByNameAsync(dataAccessLayer, userComputerProfileEntityName, cancellationToken);
        return new WorkspaceEntitySession
        {
            UserEntityId = userEntity.EntityId,
            ComputerEntityId = computerEntity.EntityId,
            UserComputerProfileEntityId = userComputerProfileEntity.EntityId,
        };
    }

    private static WorkspaceToolExecutionContext CreateExecutionContext(
        IDataAccessLayer dataAccessLayer,
        CancellationToken cancellationToken)
    {
        var placeholder = CreatePlaceholderEntitySnapshot();
        return new WorkspaceToolExecutionContext
        {
            DataAccessLayer = dataAccessLayer,
            CancellationToken = cancellationToken,
            CurrentComputerEntity = placeholder,
            CurrentUserEntity = placeholder,
            CurrentComputerUserProfileEntity = placeholder,
            ToolRelationship = placeholder,
            Participants = [placeholder],
            Tool = placeholder,
            Schedule = placeholder,
        };
    }

    private static EntitySnapshot CreatePlaceholderEntitySnapshot()
    {
        using var placeholderDocument = JsonDocument.Parse(
            """
            {
              "entity-id": "00000000-0000-0000-0000-000000000000",
              "entity-types": ["entity"],
              "names": [["placeholder"]]
            }
            """);
        return new EntitySnapshot
        {
            EntityId = new EntityId(Guid.Empty),
            ModifiedTime = new Timestamp(DateTimeOffset.UnixEpoch, "0"),
            Data = placeholderDocument.RootElement.Clone(),
            Relationships = [],
        };
    }

    private static async Task<EntitySnapshot> RequireEntityByNameAsync(
        IDataAccessLayer dataAccessLayer,
        EntityName entityName,
        CancellationToken cancellationToken)
    {
        var getResult = await dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityName = entityName,
                    },
                ],
            },
            cancellationToken);
        var entity = getResult.Batches.SelectMany(static batch => batch.Entities).FirstOrDefault();
        if (entity is null)
        {
            throw new InvalidOperationException($"Failed to discover workspace session entity '{string.Join("/", entityName.Components)}'.");
        }

        return entity;
    }
}
