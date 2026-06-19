using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tools;

public interface IWorkspaceTool
{
    string ToolType { get; }

    Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context);
}
