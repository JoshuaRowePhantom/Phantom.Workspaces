# Tool Relationship Schema

A `tool-relationship` is a `relationship` entity that **enables and schedules a tool**. It links a
single tool to one or more schedules (when it runs) and one or more targets (what it runs against).

**Schema ID**: `https://schemas.workspaces.phantom.to/workspaces/data/core/tool-relationship.json`

## Why and when to create a tool-relationship

A `tool` entity on its own does nothing — it only describes *how* to do work (its `tool-type` and
`configuration`). The `ScheduledToolHost` never runs a tool until a `tool-relationship` links it to a
schedule and a target. Creating the relationship is therefore the act of **turning a tool on**.

Create a tool-relationship when:

- A user asks to **enable**, **schedule**, or **start** a background tool (for example, "scan my git
  repositories", "keep semantic search up to date", "discover my Copilot sessions").
- You have created (or found) a `tool` entity and now need it to actually execute.
- An existing tool should run against an **additional** target or on an **additional** schedule (add a
  target/schedule to an existing relationship, or create a second relationship).

Do **not** edit the `tool` or `schedule` entities to enable a tool — they are reusable building
blocks. Enabling, disabling, and re-targeting a tool is done entirely through tool-relationship
entities. To **disable** a tool, delete its tool-relationship (the tool and schedule remain available
for reuse).

## Participants

The participant role names below are required and are defined by
[`tool-relationship.json`](/JsonSchemas/tool-relationship.json).

### tool (entity-id, required)
The single `tool` entity to execute. See the [Tool entity type](#related-entity-types) for tool-types
and configuration.

### schedule (entity-id[], required, at least one)
One or more `schedule` entities that drive execution timing. Reusable schedules already exist (see
[Finding participant entity-ids](#finding-participant-entity-ids)); prefer reusing them over creating
new ones.

### target (entity-id[], required, at least one)
One or more entities the tool runs against. This is most often a `user-computer-profile`, but any
`entity` is allowed.

## Finding participant entity-ids

`participants` holds **entity-ids** (GUIDs), not names. To create a relationship the first time, look
up each participant entity by its `names` and use the resulting `entity-id`:

- **tool** — query for an entity whose name starts with `["tools", ...]`, e.g.
  `["tools", "git-workspace-scan"]`.
- **schedule** — query for an entity named `["schedule", ...]`. Built-in reusable schedules include
  `every-minute`, `every-five-minutes`, `every-fifteen-minutes`, `every-hour`, `every-two-hours`,
  `every-four-hours`, `every-day-at-06`, and so on.
- **target** — query for the `user-computer-profile` (named
  `["computer-user-profiles", "users", ...]`) or other entity to operate on. The current host profile
  is usually the right default.

## Example: enabling a tool for the first time

The most common case — run one tool, on one schedule, against the current profile. Resolve the three
participant entity-ids by name (above), then create:

```json
{
  "entity-id": "<new-guid>",
  "entity-types": ["entity", "relationship", "tool-relationship"],
  "$schema": "https://schemas.workspaces.phantom.to/workspaces/data/core/tool-relationship.json",
  "names": [
    ["relationship", "<new-guid>"]
  ],
  "participants": {
    "tool": "<git-workspace-scan-tool-entity-id>",
    "schedule": ["<every-fifteen-minutes-schedule-entity-id>"],
    "target": ["<user-computer-profile-entity-id>"]
  }
}
```

**Reasoning**: the user wanted git repositories tracked, so we link the `git-workspace-scan` tool to a
moderate-frequency schedule and the profile to scan on. Once written, `ScheduledToolHost` discovers
the relationship, evaluates the schedule, and begins executing the tool.

## Patterns

### Tool → schedule → profile (default)

One tool, one schedule, one target. Use this unless the user asks for more.

```json
{
  "participants": {
    "tool": "<tool-entity-id>",
    "schedule": ["<schedule-entity-id>"],
    "target": ["<profile-entity-id>"]
  }
}
```

### Tool → multiple schedules → profile

A tool can run on several cadences (for example, frequently during the day and rarely overnight). List
every schedule entity-id in `schedule`.

```json
{
  "participants": {
    "tool": "<tool-entity-id>",
    "schedule": ["<every-fifteen-minutes-schedule-id>", "<every-day-at-02-schedule-id>"],
    "target": ["<profile-entity-id>"]
  }
}
```

**Reasoning**: use multiple schedules when a single cadence cannot express the desired timing.

### Tool → schedule → multiple targets

The same tool, on the same schedule, against several entities. List every target entity-id in
`target`.

```json
{
  "participants": {
    "tool": "<tool-entity-id>",
    "schedule": ["<schedule-entity-id>"],
    "target": ["<profile1-entity-id>", "<profile2-entity-id>"]
  }
}
```

**Reasoning**: use multiple targets to run one tool across several profiles/entities without
duplicating the relationship.

## Verifying execution

After creating a relationship, check `tool-execution-result` entities (named
`["tool-execution-results", <profile-name>, <tool-type>, <timestamp>]`) for run history and errors.

## Related entity types

- **Tool** (`tool` entity type,
  `https://schemas.workspaces.phantom.to/workspaces/data/core/tool.json`,
  [tool-schema.md](/JsonEntities/documentation/tool-schema.md)) — the tool being scheduled; describes
  `tool-type` and `configuration`.
- **Schedule** (`schedule` entity type,
  [schedule-schema.md](/JsonEntities/documentation/schedule-schema.md)) — drives execution timing.
- **Tool Execution Result** (`tool-execution-result` entity type,
  [tool-execution-result-schema.md](/JsonEntities/documentation/tool-execution-result-schema.md)) —
  the output written for each run.
