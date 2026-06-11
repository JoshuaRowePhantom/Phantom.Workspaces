# WorkspaceEntityToolsetFactory design

## Summary

Implement a new `WorkspaceEntityToolsetFactory : IToolsetFactory` that exposes entity-manipulation tools backed by an `IDataAccessLayer`.

The key rule is strict concurrency: **the LLM must provide valid concurrency keys for mutating operations**. The toolset will not silently fetch/guess/fill concurrency tags.

## Goals

1. Provide a first-class toolset for reading and mutating workspace entities.
2. Reuse existing `IDataAccessLayer` contracts directly (no alternate persistence pathway).
3. Enforce optimistic concurrency in tool contracts so mutation safety is explicit and deterministic.
4. Produce tool APIs that are easy for an LLM to call correctly.

## Non-goals

1. No fallback/auto-retry mutation path that hides concurrency conflicts.
2. No schema-bypass path; updates still flow through existing validation/access layers.
3. No custom storage format or duplicate entity cache in the toolset.

## Proposed types

## `WorkspaceEntityToolsetFactory`

```csharp
public sealed class WorkspaceEntityToolsetFactory : IToolsetFactory
{
    public WorkspaceEntityToolsetFactory(
        IDataAccessLayer dataAccessLayer,
        IToolsetFactory? underlyingToolsetFactory = null);

    public Task<IToolset?> CreateToolsetAsync(AgentSchema.Tool tool, AgentServices agentServices);
}
```

Behavior:

1. Matches a dedicated tool kind (proposed: `"workspace-entity"`).
2. Returns an `IToolset` containing entity tools for that kind.
3. Delegates to `underlyingToolsetFactory` when kind does not match.

## `WorkspaceEntityToolset`

Implements `IToolset` and materializes a fixed set of `AITool` wrappers over `IDataAccessLayer`.

## Tool surface (proposed)

1. `workspace_entity_get`
   - Inputs: entity ids and/or names, optional timestamp(s), relationship request options.
   - Output: snapshots including `entity-id`, `concurrency-tag`, `modified-time`, `data`.

2. `workspace_entity_query`
   - Inputs: query clauses mapped to `QueryRequest`.
   - Output: matching snapshots (including concurrency tags).

3. `workspace_entity_update`
   - Inputs: list of `EntityChange` values.
   - Output: raw `UpdateResult` projection (state, resulting ids, concurrency tags, errors).

4. `workspace_entity_delete`
   - Thin wrapper around `workspace_entity_update` for remove operations.

5. `workspace_entity_get_history` (optional first pass; likely included)
   - Inputs: entity ids.
   - Output: update timestamps.

## Concurrency policy

## Required for mutation

For `EntityChangeMode.Replace` and deletes on existing entities:

1. Caller must supply:
   - `entity-id`
   - `concurrency-tag`
   - requested mutation payload
2. If missing concurrency tag, tool returns validation error before DAL call.
3. If stale/mismatched tag, DAL conflict is returned to caller as-is.

## Allowed without concurrency

Only true create/add semantics where no prior version exists (or DAL handles as add) may omit concurrency tag.

## No hidden reconciliation

The toolset will **not**:

1. perform pre-read and retry automatically,
2. overwrite with latest tag,
3. mutate request semantics to force success.

This keeps conflict handling explicit in the model workflow.

## Request/response shape strategy

Tool schemas will mirror DAL contracts closely to reduce translation ambiguity:

1. Inputs modeled from `GetRequest`, `QueryRequest`, `UpdateRequest`, etc.
2. Outputs modeled from `GetResult`, `QueryResult`, `UpdateResult` projections.
3. Preserve key field names used by DAL (`entity-id`, `concurrency-tag`, `entity-types`, `names`).

## Error model

Errors are surfaced in two layers:

1. **Input contract errors** (missing required fields like concurrency tag for mutation): tool-level validation error.
2. **DAL operation errors** (schema validation, referential integrity, concurrency mismatch): returned from `UpdateResult.EntityResults[*].Errors`.

No broad catch-and-hide; failures are surfaced explicitly.

## Wiring plan

1. Add `WorkspaceEntityToolsetFactory` in `Phantom.Workspaces.Llm.Core`.
2. Add `WorkspaceEntityToolset` and concrete entity tool wrappers.
3. Update default factory composition to include workspace entity kind support (or keep opt-in factory composition if preferred).
4. Keep existing toolsets unchanged (`web`, `filesystem`, etc.).

## Testing plan

Use `InMemoryDataAccessLayer` for deterministic tests.

1. Factory routing tests:
   - returns toolset for matching kind,
   - delegates otherwise.
2. Listing tests:
   - expected tool names exist.
3. Concurrency enforcement tests:
   - mutation without concurrency tag is rejected for existing entities,
   - stale concurrency tag fails with conflict,
   - correct tag succeeds.
4. Data-shape tests:
   - returned snapshots include concurrency tags needed for follow-up mutations.

## Phased implementation

1. Phase 1: `get`, `query`, `update` tools + strict concurrency checks.
2. Phase 2: optional helpers (`delete`, `history`) and any schema refinements.

## Open review decisions

1. Confirm tool kind string: `"workspace-entity"` (or another preferred name).
2. Confirm whether `workspace_entity_delete` should be separate or only via `workspace_entity_update`.
3. Confirm whether `get_history` is in scope for first implementation pass.
