using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tools;

public interface IWorkspaceTool
{
    Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context);
}
