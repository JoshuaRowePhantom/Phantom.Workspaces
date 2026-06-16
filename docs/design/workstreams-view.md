# Workstreams view: task hierarchy assigned to the user

> **Status: draft — design for review.** Defines how the `workstreams` view presents tasks assigned
> to the current user in their hierarchy. No implementation should land until approved. Tracks todo
> `workstreams-task-hierarchy-view`.

## Problem & scenario

The `workstreams` view (`JsonEntities/views/workstreams-view.json`, currently a title with no
sub-views) should show the **tasks assigned to the current user**, organized in their natural
**hierarchy** (parent task → child tasks → …), so the user sees their work grouped by the larger
efforts it belongs to rather than as a flat list.

This reuses the existing **view mechanism** (`docs/design/llm-session.md`, `JsonSchemas/view.json`):
a view is a tree of `sub-views`, each either a reference to another view or an inline
**view-definition** carrying a `get-entity` data-access-layer query plus presentation hints
(`relationships-to-return`, `entity-type-views`). The workstreams view becomes a single inline
view-definition that queries tasks and projects them hierarchically.

## Data model

`task` entities (`JsonSchemas/task.json`) have:

- `status`: `pending | in-progress | completed | blocked | cancelled`.
- `assigned-to`: the user the task is assigned to.

Two modeling points must be resolved for this view:

1. **Assignment.** `assigned-to` is currently typed as a plain `string` "reference to the user".
   Per the entity-reference convention (references are entity-name arrays, never slash-joined
   strings), assignment should be expressed either as an **entity reference** (an entity-name array)
   or — preferred, to match the interests/relationships model — as a **relationship** between the
   task and the `user` entity. This design assumes assignment is queryable as a relationship/reference
   to the current user entity; migrating `assigned-to` to that representation is a prerequisite and is
   captured as a test/implementation task below.

2. **Hierarchy.** Task nesting is expressed via **parent/child entity relationships** (the same
   `relationship`/`related` mechanism used elsewhere), not via name-path nesting, so a task can move
   between parents without renaming. A task's parent may itself be a task (a sub-workstream) or a
   higher-level grouping entity.

## View definition

`workstreams-view.json` gains one inline `view-definition` sub-view:

- `get-entity`: an entity query filtered to `entity-type = task` whose assignment resolves to the
  **current user**. The query excludes terminal statuses by default (`completed`, `cancelled`) so the
  view shows *active* work; a toggle (see below) reveals completed/cancelled tasks.
- `relationships-to-return`: request parent/child task relationships so the client can assemble the
  hierarchy in one round-trip rather than N queries.
- `entity-type-views`: a `task` presentation that shows title, `status`, and the interest badges
  (consistent with `interests-toggleable-glyphs`).
- `disposition`: `expanded` (it is an expanded sub-view of `main`).

The "current user" is resolved from the active `WorkspacesProfile`/session context the GUI already
uses for user-scoped queries; the query is parameterized by that user entity reference rather than
hard-coding a name.

## Hierarchy assembly

Returned tasks (plus their parent/child relationships) are assembled into a forest:

- **Roots** are assigned tasks whose parent is either not a task, not assigned to the user, or absent.
- **Children** nest under their parent task when the parent is also in the result set; an assigned
  task whose parent is *not* assigned to the user surfaces as a root (so no assigned work is hidden),
  optionally annotated with its parent's title for context.
- Ordering within a level: by `status` (active first) then title; stable and deterministic.

A `WorkstreamsViewModel` (extending `ViewModelBase`, taking the injectable `Action<Action> dispatch`)
owns the assembled tree and updates it through the standard incremental-change/reset chat-sync style
already used for entity collections (no content-based dedupe). Presentation uses centralized shared
styles; nesting is shown via an indented tree, and `ScrollViewer.AllowAutoHide="False"` where a
scrollbar reserves space.

## Interaction with interests

- Tasks carrying the `not-interesting` interest are filtered out unless "show hidden items" is set,
  consistent with `not-interesting-filter`.
- The `actionable` interest is what the **inbox** view keys on (`inbox-actionable-view`); workstreams
  is the broader hierarchical view of all assigned active tasks, so the two are complementary: inbox
  = "what to act on now", workstreams = "everything I own, in context".

## Test tasks

- **Assignment representation:** a test that a task assigned to the current user is returned by the
  workstreams query and one assigned to another user is not (drives the `assigned-to` →
  reference/relationship migration).
- **Hierarchy assembly (unit):** given a flat set of tasks + parent/child relationships, assert the
  assembled forest: correct roots, correct nesting, and that an assigned task under an unassigned
  parent surfaces as a root.
- **Status filtering:** active tasks shown by default; `completed`/`cancelled` hidden until the
  toggle is set.
- **Interest filtering:** `not-interesting` tasks hidden unless "show hidden items" is set.
- **View-model (deterministic):** `WorkstreamsViewModel` with synchronous `dispatch`; incremental
  add/update/remove and full reset update the tree without content-based dedupe; no `Task.Delay`.
- **View entity validity:** `SchemaPopulatorTests` continues to pass with the populated
  `workstreams-view.json` view-definition (populate is all-or-nothing).

## Implementation steps (after approval)

1. Resolve assignment representation: migrate `assigned-to` to an entity reference/relationship and
   add the query path for "tasks assigned to user X" in the DAL (InMemory + MongoDB), with tests.
2. Populate `workstreams-view.json` with the inline `view-definition` (query + relationships +
   entity-type-views).
3. `WorkstreamsViewModel` + hierarchy assembly + tests.
4. Workstreams AXAML (indented tree, shared styles, interest badges, show-hidden + show-completed
   toggles).
5. Wire into the existing view host so `main` → `workstreams` renders the new view-model.

## Open questions

1. **Assignment migration:** confirm assignment should become a relationship to the `user` entity
   (preferred) vs an entity-reference array field; this affects the DAL query surface.
2. **Non-task ancestors:** should higher-level grouping entities (e.g. an epic/workspace) appear as
   hierarchy roots above tasks, or is the hierarchy task-only with grouping shown as a label?
3. **Default status filter:** confirm `completed`/`cancelled` are hidden by default with a toggle, vs
   shown but visually de-emphasized.
