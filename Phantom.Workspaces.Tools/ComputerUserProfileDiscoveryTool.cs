using System.Text.Json.Nodes;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tools;

public sealed class ComputerUserProfileDiscoveryTool(
    ICurrentExecutionContextProvider? currentExecutionContextProvider = null) : IWorkspaceTool
{
    private readonly ICurrentExecutionContextProvider currentExecutionContextProvider = currentExecutionContextProvider ?? new CurrentExecutionContextProvider();

    public async Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context)
    {
        var computerEntityName = new EntityName("computers", "hostname", this.currentExecutionContextProvider.ComputerName);
        var userEntityName = new EntityName("users", "username", this.currentExecutionContextProvider.UserName);
        var computerUserProfileName = new EntityName(
            "computer-user-profiles",
            computerEntityName.Components[0],
            computerEntityName.Components[1],
            computerEntityName.Components[2],
            userEntityName.Components[0],
            userEntityName.Components[1],
            userEntityName.Components[2]);

        var entityData = new JsonObject
        {
            ["entity-types"] = new JsonArray("user-computer-profile"),
            ["names"] = new JsonArray(new JsonArray(computerUserProfileName.Components.Select(component => (JsonNode)component).ToArray())),
            ["display-name"] = new JsonObject
            {
                ["default"] = $"{this.currentExecutionContextProvider.UserName} @ {this.currentExecutionContextProvider.ComputerName}",
            },
            ["computer-reference"] = new JsonArray(computerEntityName.Components.Select(component => (JsonNode)component).ToArray()),
            ["user-reference"] = new JsonArray(userEntityName.Components.Select(component => (JsonNode)component).ToArray()),
            ["home-directory"] = this.currentExecutionContextProvider.HomeDirectoryPath,
        };

        _ = await WorkspaceToolEntityUtilities.UpsertEntityByPrimaryNameAsync(
            context.DataAccessLayer,
            computerUserProfileName,
            entityData,
            "Discover current computer user profile entity.",
            context.CancellationToken);

        return new WorkspaceToolExecutionResult();
    }
}
