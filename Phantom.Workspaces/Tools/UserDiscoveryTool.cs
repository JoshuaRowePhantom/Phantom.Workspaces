using System.Text.Json.Nodes;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tools;

public sealed class UserDiscoveryTool(
    ICurrentExecutionContextProvider? currentExecutionContextProvider = null) : IWorkspaceTool
{
    private readonly ICurrentExecutionContextProvider currentExecutionContextProvider = currentExecutionContextProvider ?? new CurrentExecutionContextProvider();

    public string ToolType => "user-discovery";

    public async Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context)
    {
        var userName = this.currentExecutionContextProvider.UserName;
        var userEntityName = new EntityName("users", "username", userName);

        var entityData = new JsonObject
        {
            ["entity-types"] = new JsonArray("user"),
            ["names"] = new JsonArray(new JsonArray("users", "username", userName)),
            ["display-name"] = new JsonObject
            {
                ["default"] = userName,
            },
        };

        _ = await WorkspaceToolEntityUtilities.UpsertEntityByPrimaryNameAsync(
            context.DataAccessLayer,
            userEntityName,
            entityData,
            "Discover current user entity.",
            context.CancellationToken);

        return new WorkspaceToolExecutionResult();
    }
}
