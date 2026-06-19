# Tool Entity Design Document

## Overview

This document details the design for creating tool entities, their JSON schema, and a tools view that displays scheduled tools and their execution status.

## Implementation Notes

**Key Findings from Existing Infrastructure:**

1. **Schedule entities already exist**: Use existing `schedule.json` schema - schedules are separate entities, not inline properties
2. **Tool relationships already exist**: Use existing `tool-relationship.json` - links tools, schedules, and targets
3. **Enablement via relationship existence**: A tool is "enabled" on a profile when a `tool-relationship` exists; disabled by deleting the relationship
4. **Views use standard view model**: Tools view should use existing view infrastructure, not a custom ViewModel
5. **Single unified tool interface**: All tools (bootstrap and scheduled) use `IWorkspaceTool` interface with `ToolType` property for runtime dispatch

## 1. Tool JSON Schema and Entity Type (define-tool-schema)

### tool.json Schema

**Location**: `Phantom.Workspaces.Data.Core/JsonSchemas/tool.json`

```json
{
  "$id": "https://schemas.workspaces.phantom.to/workspaces/data/core/tool.json",
  "description": "Schema for tool entities that represent scheduled background tasks and their configuration.",
  "allOf": [
    {
      "$ref": "entity.json"
    }
  ],
  "type": "object",
  "properties": {
    "entity-types": {
      "type": "array",
      "contains": {
        "const": "tool"
      },
      "description": "Must contain 'tool'."
    },
    "tool-type": {
      "type": "string",
      "description": "Identifies the implementation class (e.g., 'GitWorkspaceScanTool', 'VectorIndexerTool')."
    },
    "configuration": {
      "type": "object",
      "description": "Tool-specific configuration settings.",
      "additionalProperties": true
    }
  },
  "required": [
    "entity-types",
    "tool-type"
  ]
}
```

**Note**: Schedules are separate entities linked via `tool-relationship`, not embedded in tool entities.

### tool-entity-type.json Entity Definition

**Location**: `Phantom.Workspaces.Data.Core/JsonEntities/schema-definitions/tool-entity-type.json`

```json
{
  "entity-id": "<generated-guid>",
  "entity-types": [
    "entity-type",
    "note"
  ],
  "names": [
    [
      "json-schemas",
      "https://schemas.workspaces.phantom.to/workspaces/data/core/tool.json"
    ],
    [
      "entity-types",
      "tool"
    ]
  ],
  "display-name": {
    "default": "Tool Schema"
  },
  "default-name-prefixes": [
    [
      "tools"
    ]
  ],
  "content": {
    "default": {
      "mime-type": "text/markdown",
      "url": "/JsonEntities/documentation/tool-schema.md"
    }
  },
  "schema": {
    "$ref": "/JsonSchemas/tool.json"
  }
}
```

### scheduled-tool.json Relationship Type Schema

**Location**: `Phantom.Workspaces.Data.Core/JsonSchemas/scheduled-tool.json`

```json
{
  "$id": "https://schemas.workspaces.phantom.to/workspaces/data/core/scheduled-tool.json",
  "description": "Schema for scheduled-tool relationship entities connecting user-computer-profiles to tools.",
  "allOf": [
    {
      "$ref": "relationship.json"
    }
  ],
  "type": "object",
  "properties": {
    "entity-types": {
      "type": "array",
      "contains": {
        "const": "scheduled-tool"
      },
      "description": "Must contain 'relationship' and 'scheduled-tool'."
    },
    "schedule": {
      "$ref": "tool.json#/$defs/schedule-definition",
      "description": "Schedule override for this specific tool on this profile."
    },
    "last-run": {
      "type": "string",
      "format": "date-time",
      "description": "Timestamp of last successful execution."
    },
    "last-result": {
      "type": "string",
      "enum": ["success", "failure", "skipped"],
      "description": "Result of last execution."
    }
  },
  "required": [
    "entity-types"
  ]
}
```

## 2. Tool Entity Definitions (define-tool-entities)

Create four tool entities for existing scheduled tools.

### 2.1 GitWorkspaceScanTool Entity

**Location**: `Phantom.Workspaces.Data.Core/JsonEntities/defaults/tools/git-workspace-scan-tool.json`

```json
{
  "entity-id": "<generated-guid>",
  "entity-types": ["tool"],
  "$schema": "https://schemas.workspaces.phantom.to/workspaces/data/core/tool.json",
  "names": [
    ["tools", "git-workspace-scan"]
  ],
  "display-name": {
    "default": "Git Workspace Scan Tool"
  },
  "content": {
    "default": {
      "mime-type": "text/markdown",
      "text": "# Git Workspace Scan Tool\n\nScans configured directories for git repositories and creates workspace entities.\n\n## Configuration\n\n- **scan-roots**: Array of directory paths to scan for git repositories\n- **create-worktree-entities**: Whether to create git-worktree entities for discovered repositories (default: true)\n- **create-workspace-entities**: Whether to create workspace entities (default: true)\n\n## Schedule\n\nDefault: Runs every 5 minutes when enabled."
    }
  },
  "tool-type": "GitWorkspaceScanTool",
  "configuration": {
    "scan-roots": [],
    "create-worktree-entities": true,
    "create-workspace-entities": true
  },
  "default-schedule": {
    "enabled": false,
    "interval": "5m",
    "run-on-startup": true
  }
}
```

### 2.2 EntityClassifierTool Entity

**Location**: `Phantom.Workspaces.Data.Core/JsonEntities/defaults/tools/entity-classifier-tool.json`

```json
{
  "entity-id": "<generated-guid>",
  "entity-types": ["tool"],
  "$schema": "https://schemas.workspaces.phantom.to/workspaces/data/core/tool.json",
  "names": [
    ["tools", "entity-classifier"]
  ],
  "display-name": {
    "default": "Entity Classifier Tool"
  },
  "content": {
    "default": {
      "mime-type": "text/markdown",
      "text": "# Entity Classifier Tool\n\nRuns an LLM agent against entities to classify them and apply interests/relationships.\n\n## Configuration\n\n- **agent-definition-entity-id**: ID of the agent definition to use for classification\n- **target-entity-types**: Array of entity types to classify (e.g., ['workspace', 'task'])\n- **max-entities-per-run**: Maximum number of entities to process per execution (default: 10)\n\n## Schedule\n\nDefault: Runs every 15 minutes when enabled.\n\n## Implementation Note\n\nCreates a temporary agent session per entity with no chat history, retrieves the before/after entity snapshot, and applies suggested changes."
    }
  },
  "tool-type": "EntityClassifierTool",
  "configuration": {
    "agent-definition-entity-id": null,
    "target-entity-types": ["workspace", "task"],
    "max-entities-per-run": 10
  },
  "default-schedule": {
    "enabled": false,
    "interval": "15m",
    "run-on-startup": false
  }
}
```

### 2.3 VectorIndexerTool Entity

**Location**: `Phantom.Workspaces.Data.Core/JsonEntities/defaults/tools/vector-indexer-tool.json`

```json
{
  "entity-id": "<generated-guid>",
  "entity-types": ["tool"],
  "$schema": "https://schemas.workspaces.phantom.to/workspaces/data/core/tool.json",
  "names": [
    ["tools", "vector-indexer"]
  ],
  "display-name": {
    "default": "Vector Indexer Tool"
  },
  "content": {
    "default": {
      "mime-type": "text/markdown",
      "text": "# Vector Indexer Tool\n\nProcesses the vector indexing queue, computing embeddings for entities and updating the vector database.\n\n## Configuration\n\n- **batch-size**: Number of entities to process per execution (default: 50)\n- **embeddings-provider**: Provider to use for generating embeddings (e.g., 'openai', 'github')\n- **model**: Embedding model name (e.g., 'text-embedding-3-small')\n\n## Schedule\n\nDefault: Runs every 2 minutes when enabled.\n\n## Behavior\n\nDrains the vector index queue by:\n1. Calling `ProcessQueueAsync` to get pending entities\n2. Computing embeddings via `ComputeEmbeddingsAsync`\n3. Updating entity embeddings via `UpdateEmbeddingsAsync`"
    }
  },
  "tool-type": "VectorIndexerTool",
  "configuration": {
    "batch-size": 50,
    "embeddings-provider": "github",
    "model": "text-embedding-3-small"
  },
  "default-schedule": {
    "enabled": false,
    "interval": "2m",
    "run-on-startup": true
  }
}
```

### 2.4 CopilotSessionDiscoveryTool Entity

**Location**: `Phantom.Workspaces.Data.Core/JsonEntities/defaults/tools/copilot-session-discovery-tool.json`

```json
{
  "entity-id": "<generated-guid>",
  "entity-types": ["tool"],
  "$schema": "https://schemas.workspaces.phantom.to/workspaces/data/core/tool.json",
  "names": [
    ["tools", "copilot-session-discovery"]
  ],
  "display-name": {
    "default": "Copilot Session Discovery Tool"
  },
  "content": {
    "default": {
      "mime-type": "text/markdown",
      "text": "# Copilot Session Discovery Tool\n\nScans local GitHub Copilot CLI session state and creates agent-definition entities.\n\n## Configuration\n\n- **session-state-directory**: Path to copilot session state directory (default: ~/.copilot/session-state)\n- **create-agent-definitions**: Whether to create agent-definition entities (default: true)\n- **scan-checkpoints**: Whether to include checkpoint information (default: false)\n\n## Schedule\n\nDefault: Runs every 10 minutes when enabled.\n\n## Behavior\n\nScans the configured directory for copilot session folders, extracts plan.md and agent metadata, and creates or updates agent-definition entities representing those sessions."
    }
  },
  "tool-type": "CopilotSessionDiscoveryTool",
  "configuration": {
    "session-state-directory": "~/.copilot/session-state",
    "create-agent-definitions": true,
    "scan-checkpoints": false
  },
  "default-schedule": {
    "enabled": false,
    "interval": "10m",
    "run-on-startup": false
  }
}
```

## 3. Tools View (add-tools-view)

### 3.1 tools-view.json View Definition

**Location**: `Phantom.Workspaces.Data.Core/JsonEntities/defaults/views/tools-view.json`

```json
{
  "entity-id": "<generated-guid>",
  "entity-types": ["view"],
  "$schema": "https://schemas.workspaces.phantom.to/workspaces/data/core/view.json",
  "names": [
    ["views", "tools"]
  ],
  "display-name": {
    "default": "Tools"
  },
  "content": {
    "default": {
      "mime-type": "text/markdown",
      "text": "# Tools View\n\nShows available tools and their scheduled execution status for user-computer-profiles owned by the current user."
    }
  },
  "view-definition": {
    "sub-views": [
      {
        "name": "available-tools",
        "display-name": {
          "default": "Available Tools"
        },
        "get-entity": {
          "entity-type": "tool"
        }
      },
      {
        "name": "scheduled-on-my-profiles",
        "display-name": {
          "default": "Scheduled Tools"
        },
        "query": {
          "clauses": [
            {
              "clause-type": "participation",
              "participation-mode": "target",
              "relationship-type-names": {
                "values": [
                  ["entity-types", "scheduled-tool"]
                ]
              },
              "participants": {
                "source": {
                  "clause-type": "participation",
                  "participation-mode": "target",
                  "relationship-type-names": {
                    "values": [
                      ["entity-types", "owned-by"]
                    ]
                  },
                  "participants": {
                    "source": {
                      "clause-type": "field",
                      "field-path": {
                        "components": ["entity-types"]
                      },
                      "comparison-operator": "contains",
                      "value": "user"
                    }
                  }
                }
              }
            }
          ]
        }
      }
    ]
  }
}
```

### 3.2 Tools View Query Logic

The "scheduled-on-my-profiles" sub-view uses a nested query:

1. **Inner query**: Find all user entities (current user via `${USER}` token)
2. **Middle query**: Find user-computer-profiles owned by those users
3. **Outer query**: Find tools that are the target of "scheduled-tool" relationships from those profiles

This retrieves all tools scheduled on the current user's profiles.

### 3.3 Tools View Display

The view should show:

**Available Tools Tab:**
- List all tool entities
- Display name, description, tool-type
- Default schedule configuration
- "Schedule on Profile" shortcut button

**Scheduled Tools Tab:**
- List all scheduled-tool relationships for current user's profiles
- Tool name and description
- Host profile name
- Schedule configuration (enabled, interval, run-on-startup)
- Last run timestamp
- Last result status (success/failure/skipped)
- "Edit Schedule" shortcut button
- "Remove Schedule" shortcut button

### 3.4 ToolsViewViewModel Implementation

**Location**: `Phantom.Workspaces/ViewModels/ToolsViewViewModel.cs`

```csharp
public sealed class ToolsViewViewModel : IDisposable
{
    private readonly EntityBroker entityBroker;
    private SubscribedGet? availableToolsSubscription;
    private SubscribedQuery? scheduledToolsSubscription;
    
    public ObservableCollection<SubscribedEntityViewModel> AvailableTools { get; } = new();
    public ObservableCollection<ScheduledToolViewModel> ScheduledTools { get; } = new();
    
    public async Task InitializeAsync()
    {
        // Subscribe to available tools (all tool entities)
        var availableToolsRequest = new GetRequest
        {
            Entities =
            [
                new GetEntityRequest
                {
                    EntityTypeName = new EntityTypeName("tool"),
                },
            ],
        };
        this.availableToolsSubscription = await this.entityBroker.SubscribeGetAsync(availableToolsRequest);
        this.availableToolsSubscription.Changed += OnAvailableToolsChanged;
        
        // Subscribe to scheduled tools (query as shown in tools-view.json)
        var scheduledToolsQuery = /* construct query from tools-view.json */;
        this.scheduledToolsSubscription = await this.entityBroker.SubscribeQueryAsync(scheduledToolsQuery);
        this.scheduledToolsSubscription.Changed += OnScheduledToolsChanged;
    }
    
    private void OnAvailableToolsChanged(object? sender, EventArgs e)
    {
        // Update AvailableTools collection
    }
    
    private void OnScheduledToolsChanged(object? sender, EventArgs e)
    {
        // Update ScheduledTools collection
        // Extract schedule info from scheduled-tool relationship properties
    }
    
    public void Dispose()
    {
        this.availableToolsSubscription?.Dispose();
        this.scheduledToolsSubscription?.Dispose();
    }
}

public sealed class ScheduledToolViewModel
{
    public required string ToolName { get; init; }
    public required string ProfileName { get; init; }
    public required bool Enabled { get; init; }
    public required string Interval { get; init; }
    public DateTime? LastRun { get; init; }
    public string? LastResult { get; init; }
    public EntityId ToolEntityId { get; init; }
    public EntityId ProfileEntityId { get; init; }
    public EntityId RelationshipEntityId { get; init; }
}
```

## 4. Implementation Dependencies

### 4.1 Existing Infrastructure (Already Implemented)

**Note**: The codebase now uses a single tool abstraction:
- **`IWorkspaceTool`** (`Phantom.Workspaces.Tools`) - Shared interface for bootstrap/discovery tools and scheduled background tools. Every tool exposes a `ToolType` discriminator and runs through `ExecuteAsync(WorkspaceToolExecutionContext)`.

**Core Scheduled Tool Execution:**
- **`IWorkspaceTool` interface** - Contract for all tool implementations, including scheduled tools and bootstrap/discovery tools
- **`WorkspaceToolExecutionContext`** - Unified context passed to tools: DataAccessLayer, CancellationToken, current computer/user/profile entities, the tool-relationship snapshot, participant snapshots, tool snapshot, and due schedule snapshot
- **`ScheduledToolRegistry`** - Maps tool-type discriminators to `IWorkspaceTool` implementations
- **`ScheduledToolHost`** - Discovers tool-relationships targeting a host, evaluates schedules, executes tools
  - `RunDueToolsAsync(hostEntityId, hostNameComponents)` - Main execution loop
  - `GetRunningExecutions()` - Returns snapshot of currently running tools
  - Prevents duplicate concurrent runs of same relationship
- **`ToolExecutionResultWriter`** - Writes tool execution results to `tool-execution-result` entities
- **`ScheduleEvaluator`** - Evaluates schedule entities to determine if execution is due
  - Parses `repeat.frequency` (e.g., "5m", "1h")
  - Evaluates `repeat.start-at` times
  - Handles `repeat.days-of-week` constraints

**Existing Tool Implementations:**
- **`GitWorkspaceScanTool`** - Scans directories for git repositories, creates workspace/worktree entities
- **`EntityClassifierTool`** - Runs LLM agent against entities for classification
- **`VectorIndexerTool`** - Processes embedding queue, updates vector database
- **`CopilotSessionDiscoveryTool`** - Scans copilot session state, creates agent-definition entities

**View Infrastructure:**
- **`ScheduledToolsRunningViewModel`** - Shows currently running tools in UI
- Standard view system - Views are entities with view-definition property, rendered by existing ViewBrowserViewModel

### 4.2 Required New Components

**Entity Definitions Only** - No new code required:
1. ✅ `tool.json` schema (CREATED)
2. ✅ `tool-entity-type.json` entity definition (CREATED)
3. ⏳ Four tool entity instances (git-workspace-scan, entity-classifier, vector-indexer, copilot-session-discovery)
4. ⏳ `tools-view.json` view definition with sub-views
5. ⏳ `tool-schema.md` documentation for LLMs

**Optional Future Enhancements:**
- **Tool configuration UI** - Dialog/view for editing tool configuration
- **Schedule creation shortcut** - "Schedule on Profile" button that creates tool-relationship + schedule entities
- **Schedule editing shortcut** - Modify existing schedule entities
- **Tool result viewer** - View/filter tool-execution-result entities

### 4.3 How Tools are Scheduled

**Scheduling Workflow:**
1. User creates a **schedule entity** with repeat frequency:
   ```json
   {
     "entity-types": ["schedule"],
     "repeat": {
       "frequency": "5m",
       "start-at": ["00:00"]
     }
   }
   ```

2. User creates a **tool-relationship entity** linking profile → tool with schedule:
   ```json
   {
     "entity-types": ["relationship", "tool-relationship"],
     "names": [["relationship", "<entity-id>"]],
     "participants": {
       "tool": "<tool-entity-id>",
       "schedule": ["<schedule-entity-id>"],
       "target": ["<profile-entity-id>"]
     }
   }
   ```

3. **ScheduledToolHost** discovers the relationship:
   - Queries for tool-relationships where target includes the running host
   - For each relationship, retrieves the tool entity, the due schedule entity, and all relationship participants
   - Resolves the current computer, user, and user-computer-profile entities from the host profile
   - Evaluates schedule via ScheduleEvaluator to check if execution is due
   - If due, resolves the tool via `ScheduledToolRegistry.GetTool(tool-type)`
   - Calls `IWorkspaceTool.ExecuteAsync(context)` with the unified execution context
   - Writes result via ToolExecutionResultWriter

4. **Disabling a tool**: Delete the tool-relationship entity (no "enabled" flag needed)

### 4.4 Querying for Scheduled Tools

To find all tools scheduled on a user-computer-profile:

```csharp
// Query for tool-relationships targeting a specific profile
var query = new QueryRequest
{
    Clauses =
    [
        new ParticipationQueryClause
        {
            ParticipationMode = ParticipationMode.Specified,
            ParticipantRole = "target",
            RelationshipTypeNames = new RelationshipTypeNameQuery
            {
                Values = [new EntityName(["entity-types", "tool-relationship"])],
            },
            Participants = new Dictionary<string, QueryClause>
            {
                ["target"] = new FieldQueryClause
                {
                    FieldPath = new FieldPath(["entity-id"]),
                    ComparisonOperator = FieldComparisonOperator.Equals,
                    Value = JsonSerializer.SerializeToElement(profileEntityId.ToString()),
                },
            },
        },
    ],
};
```

This returns tool-relationship entities. Extract `participants.tool` and `participants.schedule` to get tool and schedule entity IDs.

## 5. Usage Workflow

### 5.1 Scheduling a Tool

1. User opens Tools view
2. User navigates to "Available Tools" tab
3. User selects a tool (e.g., "Git Workspace Scan Tool")
4. User clicks "Schedule on Profile" shortcut
5. UI prompts for target user-computer-profile
6. UI shows schedule configuration dialog
7. User configures schedule (enabled: true, interval: "5m", run-on-startup: true)
8. System creates scheduled-tool relationship entity linking profile → tool
9. ScheduledToolHost discovers the new relationship and begins executing tool per schedule

### 5.2 Viewing Scheduled Tools

1. User opens Tools view
2. User navigates to "Scheduled Tools" tab
3. View displays all tools scheduled on user's profiles:
   - Tool name: "Git Workspace Scan Tool"
   - Profile: "alice@devbox"
   - Schedule: "Every 5m"
   - Last run: "2 minutes ago"
   - Last result: "Success"
4. User can edit schedule or remove schedule via shortcut buttons

### 5.3 Monitoring Tool Execution

1. ScheduledToolHost discovers scheduled-tool relationships targeting user-computer-profiles
2. For each relationship, host evaluates schedule via ScheduleEvaluator
3. When tool is due, host:
   - Resolves the tool via `ScheduledToolRegistry.GetTool(tool-type)`
   - Calls `IWorkspaceTool.ExecuteAsync()`
   - Writes result entity via `ToolExecutionResultWriter`
   - Updates scheduled-tool relationship with last-run and last-result
4. Tools view auto-refreshes to show updated execution status

## 6. Testing Strategy

### 6.1 Schema Validation Tests

**Location**: `Phantom.Workspaces.Data.Core.Tests/ToolSchemaTests.cs`

- Validate tool.json schema against JSON Schema Draft 2020-12
- Validate all tool entity definitions against tool.json schema
- Validate scheduled-tool.json schema
- Verify schedule-definition regex patterns (interval format)

### 6.2 Entity Naming Tests

**Location**: `Phantom.Workspaces.Data.Core.Tests/ToolEntityNamingTests.cs`

- Verify tool entities have correct name prefixes (["tools", ...])
- Verify scheduled-tool relationships have correct name format (["relationship", <entity-id>])

### 6.3 View Query Tests

**Location**: `Phantom.Workspaces.Tests/ToolsViewQueryTests.cs`

- Create test user, user-computer-profile, tool, and scheduled-tool relationship
- Execute tools-view.json query
- Verify query returns correct scheduled tools for current user's profiles
- Verify query excludes tools scheduled on other users' profiles

### 6.4 View Model Tests

**Location**: `Phantom.Workspaces.Tests/ToolsViewViewModelTests.cs`

- Initialize ToolsViewViewModel
- Verify AvailableTools collection populates with all tool entities
- Verify ScheduledTools collection populates with scheduled tools for current user
- Test subscription updates when tools are scheduled/unscheduled
- Test schedule configuration extraction from relationship entities

## 7. Documentation

### 7.1 tool-schema.md

**Location**: `Phantom.Workspaces.Data.Core/JsonEntities/documentation/tool-schema.md`

Comprehensive documentation for tool entities including:
- Expected JSON shape
- Property descriptions
- Tool-type registry
- Configuration examples
- Schedule definition format
- Relationship patterns (scheduled-tool)
- LLM configuration guidance

### 7.2 User Documentation

**Location**: `docs/features/scheduled-tools.md`

User-facing documentation explaining:
- What scheduled tools are
- How to view available tools
- How to schedule tools on profiles
- How to configure schedules
- How to monitor tool execution
- How to troubleshoot failed tools

## 8. Open Questions

1. **Tool selection UI**: Should "Schedule on Profile" show a dialog or use a simpler inline approach?
2. **Schedule editing**: Should schedule be editable in-place in the view, or via a separate edit dialog?
3. **Tool configuration**: Should configuration be editable after scheduling, or fixed at schedule time?
4. **Permission model**: Should tools have explicit permissions/trust profiles, or inherit from host profile?

## 9. Future Enhancements

1. **Tool marketplace**: Browse and install third-party tools
2. **Tool templates**: Create custom tools via UI without code
3. **Tool chains**: Schedule dependent tools (e.g., scan → classify → index)
4. **Tool notifications**: Alert user when tools fail or complete
5. **Tool logs**: Detailed execution logs viewable in GUI
6. **Tool metrics**: Execution time, success rate, resource usage
