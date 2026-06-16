# Workstreams view: task hierarchy assigned to the user

> **Status: draft — review feedback incorporated.** Defines how the `workstreams` view presents
> tasks assigned to the current user in their hierarchy. Per review feedback, there is **no bespoke
> workstreams view-model or AXAML**: the view is pure `view-definition` JSON rendered by the
> **standard view model** (`MainWindowViewModel`), which is extended to support contextual entities
> (e.g. the current user) and parent/child relationship nesting. Tracks todo
> `workstreams-task-hierarchy-view`.

## Problem & scenario

The `workstreams` view (`JsonEntities/views/workstreams-view.json`, currently a title with no
sub-views) should show the **tasks assigned to the current user**, organized in their natural
**hierarchy** (parent task → child tasks → …), and, under each task (workstream node), **all entities
related to that workstream** (notes, git worktrees, work items, sessions, …) — so the user sees their
work grouped by the larger efforts it belongs to, together with the material that belongs to each
effort, rather than as a flat list.

This reuses the existing **view mechanism** (`docs/design/llm-session.md`, `JsonSchemas/view.json`):
a view is a tree of `sub-views`, each either a reference to another view or an inline
**view-definition** carrying a `get-entity` data-access-layer query plus presentation hints
(`relationships-to-return`, `entity-type-views`). The workstreams view becomes a single inline
view-definition that queries tasks and projects them hierarchically.

## Data model

`task` entities (`JsonSchemas/task.json`) have:

- `status`: `pending | in-progress | completed | blocked | cancelled`.
- `assigned-to`: the user the task is assigned to.

Two modeling decisions (confirmed in review):

1. **Assignment is a relationship.** `assigned-to` is currently a plain `string` "reference to the
   user", which violates the entity-reference convention (references are entity-name arrays, never
   slash-joined strings). **Decision:** assignment is expressed as a **relationship** between the
   `task` and the `user` entity, matching the interests/relationships model. The legacy
   `assigned-to` string is migrated to that relationship; the query selects tasks related to the
   current user by the assignment relationship.

2. **Hierarchy is task-to-task.** Task nesting is expressed via **parent/child entity
   relationships** (the same `relationship`/`related` mechanism used elsewhere), not via name-path
   nesting, so a task can move between parents without renaming. **Decision:** a task's parent is
   always another `task` (a sub-workstream nests under its parent task); there are no non-task
   hierarchy ancestors.

3. **Workstream membership uses the `related` relationship.** Arbitrary entities are associated with
   a workstream through the existing general-purpose **`related`** relationship type
   (`JsonSchemas/related.json`: a `participants.entities` list of 2+ entity ids, unordered). This
   answers the review question "which relationship type is this?": it is `related` (a `task` and the
   member entity are co-participants in a `related` instance). The entity classifier is instructed to
   create a `related` relationship between an entity and the workstream's `task` when the entity is
   clearly part of that workstream (see "Interaction with interests / classifier").

## View definition

`workstreams-view.json` gains one inline `view-definition` sub-view — this JSON is the **primary
artifact**; everything else is a generic enhancement to the standard view model:

- `get-entity`: an entity query filtered to `entity-type = task` related to the **current user** by
  the assignment relationship (see "Contextual entities" below). The view does **not** itself filter
  out completed/cancelled tasks; stale terminal tasks are hidden via the `not-interesting` interest
  applied by the entity classifier (see "Interaction with interests").
- `relationships-to-return`: request the parent/child task relationships (for hierarchy), the
  assignment relationship, **and the `related` relationships** so the standard view model can nest
  both child tasks and related member entities under each task in one round-trip rather than N
  queries.
- `entity-type-views`: a `task` presentation that shows title, `status`, and the interest badges
  (consistent with `interests-toggleable-glyphs`), plus default presentations for the member entity
  types surfaced under a task.
- `disposition`: `expanded` (it is an expanded sub-view of `main`).

## Rendering via the standard view model (no bespoke view-model)

The standard view model `MainWindowViewModel` already renders a `view-definition`'s `get-entity`
query: `ApplySelectedViewAsync` → `TryReadSubViewGetRequest` (which already parses
`relationships-to-return`) → `LoadGetSubViewEntitiesAsync` → `EntityBroker.SubscribeGetAsync`, and
projects each result through `CreateViewEntityViewModel(entity, indentLevel, isParentContext)` into a
`ViewEntityViewModel`. The workstreams view therefore needs **no new view-model and no new AXAML** —
only two generic capabilities added to this existing path:

1. **Contextual entities.** Today a `get-entity` query is static JSON. Add a contextual-substitution
   step so a query can reference well-known context entities — the **current user** (and, where
   relevant, the current parent-context entity) — resolved from the active
   `WorkspacesProfile`/session the GUI already tracks. A query clause that today would hard-code an
   assignee instead names the current-user context placeholder, which `MainWindowViewModel` binds
   before issuing the `GetRequest`. This is reusable by any view (e.g. the inbox), not workstreams-specific.

2. **Parent/child + membership nesting.** `ViewEntityViewModel` already carries an `indentLevel`, but
   the view-definition path currently emits every result at `indentLevel: 0`. Extend it so that, when
   a `view-definition` returns `relationships-to-return`, the standard view model assembles the
   returned entities into a forest and emits them depth-first with increasing `indentLevel` (roots at
   0, children at 1, …). Two relationship roles drive nesting: parent/child **task** relationships
   nest sub-tasks under their parent task, and **`related`** relationships nest member entities under
   the task they belong to. Roots are results whose parent is absent or not in the result set; a
   returned task whose parent is not in the set surfaces as a root (no assigned work is hidden).
   Ordering within a level is stable (`status` active-first, then title). This nesting is generic —
   driven entirely by `relationships-to-return` — so any hierarchical view benefits.

Both enhancements live in `MainWindowViewModel`'s view-definition handling and the shared
`ViewEntityViewModel`; presentation continues to use the existing centralized styles and the
`ScrollViewer.AllowAutoHide="False"` convention already in the view's AXAML.

## Interaction with interests and the entity classifier

- Tasks carrying the `not-interesting` interest are filtered out unless "show hidden items" is set,
  consistent with `not-interesting-filter`.
- **Completed/cancelled tasks are aged out by the entity classifier, not a UI toggle.** Per review
  decision, the entity classifier's task instructions mark a task with the `not-interesting` interest
  when it has been in a terminal state (`completed` or `cancelled`) **and** has not been modified for
  a week. Those tasks then drop out of the workstreams view via the `not-interesting` filter, while
  recently-closed tasks remain visible.
- **Workstream membership is established by the classifier.** The entity classifier is instructed
  that when an entity is clearly part of a workstream, it should associate that entity with the
  corresponding `task` via a **`related`** relationship. The workstreams view then surfaces those
  members under the task (via `relationships-to-return`).
- **`actionable` / `blocked` are user-scoped interests.** The inbox (`inbox-actionable-view`) shows
  **all entities** — not only tasks — that carry the `actionable` interest. This requires new
  interest types `actionable` (by a user) and `blocked` (by a user), each allowing a `user`
  participant so they apply per user. Each entity type's classifier instructions must specify when an
  entity of that type becomes actionable / not-actionable / blocked / not-blocked. These rules live in
  the entity classifier instructions (todo `classifier-interest-instructions`) and the interest-type
  definitions (todo `actionable-blocked-interest-types`), keeping the views themselves free of
  type-specific logic.
- workstreams = "everything I own, in context (with its related material)"; inbox = "everything
  actionable right now (any type)" — the two are complementary.

## Test tasks

- **Assignment representation:** a test that a task related to the current user by the assignment
  relationship is returned by the workstreams query and one assigned to another user is not (drives
  the `assigned-to` string → user-relationship migration).
- **Contextual entities (view-model):** `MainWindowViewModel` substitutes the current-user context
  into a `view-definition` query before issuing the `GetRequest`; a task assigned to a different user
  is not returned. Deterministic, synchronous `dispatch`, no `Task.Delay`.
- **Relationship nesting (view-model):** given a `view-definition` result with parent/child
  `relationships-to-return`, the standard view model emits `ViewEntityViewModel`s with the expected
  `indentLevel`s — correct roots, correct nesting, and a returned task under an out-of-set parent
  surfacing as a root.
- **Classifier aging (in `classifier-interest-instructions`):** a completed/cancelled task not
  modified for over a week is marked `not-interesting`; a recently-closed one is not.
- **Interest filtering:** `not-interesting` tasks hidden unless "show hidden items" is set.
- **View entity validity:** `SchemaPopulatorTests` continues to pass with the populated
  `workstreams-view.json` view-definition (populate is all-or-nothing).

## Implementation steps (after approval)

1. Migrate assignment to a `task`→`user` relationship and add the query path for "tasks assigned to
   user X" (assignment relationship) in the DAL (InMemory + MongoDB), with tests.
2. Add **contextual-entity substitution** to `MainWindowViewModel`'s view-definition query handling
   (current-user, current parent-context), with tests.
3. Add **relationship-driven nesting** (forest assembly → depth-first `indentLevel`) to the standard
   view-definition rendering path, with tests.
4. Populate `workstreams-view.json` with the inline `view-definition` (query relating tasks to the
   current-user context + `relationships-to-return` for parent/child + `entity-type-views`).
5. Add the stale-terminal-task → `not-interesting` rule to the entity classifier instructions
   (todo `classifier-interest-instructions`).

## Resolved decisions (from review)

1. **Assignment** is a relationship from the `task` to the `user` entity (not a string field).
2. **Hierarchy** is task-to-task: a task's parent is always another `task`; there are no non-task
   ancestors.
3. **Workstream membership** uses the existing **`related`** relationship type (confirmed in review):
   the classifier associates an entity with a workstream by creating a `related` relationship between
   the entity and the workstream's `task`; the view shows all such members under the task.
4. **Completed/cancelled aging** is handled by the entity classifier: a terminal task unmodified for
   over a week is marked `not-interesting`, which hides it via the `not-interesting` filter — there
   is no separate show-completed toggle in the view.
5. **No bespoke view-model/AXAML.** Use the ordinary view's model. The standard view model
   (`MainWindowViewModel` + `ViewEntityViewModel` + the `view-definition` JSON) is extended
   generically to support contextual entities and parent/child + `related` nesting; the workstreams
   view is then pure configuration.
6. **`actionable` / `blocked` interests (by a user)** are new interest types (todo
   `actionable-blocked-interest-types`); the inbox shows **all entities** carrying `actionable` (any
   type, not just tasks), and each entity type's classifier instructions define its
   actionable/not-actionable/blocked/not-blocked transitions (todo `classifier-interest-instructions`).
