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
   - `target` — exactly one host entity id (typically a `user-computer-profile`), not an array of
     hosts.
   - `paused` — optional boolean (default `false`). When true, this relationship is skipped even if
     its schedule is due.
   - `last-started` — optional RFC 3339 UTC date-time set by the host immediately before a run is
     launched; this is the due-check indicator for whether to start a new run.
4. **`tool-execution-result` entity** — a record of a single tool run, with a start time, end
   time, the tool name, and arbitrary content. A `tool-execution-result` may have child
   `tool-execution-result` entities to log sub-tasks and progress.
5. **Host scheduler state (persisted on the host profile entity)** — add
   `scheduled-tools-paused: boolean` (default `false`). This is the persisted pause/stop-all state
   controlled from the scheduled tools tab.

### Execution-result storage path

Tool execution results are stored under the host entity, named:

```
[ <host entity name components...>, "tool-executions", <tool-name>, <start-time> ]
```

Child results (sub-tasks / progress) are nested beneath their parent result entity.

## Host runtime

> **Status:** the host runtime is implemented in `Phantom.Workspaces/ScheduledTools/` and
> `Phantom.Workspaces.Tools/`: `ScheduleEvaluator` (+ `ScheduleDefinition`) decides whether a
> schedule is due; `IWorkspaceTool` / `ScheduledToolRegistry` dispatch by `tool.type`;
> `ToolExecutionResultWriter` records runs as
> `tool-execution-result` entities; and `ScheduledToolHost.RunDueToolsAsync` discovers
> tool-relationships targeting the host, evaluates schedules against `tool-relationship.last-started`,
> and runs due tools (no double-start). The built-in tools are implemented — `VectorIndexerTool`,
> `EntityClassifierTool`, `GitWorkspaceScanTool`, and `CopilotSessionDiscoveryTool`. The host exposes
> in-flight running state (`GetRunningExecutions` + `RunningExecutionsChanged`); the GUI view-model
> cores are implemented — `ScheduledToolsRunningViewModel` (running display) and
> `ToolResultBrowserViewModel` (result browser). The remaining work is the thin Avalonia views /
> windows and wiring them into the main window (shared with the connection-status network icon from
> `reverse-tunnel-trust-execution`).

When a host is running, it executes the scheduled tools bound to it. For a `user-computer-profile`
host, the host process is the **`Phantom.Workspaces` executable**.

1. On startup (and periodically), the host queries for `tool-relationship` entities whose
   `target` equals the host entity id.
2. Before due-checking, the host reads `scheduled-tools-paused` from the current host profile
   entity; if paused, it starts no new runs.
3. For each relationship, if `tool-relationship.paused = true`, it is skipped.
4. For each non-paused relationship, it evaluates the bound `schedule` entities against
   `tool-relationship.last-started` to decide whether a new run is due.
5. When a run is due, the host first updates `tool-relationship.last-started = now` (with normal
   concurrency checks), then starts the run.
6. Due tools are executed; each run creates a `tool-execution-result` under the host and updates
   it (or appends child results) as the run progresses.
7. Tool execution honors the workspace **trust model**: a scheduled tool runs under the trust
   profile resolved for its tool/agent definition, and on the host's client instance (see
   `docs/design/trust-models.md`). Tools that drive an agent use `ITrustedExecutor` to construct
   the agent on the correct (local or remote) client instance.
8. Tools that are currently running when their schedule evaluates shall not be started again;
   the current tool run is allowed to complete first, and the next run will start at the
   next evaluation time.
9. The scheduled tools tab exposes a persisted **Stop all / Pause** action:
   - sets `scheduled-tools-paused = true` on the host profile entity,
   - requests cancellation for all currently running scheduled tool executions,
   - keeps the scheduler paused across app restart until resumed.

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
4. Provide a persisted **Stop all / Pause** toggle button in the scheduled tools tab:
   - **Pause**: persist `scheduled-tools-paused = true`, stop current runs, and block new runs.
   - **Resume**: persist `scheduled-tools-paused = false`; normal scheduling resumes on next tick.
5. When paused, the main window clock/scheduled-tools button shows a pause icon state.
6. Each scheduled tool relationship row exposes a persisted per-relationship pause toggle bound to
   `tool-relationship.paused`.
7. Boolean fields (including `scheduled-tools-paused` and `tool-relationship.paused`) are edited
   with a left-right toggle button control in field-list editing UIs.

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
   3. The interest instructions (static across the run; see below).
   4. The entity-type instructions.
   5. The entity content.
   6. The relationships the entity currently possesses.
6. Asks the LLM to use workspace tools to create/remove relationships or make other state changes
   permitted by the entity type or by notes on the entity.

### Interest instructions

Immediately after the entity-type list (and before the entity-specific sections, to keep the static
content KV-cache-friendly), the classifier emits an `# Interests` section built by querying the
seeded `interest-type` entities (`docs/design/interests.md`). It lists each available interest with
its applied-state description, and instructs the agent to: apply/remove interests by creating/removing
the corresponding interest relationship (with `target`/`user`/`view` participants); include a `note`
explaining *why* on every relationship it creates; mark a `completed`/`cancelled` task unmodified for
over a week as `not-interesting`; derive a task's `assigned-to` interest from its source-system
`assigned-to` field when missing; and associate an entity that is clearly part of a workstream with
the corresponding task via a `related` relationship.

The classifier runs the agent definition once per entity **without recording chat history**. As
each batch is processed, it advances the queue token (a timestamp; see vector-search.md), so a run
resumes where the previous run stopped.

## New classes

1. `ScheduledToolHost`
   - Host-side service that discovers due `tool-relationship`s for the host and drives runs.
2. `IWorkspaceTool` / `ScheduledToolRegistry`
   - Shared abstraction for runnable tools keyed by `tool.type`, plus a registry mapping types to
     scheduled implementations (`VectorIndexerTool`, `EntityClassifierTool`, `GitWorkspaceScanTool`,
     `CopilotSessionDiscoveryTool`).
3. `ScheduleEvaluator`
   - Decides whether a `schedule` is due given the last execution time and the current time.
4. `ToolExecutionResultWriter`
   - Creates and updates `tool-execution-result` entities (including child progress entries)
     under the host.
5. `EntityClassifierTool` / `VectorIndexerTool`
   - Concrete scheduled tools (the latter is detailed in vector-search.md).
6. `ToolResultBrowserViewModel`
   - GUI view model enumerating hosts and navigating execution-result trees.
7. `ScheduledToolPauseStateService` (or equivalent)
   - Reads/writes persisted `scheduled-tools-paused` on the host profile entity and exposes current
     pause state to the runner and UI.

## Key integration points

1. App / host startup (`Phantom.Workspaces`)
   - Composes `ScheduledToolHost` for the current `user-computer-profile` after the repository is
     initialized.
   - Creates and starts a scheduler loop service (for example `ScheduledToolRunner`) that invokes
     `RunDueToolsAsync(currentProfileEntityId, hostNameComponents)` immediately on startup and then
     periodically.
   - Wires a pause-state service that reads persisted `scheduled-tools-paused` and publishes state
     changes to both scheduler runtime and UI.
   - Stops the scheduler loop when the main window/view model is disposed so no background runs
     continue after shutdown.
2. `IDataAccessLayer`
   - Query due `tool-relationship`s; read tool/schedule/target entities; update
     `tool-relationship.last-started`; write `tool-execution-result`s.
     - Read/write both pause flags: host `scheduled-tools-paused` and relationship
       `tool-relationship.paused`.
3. Trust model (`docs/design/trust-models.md`)
   - Tools (especially agent-driven ones) execute via `ITrustedExecutor` under the resolved trust
     profile and client instance.
4. Vector search (`docs/design/vector-search.md`)
   - The vector indexer and entity classifier consume the `ProcessQueue` API and vector queries.
5. GUI shell
   - Adds the tool result browser as a workspace tab / window.
   - Binds the main-window clock/scheduled-tools button icon to scheduler state:
     running/idle normally, pause icon when `scheduled-tools-paused` is true.
   - Updates `tool-relationship` entity-type view to include editable `paused` (toggle) and
     `last-started` fields for scheduled relationship inspection.

## Startup / run-loop integration points

The host runtime must be explicitly started by the `Phantom.Workspaces` process.

1. `MainWindowViewModel.InitializeAsync`
   - After `EntityBroker` and profile/session entities are available, construct
    `ScheduledToolRegistry` from the built-in `IWorkspaceTool` implementations.
   - Construct `ScheduledToolHost` using the workspace `IDataAccessLayer` and that registry.
   - Start a periodic runner that calls `RunDueToolsAsync(...)`.
2. Scheduler loop implementation
   - Use a single loop instance per opened workspace host.
   - Trigger one immediate run on startup, then repeat at a fixed poll interval.
   - Serialize invocations so the loop never overlaps its own `RunDueToolsAsync` calls.
   - Check persisted pause state each tick and skip starting runs while paused.
   - Support a stop-all path that cancels in-flight scheduled tool executions.
3. Shutdown/disposal
   - Cancel and dispose the loop from `MainWindowViewModel.DisposeAsync` (or equivalent window
    teardown path).
   - Ensure cancellation is cooperative so in-flight tools can complete or stop according to their
    cancellation token handling.
4. Scheduled tasks tab actions
   - Pause/resume buttons update persisted pause state on the host profile entity.
   - Pause action issues stop-all cancellation through the scheduler host/runner.
   - Per-relationship pause/resume toggles update `tool-relationship.paused` directly.

## Test tasks

1. `ScheduleEvaluator` tests — frequency / days-of-week / start-at "is due" decisions across edge
   cases (timezones, missed runs, first run).
2. `ScheduledToolHost` tests — discovers only relationships whose single `target` is the host;
   uses `last-started` to decide due runs; updates `last-started` when a run starts; does not
   start a second run while one is already running; records start/end results.
3. `ToolExecutionResultWriter` tests — result entities are created at the expected name path and
   child progress entries nest correctly.
4. `EntityClassifierTool` tests — per-entity prompt assembly order; queue token advances per batch;
   no chat history recorded.
5. Tool result browser view-model tests — enumerates hosts and builds the execution-result tree.
6. Pause-state tests — persisted `scheduled-tools-paused` survives restart and controls due-run
   behavior.
7. Stop-all tests — pause command cancels running tools and prevents new starts while paused.
8. Main-window indicator tests — clock/scheduled-tools button shows pause icon whenever paused.
9. Per-relationship pause tests — `tool-relationship.paused = true` prevents due runs for that
   relationship while leaving other relationships runnable.
10. Relationship-view tests — `tool-relationship` entity-type view includes editable `paused` and
    visible `last-started`.
11. Boolean editor tests — boolean fields render/edit with the left-right toggle control and persist
    true/false transitions correctly.

## Non-goals

1. A general cron expression language (recurrence is modeled by the `schedule` entity).
2. Multi-host targets in one `tool-relationship` (a relationship is single-host via one `target`).
