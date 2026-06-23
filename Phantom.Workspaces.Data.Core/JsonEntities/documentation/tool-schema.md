# Tool Entity Schema

A `tool` entity represents a scheduled background task that can be executed on a user-computer-profile or other target entity.

## Schema

**Schema ID**: `https://schemas.workspaces.phantom.to/workspaces/data/core/tool.json`

## Expected JSON Shape

```json
{
  "entity-id": "<guid>",
  "entity-types": ["tool"],
  "$schema": "https://schemas.workspaces.phantom.to/workspaces/data/core/tool.json",
  "names": [
    ["tools", "<tool-name>"]
  ],
  "display-name": {
    "default": "<display name>"
  },
  "content": {
    "default": {
      "mime-type": "text/markdown",
      "text": "<tool description and usage>"
    }
  },
  "tool-type": "<tool-type-discriminator>"
}
```

## Properties

### tool-type (required)

The `tool-type` property identifies the C# implementation class that executes this tool. This value is used by `ScheduledToolRegistry` to dispatch tool execution to the correct `IWorkspaceTool` implementation.

**Examples**:
- `"git-workspace-scan"` → `GitWorkspaceScanTool`
- `"entity-classifier"` → `EntityClassifierTool`
- `"vector-indexer"` → `VectorIndexerTool`
- `"copilot-session-discovery"` → `CopilotSessionDiscoveryTool`

### configuration (optional)

The `configuration` property holds tool-specific configuration data. The structure varies by tool-type:

**GitWorkspaceScanTool**:

Works out of the box with no configuration — it scans **all local fixed drives** for Git
repositories. The scan can optionally be narrowed/bounded with **top-level** properties (not nested
under `configuration`):

```json
{
  "tool-type": "git-workspace-scan",
  "scan-roots": ["C:\\dev", "D:\\work"],
  "max-depth": 6
}
```

- `scan-root` (string) or `scan-roots` (array of strings): directories to scan instead of all local
  drives. Omit both to scan every local fixed drive.
- `max-depth` (number, default 6): how deep the scan descends.

**EntityClassifierTool**:
```json
{
  "tool-type": "entity-classifier",
  "configuration": {
    "classifier-prompt": "Classify entities and apply interests...",
    "batch-size": 50
  }
}
```

**VectorIndexerTool**:
```json
{
  "tool-type": "vector-indexer",
  "configuration": {
    "batch-size": 100
  }
}
```

**CopilotSessionDiscoveryTool**:
```json
{
  "tool-type": "copilot-session-discovery",
  "configuration": {
    "session-state-root": "~/.copilot/session-state"
  }
}
```

## Naming Conventions

Tool entities should use the naming pattern:
```json
"names": [
  ["tools", "<descriptive-name>"]
]
```

Examples:
- `["tools", "git-workspace-scan"]`
- `["tools", "entity-classifier"]`
- `["tools", "vector-indexer"]`
- `["tools", "copilot-session-discovery"]`

## Scheduling Tools

Tools are scheduled by creating a `tool-relationship` entity that links:
1. A **tool entity** (via `participants.tool`)
2. One or more **schedule entities** (via `participants.schedule` array)
3. One or more **target entities** (via `participants.target` array)

Example tool-relationship entity:
```json
{
  "entity-id": "<guid>",
  "entity-types": ["relationship", "tool-relationship"],
  "names": [
    ["relationship", "<entity-id>"]
  ],
  "participants": {
    "tool": "<tool-entity-id>",
    "schedule": ["<schedule-entity-id>"],
    "target": ["<profile-entity-id>"]
  }
}
```

When the `ScheduledToolHost` discovers this relationship, it:
1. Evaluates the linked schedule entity to determine if execution is due
2. Resolves the tool implementation via `ScheduledToolRegistry`
3. Calls `IWorkspaceTool.ExecuteAsync()` with rich context
4. Writes execution results to a `tool-execution-result` entity

## Tool Execution Context

When a tool executes, it receives a `WorkspaceToolExecutionContext` with:
- `DataAccessLayer` - for reading/writing entities
- `CancellationToken` - for cancellation support
- `CurrentComputerEntity` - the host computer
- `CurrentUserEntity` - the current user
- `CurrentComputerUserProfileEntity` - the host profile
- `ToolRelationship` - the relationship entity that triggered execution
- `Participants` - array of participant entity snapshots
- `Tool` - the tool entity snapshot (including configuration)
- `Schedule` - the schedule entity snapshot

## LLM Configuration Guidance

When configuring tools for a user or profile:

1. **Determine appropriate tool-type** based on user needs:
   - Git repository discovery → `git-workspace-scan`
   - Entity organization → `entity-classifier`
   - Semantic search → `vector-indexer`
   - Copilot session tracking → `copilot-session-discovery`

2. **Configure tool-specific parameters**:
   - For git-workspace-scan: Ask for development directory paths
   - For entity-classifier: Provide classification prompt with clear instructions
   - For vector-indexer: Use defaults unless high-volume requires tuning
   - For copilot-session-discovery: Use defaults unless non-standard location

3. **Create or select a schedule entity** with appropriate frequency:
   - Frequent (2-5 min): vector-indexer, copilot-session-discovery
   - Moderate (10-15 min): git-workspace-scan, entity-classifier
   - Custom: based on user requirements

4. **Create the tool-relationship** linking tool, schedule(s), and target profile

5. **Verify execution** by checking tool-execution-result entities

## Common Tools

### Git Workspace Scan

Discovers git repositories in a directory tree and creates `git` entities.

**When to use**: User wants to track their git repositories in the workspace.

**Out of the box**: With no configuration it scans **all local fixed drives**, so simply enabling the
tool (creating a `tool-relationship` to a schedule and profile) is enough — the tool entity does not
need to be edited.

**Optional configuration** (top-level properties on the tool entity, not nested under
`configuration`):
- `scan-root`: a single path to scan instead of all drives (e.g. `C:\\dev`, `/home/user/projects`)
- `scan-roots`: an array of paths to scan instead of all drives
- `max-depth`: how deep to recurse (default: 6)

### Entity Classifier

Runs LLM against entities to automatically apply interests and relationships.

**When to use**: User wants automatic organization of tasks, documents, and other entities.

**Configuration**:
- `classifier-prompt`: Detailed classification instructions
- `batch-size`: Entities per execution (default: 50)

### Vector Indexer

Maintains the vector embedding index for semantic search.

**When to use**: User wants to search entities by meaning/similarity.

**Configuration**:
- `batch-size`: Entities per execution (default: 100)

### Copilot Session Discovery

Discovers local GitHub Copilot CLI sessions and surfaces them as agent-definitions.

**When to use**: User wants to see their copilot sessions in the workspace.

**Configuration**:
- `session-state-root`: Path override (usually use default)

## Relationship Patterns

### Tool → Schedule → Profile

The most common pattern is scheduling a tool on a single profile with a single schedule:

```
tool-relationship {
  tool: <tool-entity-id>
  schedule: [<schedule-entity-id>]
  target: [<profile-entity-id>]
}
```

### Tool → Multiple Schedules → Profile

A tool can have multiple schedules (e.g., frequent during work hours, rare at night):

```
tool-relationship {
  tool: <tool-entity-id>
  schedule: [<daytime-schedule-id>, <night-schedule-id>]
  target: [<profile-entity-id>]
}
```

### Tool → Schedule → Multiple Targets

A tool can run on multiple profiles with the same schedule:

```
tool-relationship {
  tool: <tool-entity-id>
  schedule: [<schedule-entity-id>]
  target: [<profile1-id>, <profile2-id>]
}
```

## Disabling a Tool

To disable a tool on a profile, delete the tool-relationship entity. Do not modify the tool or schedule entities - they remain available for future use or use by other relationships.

## Troubleshooting

Check `tool-execution-result` entities for execution history and error messages:

```json
{
  "entity-types": ["tool-execution-result"],
  "names": [
    ["tool-execution-results", "<profile-name>", "<tool-type>", "<timestamp>"]
  ],
  "tool-type": "<tool-type>",
  "success": false,
  "content": {
    "default": {
      "text": "<error message>"
    }
  }
}
```
