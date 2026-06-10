using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tools;

public sealed record WorkspaceToolExecutionContext
{
    public required IDataAccessLayer DataAccessLayer { get; init; }

    public required CancellationToken CancellationToken { get; init; }

    public required EntitySnapshot ToolRelationship { get; init; }

    public required EntitySnapshot[] Participants { get; init; }

    public required EntitySnapshot Tool { get; init; }

    public required EntitySnapshot Schedule { get; init; }
}
