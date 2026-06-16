# Scheduled tools

## Purpose

Let a user schedule tools to run against their workspaces — for example, scanning for git
repositories, vector indexing, and classifying entities — without manual intervention. Tools are
modeled as entities, scheduled through relationships, executed by a host, and their runs are
recorded as entities that can be browsed.

## Entity model

Scheduled tools reuse the existing entity / relationship model (see the entity-type catalog under
`Phantom.Workspaces.Data.Core/JsonEntities/schema-definitions`).

1. **`tool` entity** — each tool type exists as an entity of type `tool`. A tool carries a
   `type` discriminator and an arbitrary set of parameters specific to that tool. A tool's
   parameters may themselves be modeled as a more specific entity type (for example,
   `vector-indexer-tool`, `entity-classifier-tool`).
2. **`schedule` entity** (`schedule.json`) — models recurrence: `repeat.frequency`,
   `repeat.days-of-week`, and `repeat.start-at`. A library of common schedules already exists
   (`JsonEntities/Schedules/*` — every minute, every hour, daily at NN:00, etc.).
3. **`tool-relationship` entity** (`tool-relationship.json`) — a relationship whose participants
   bind a tool to its execution context:
   - `tool` — the tool entity to run.
   - `schedule` — one or more `schedule` entities controlling when it runs.
   - `target` — one or more entities to run against; the **host** that executes the relationship
     is typically a `user-computer-profile`.
4. **`tool-execution-result` entity** — a record of a single tool run, with a start time, end
   time, the tool name, and arbitrary content. A `tool-execution-result` may have child
   `tool-execution-result` entities to log sub-tasks and progress.

### Execution-result storage path

Tool execution results are stored under the host entity, named:

```
[ <host entity name components...>, "tool-executions", <tool-name>, <start-time> ]
```

Child results (sub-tasks / progress) are nested beneath their parent result entity.

## Host runtime

When a host is running, it executes the scheduled tools bound to it. For a `user-computer-profile`
host, the host process is the **`Phantom.Workspaces` executable**.

1. On startup (and periodically), the host queries for `tool-relationship` entities whose
   `target` includes the host.
2. For each relationship, it evaluates the bound `schedule` entities against the last execution
   time to decide whether a run is due.
3. Due tools are executed; each run creates a `tool-execution-result` under the host and updates
   it (or appends child results) as the run progresses.
4. Tool execution honors the workspace **trust model**: a scheduled tool runs under the trust
   profile resolved for its tool/agent definition, and on the host's client instance (see
   `docs/design/trust-models.md`). Tools that drive an agent use `ITrustedExecutor` to construct
   the agent on the correct (local or remote) client instance.
5. Tools that are currently running when their schedule evaluates shall not be started again;
   the current tool run is allowed to complete first, and the next run will start at the
   next evaluation time.

## Tool progress

Tools periodically update their `tool-execution-result` entity with progress, or add child
`tool-execution-result` entities to record sub-tasks. Updates use the normal `IDataAccessLayer`
update path, so progress is immediately visible to any subscriber.

## Tool result browser GUI

A dedicated **tool result browser** lets users inspect runs:

1. Enumerate all known host entity types and their host entities.
2. For a selected host, present a tree rooted at `[ host..., "tool-executions" ]` and navigate by
   relationship into per-tool, per-run results and their child progress entries.
3. Surface start/end times, status, and content for each result; refresh live as runs update.

## Default setup note

A `note` entity describes the typical first-run setup a user is guided through, including:

- Scanning for git repositories.
- Vector indexing (see `docs/design/vector-search.md`).
- Entity classification.

The `tool` entity type is documented (entity-type instructions) so the LLM can correctly author
`tool` entities and `tool-relationship` relationships on the user's behalf.

## Entity classifier tool

The entity classifier tool uses the queue model described in `docs/design/vector-search.md`
(`ProcessQueue`) to classify entities on a schedule. It is configured (via relationship) with:

- a `note` entity providing the classifier prompt, and
- an agent definition for the agent that performs the classification.

The classifier prompt directs the agent to:

- Use vector search to locate related tasks.
  - If no related task exists and the entity is not itself a task, create one; otherwise create
    the appropriate association.

For each entity, the classifier tool:

1. Reads the entity.
2. Reads the entity-type.
3. Reads the set of all entity-types as simple names.
4. Reads the entity's relationships to other entities.
5. Presents a prompt in this order (to favor LLM KV-cache reuse):
   1. The classifier prompt.
   2. The set of all entity types.
   3. The entity-type instructions.
   4. The entity content.
   5. The relationships the entity currently possesses.
6. Asks the LLM to use workspace tools to create/remove relationships or make other state changes
   permitted by the entity type or by notes on the entity.

The classifier runs the agent definition once per entity **without recording chat history**. As
each batch is processed, it advances the queue token (a timestamp; see vector-search.md), so a run
resumes where the previous run stopped.

## New classes

1. `ScheduledToolHost`
   - Host-side service that discovers due `tool-relationship`s for the host and drives runs.
2. `IScheduledTool` / `ScheduledToolRegistry`
   - Abstraction for a runnable tool keyed by `tool.type`, plus a registry mapping types to
     implementations (`VectorIndexerTool`, `EntityClassifierTool`, `GitWorkspaceScanTool`).
3. `ScheduleEvaluator`
   - Decides whether a `schedule` is due given the last execution time and the current time.
4. `ToolExecutionResultWriter`
   - Creates and updates `tool-execution-result` entities (including child progress entries)
     under the host.
5. `EntityClassifierTool` / `VectorIndexerTool`
   - Concrete scheduled tools (the latter is detailed in vector-search.md).
6. `ToolResultBrowserViewModel`
   - GUI view model enumerating hosts and navigating execution-result trees.

## Key integration points

1. App / host startup (`Phantom.Workspaces`)
   - Composes `ScheduledToolHost` for the current `user-computer-profile` after the repository is
     initialized.
2. `IDataAccessLayer`
   - Query for due `tool-relationship`s; read targets/entity-types; write `tool-execution-result`s.
3. Trust model (`docs/design/trust-models.md`)
   - Tools (especially agent-driven ones) execute via `ITrustedExecutor` under the resolved trust
     profile and client instance.
4. Vector search (`docs/design/vector-search.md`)
   - The vector indexer and entity classifier consume the `ProcessQueue` API and vector queries.
5. GUI shell
   - Adds the tool result browser as a workspace tab / window.

## Test tasks

1. `ScheduleEvaluator` tests — frequency / days-of-week / start-at "is due" decisions across edge
   cases (timezones, missed runs, first run).
2. `ScheduledToolHost` tests — discovers only relationships targeting the host; runs only due
   tools; records start/end results.
3. `ToolExecutionResultWriter` tests — result entities are created at the expected name path and
   child progress entries nest correctly.
4. `EntityClassifierTool` tests — per-entity prompt assembly order; queue token advances per batch;
   no chat history recorded.
5. Tool result browser view-model tests — enumerates hosts and builds the execution-result tree.

## Non-goals

1. A general cron expression language (recurrence is modeled by the `schedule` entity).
2. Cross-host coordination/leasing of a single relationship (each host runs the relationships that
   target it).
