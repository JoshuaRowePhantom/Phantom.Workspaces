# Workspace Entity Tool Instruction Details

## Tool summary

1. `workspaces_entity_get` reads entities. Input is a full `get-request` object.
2. `workspaces_entity_update` adds/replaces/deletes entities. Input is a full `update-request` object.
3. `workspaces_entity_generate_guid` creates a GUID for explicit `entity-id` assignment.

## High-level entity model

1. Entities are JSON objects.
2. The `entity-types` property on an entity declares which entity types that entity is.
3. Each declared entity type corresponds to a schema definition.
4. Effective validation is schema composition:
   - the base `entity` schema always applies,
   - each declared entity type schema is composed into the validator,
   - unknown or unevaluated properties are rejected by the composed schema rules.
5. Schema validation is enforced by the data access layer during updates.
6. Entity type definitions are stored as entities under `["entity-types", "<type-name>"]`.

## `workspaces_entity_get` shape and semantics

`workspaces_entity_get` accepts:

```json
{
  "get-entity": [
    {
      "entity-id": "optional-guid",
      "entity-name": ["optional", "name", "components"],
      "entity-type-names": ["optional-type-filter"],
      "relationships-to-return": [
        {
          "relationship-type-names": ["optional-relationship-type-filter"],
          "relationship-role-names": ["optional-role-filter"]
        }
      ],
      "properties": ["optional", "json.path", "filters"]
    }
  ],
  "relationships-to-return": [],
  "timestamps": [null],
  "properties": ["optional", "json.path", "filters"]
}
```

Pass arrays and objects as raw JSON values. Do not JSON-encode them into strings (for example, do not send `"get-entity": "[{...}]"`).

Property filtering behavior:

1. `properties` at request level applies to all returned entities.
2. If request-level `properties` is omitted and exactly one entity query is present, that query's `properties` is used.
3. A property path like `"content.default.content.text"` returns only that nested branch in `data`.
4. Unknown paths are ignored.
5. Use `properties` only when filtering many entities, or when you only need a small set of short content properties.
6. For single entity-type reads, prefer omitting `properties` and reading the full entity.

## Entity type names vs. display names (avoid a common mistake)

When filtering by entity type (the `entity-type-names` field in a `get-request`, or a query's
entity-type clause), each value must be the entity type's **canonical name** — a single component
taken from the entity type's `names` property (for example `task`, `note`, `entity-type`) — or the
entity type's **entity id**.

Do **not** use an entity type's **display-name**. The display-name is a human-facing label (for
example `"Work Item"`) and is **not interchangeable** with the entity type's name. Passing a
display-name as an `entity-type-names` value will not match anything.

- Correct: `"entity-type-names": ["task"]`
- Incorrect: `"entity-type-names": ["Task Item"]`  (this is a display-name, not a name)

To discover valid `entity-type-names` values, list the entity types and read their `names`
(see "list all entity types and their names" below); use a value from `names`, not from
`display-name`.

## `workspaces_entity_update` shape and semantics

Use one tool for add, replace, and delete. Pass:

```json
{
  "update-metadata": {
    "comment": {
      "text": "why this change is happening"
    }
  },
  "changes": [
    {
      "entity-id": "optional-guid",
      "concurrency-tag": "required for safe replace/delete",
      "entity-change-mode": "replace",
      "data": { "full entity JSON for add/replace, or null for delete" }
    }
  ]
}
```

Update behaviors:

1. Add: `data` has entity JSON and `entity-id` may be omitted to auto-generate.
2. Replace: send full replacement `data` and current `concurrency-tag`.
3. Delete: set `data` to `null` and send current `concurrency-tag`.

## Relationship reason-note rule

Every relationship you create or replace — any entity whose `data` carries a `participants` object —
**must** include a non-empty `note` property stating *why* the relationship is being applied. Updates
that add or replace a relationship without a `note` reason are rejected. Example:

```json
{
  "entity-types": ["assigned-to", "relationship"],
  "participants": { "target": "<task-guid>", "user": "<user-guid>" },
  "note": "Assigned to the user on the project board."
}
```

## GUID generation rule

Do not call `workspaces_entity_generate_guid` for normal single-entity adds.

Only call `workspaces_entity_generate_guid` when you must pre-assign IDs (for example, creating multiple related entities in one update where they must reference each other).

## Prefer not to modify default entities

Entities whose name is under the `defaults` namespace — the first name component is `defaults`, for
example `["defaults", "mcp-servers", "<name>"]`, `["defaults", "agent-manifests", "<name>"]`, or
`["defaults", "profiles", "default"]` — are system-provided defaults that ship with the workspace.
They are managed by the system and may be re-seeded or reset, so direct edits can be lost or cause
conflicts.

By default, do **not** modify, replace, or delete these `defaults/...` entities on your own
initiative. To customize behavior, prefer creating your own entity in a non-`defaults` namespace (for
example a profile-specific entity such as `["computer-user-profiles", ..., "mcp-servers", "<name>"]`)
instead of editing the `defaults` entity. Reading `defaults/...` entities is always fine.

However, if the user **explicitly and unambiguously asks you to edit, replace, or delete a specific
`defaults/...` entity**, you may do so. First briefly warn them that the entity is system-managed and
that the change may be lost if the workspace re-seeds or resets, then proceed with the requested
`workspaces_entity_update`. Do not refuse a direct, explicit user instruction to change a `defaults`
entity solely because it is under `defaults`.

## Exact query: get one entity type explicitly

To get one entity type named `<type-name>`, call `workspaces_entity_get` with:

```json
{
  "get-entity": [
    {
      "entity-name": ["entity-types", "<type-name>"]
    }
  ]
}
```

## Exact query: get schema for one entity type

To get only the schema for one entity type named `<type-name>`, call `workspaces_entity_get` with:

```json
{
  "get-entity": [
    {
      "entity-name": ["entity-types", "<type-name>"]
    }
  ]
}
```

Then read `entity.data.schema` from the returned entity.

## Exact query: list all entity types and their names

Use this `workspaces_entity_get` request to list each entity type with its display name and entity names:

```json
{
  "get-entity": [
    {
      "entity-type-names": ["entity-type"]
    }
  ],
  "properties": ["display-name", "names"]
}
```

When you later filter by entity type, use a value from each type's `names` (its canonical name),
never its `display-name`.

## Exact query: list all tools

A `tool` entity defines a runnable background task. It is named `["tools", "<tool-type>"]` and
declares its implementation via the `tool-type` property. To list the available tools with their
names and implementation type, call `workspaces_entity_get` with:

```json
{
  "get-entity": [
    {
      "entity-type-names": ["tool"]
    }
  ],
  "properties": ["display-name", "names", "tool-type"]
}
```

## How to understand entity schemas

Each entity type has a documentation note attached. To read the documentation for a specific entity type (e.g. `git-repository`), call `workspaces_entity_get` with that entity type's name, then read the `content.default.content.text` property of the returned entity:

```json
{
  "get-entity": [
    {
      "entity-name": ["entity-types", "git-repository"]
    }
  ]
}
```

The returned entity's `content.default.content.text` contains the markdown documentation for that schema — including purpose, properties, naming convention, and a creation example.

Entity types with schema documentation include (but are not limited to):

| Entity type | What it represents |
|---|---|
| `organization` | Platform-agnostic organization (base type) |
| `repository` | Platform-agnostic repository (base type) |
| `work-item` | Platform-agnostic work item / issue (base type) |
| `pull-request` | Platform-agnostic pull request (base type) |
| `git-repository` | Git-specific repository (extends `repository`) |
| `git-pull-request` | Git-specific pull request with branch and commit fields |
| `git-work-item` | Git-specific work item with repository and PR linkage |
| `azure-devops-organization` | Azure DevOps organization |
| `azure-devops-project` | Azure DevOps project (repository equivalent) |
| `azure-devops-work-item` | Azure DevOps work item |

## How tools are configured and enabled

1. A tool's configuration lives as **top-level properties on the tool entity itself** (not nested
   under a `configuration` object). Which properties apply depends on the `tool-type`. To read one
   tool's full configuration, get the tool entity by name and read its properties:

   ```json
   {
     "get-entity": [
       {
         "entity-name": ["tools", "<tool-type>"]
       }
     ]
   }
   ```

2. A tool does not run on its own. It runs only when a `tool-relationship` entity
   (`"entity-types": ["relationship", "tool-relationship"]`) links it, via `participants`, to:
   - `tool`: the tool entity id,
   - `schedule`: an array of schedule entity ids (how often it runs),
   - `target`: an array of target entity ids to run against (typically user-computer-profiles).

   Creating that relationship enables and schedules the tool; deleting it disables the tool on those
   targets (the tool and schedule entities themselves are left intact). Because it is a relationship,
   it must include a `note` reason (see the relationship reason-note rule above).

3. To list how tools are currently enabled/scheduled, list `tool-relationship` entities:

   ```json
   {
     "get-entity": [
       {
         "entity-type-names": ["tool-relationship"]
       }
     ],
     "properties": ["names", "participants"]
   }
   ```

4. Schedules are themselves entities (`schedule` entity type); list them
   (`"entity-type-names": ["schedule"]`) to choose an existing frequency, or create one, before
   linking it in a `tool-relationship`.

5. Execution history and errors are recorded as `tool-execution-result` entities; read them to verify
   a tool ran and to see any error messages.

## Adding tabs to a workspace

### Choosing entity-view vs browser-view

- **Always prefer `entity-view`** for any URL the user cares about. Create an `external` entity to hold the URL and reference it by entity name in the tab.
- Use `browser-view` only for transient, one-off URLs that have no ongoing relevance and do not need to be persisted.

### external entity rules

- `entity-types` must be `["entity", "external"]` — **not** `["external"]` alone.
- Name the entity `["external", "<descriptive-name>"]` — **not** under any other namespace.
- The URL that workspace tabs open is stored under the `"default"` key in the `urls` map.

### Pattern: adding a persistent URL tab

When the user asks to open a URL in a workspace, perform these three operations in a single `workspaces_entity_update` call:

1. **Create the `external` entity** with the URL under `"default"` in `urls`.
2. **Create a `related` relationship** linking the workspace entity and the new external entity (include a `note`).
3. **Patch the workspace entity** to add an `entity-view` tab whose `target-entity-name` points at the new external entity's name.

Pre-generate both new entity IDs with `workspaces_entity_generate_guid` so the relationship can reference them before they exist.

```json
{
  "update-metadata": { "comment": { "text": "Add URL tab to workspace" } },
  "changes": [
    {
      "entity-id": "<external-guid>",
      "entity-change-mode": "replace",
      "data": {
        "entity-id": "<external-guid>",
        "entity-types": ["entity", "external"],
        "names": [["external", "<name>"]],
        "display-name": { "default": "<display name>" },
        "urls": { "default": "<url>" }
      }
    },
    {
      "entity-id": "<related-guid>",
      "entity-change-mode": "replace",
      "data": {
        "entity-id": "<related-guid>",
        "entity-types": ["entity", "related", "relationship"],
        "participants": {
          "source": "<workspace-entity-id>",
          "target": "<external-guid>"
        },
        "note": "Surfaces the external entity as a tab in the workspace."
      }
    },
    {
      "entity-id": "<workspace-entity-id>",
      "concurrency-tag": "<current-concurrency-tag>",
      "entity-change-mode": "json-patch",
      "data": [
        {
          "op": "add",
          "path": "/regions/0/tabs/-",
          "value": {
            "tab-id": "<stable-tab-uuid>",
            "title": "<display name>",
            "kind": "entity-view",
            "content": { "target-entity-name": ["external", "<name>"] }
          }
        }
      ]
    }
  ]
}
```
