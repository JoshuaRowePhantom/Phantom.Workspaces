using System.Text.Json.Nodes;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tools;

public sealed class ComputerDiscoveryTool(
    ICurrentExecutionContextProvider? currentExecutionContextProvider = null) : IWorkspaceTool
{
    private readonly ICurrentExecutionContextProvider currentExecutionContextProvider = currentExecutionContextProvider ?? new CurrentExecutionContextProvider();

    public string ToolType => "computer-discovery";

    public async Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context)
    {
        var computerName = this.currentExecutionContextProvider.ComputerName;
        var computerEntityName = new EntityName("computers", "hostname", computerName);

        var entityData = new JsonObject
        {
            ["entity-types"] = new JsonArray("computer"),
            ["names"] = new JsonArray(new JsonArray("computers", "hostname", computerName)),
            ["display-name"] = new JsonObject
            {
                ["default"] = computerName,
            },
            ["os"] = this.currentExecutionContextProvider.OperatingSystemName,
        };

        _ = await WorkspaceToolEntityUtilities.UpsertEntityByPrimaryNameAsync(
            context.DataAccessLayer,
            computerEntityName,
            entityData,
            "Discover current computer entity.",
            context.CancellationToken);

        return new WorkspaceToolExecutionResult();
    }
}
