# Workspace tools design

## Projects

- `Phantom.Workspaces.Tools.Core` contains workspace tool contracts.
- `Phantom.Workspaces.Tools` is the runtime executable host.
- `Phantom.Workspaces.Tools.Test` is an executable for tool integration and diagnostics scenarios.

## Core contract

`IWorkspaceTool` executes with `WorkspaceToolExecutionContext`:

- `IDataAccessLayer`
- `CancellationToken`
- `EntitySnapshot ToolRelationship`
- `EntitySnapshot[] Participants`
- `EntitySnapshot Tool`
- `EntitySnapshot Schedule`

`WorkspaceToolExecutionResult` is currently an empty result type and can be extended as execution output requirements evolve.
