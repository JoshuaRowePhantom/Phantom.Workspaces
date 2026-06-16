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

## View definition

`workstreams-view.json` gains one inline `view-definition` sub-view — this JSON is the **primary
artifact**; everything else is a generic enhancement to the standard view model:

- `get-entity`: an entity query filtered to `entity-type = task` related to the **current user** by
  the assignment relationship (see "Contextual entities" below). The view does **not** itself filter
  out completed/cancelled tasks; stale terminal tasks are hidden via the `not-interesting` interest
  applied by the entity classifier (see "Interaction with interests").
- `relationships-to-return`: request parent/child task relationships (and the assignment
  relationship) so the standard view model can nest the results in one round-trip rather than N
  queries.
- `entity-type-views`: a `task` presentation that shows title, `status`, and the interest badges
  (consistent with `interests-toggleable-glyphs`).
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

2. **Parent/child relationship nesting.** `ViewEntityViewModel` already carries an `indentLevel`, but
   the view-definition path currently emits every result at `indentLevel: 0`. Extend it so that, when
   a `view-definition` returns `relationships-to-return` parent/child links, the standard view model
   assembles the returned entities into a forest and emits them depth-first with increasing
   `indentLevel` (roots at 0, children at 1, …). Roots are results whose parent is absent or not in
   the result set; a returned task whose parent is not in the set surfaces as a root (no assigned work
   is hidden). Ordering within a level is stable (`status` active-first, then title). This nesting is
   generic — driven entirely by `relationships-to-return` — so any hierarchical view benefits.

Both enhancements live in `MainWindowViewModel`'s view-definition handling and the shared
`ViewEntityViewModel`; presentation continues to use the existing centralized styles and the
`ScrollViewer.AllowAutoHide="False"` convention already in the view's AXAML.

## Interaction with interests

- Tasks carrying the `not-interesting` interest are filtered out unless "show hidden items" is set,
  consistent with `not-interesting-filter`.
- **Completed/cancelled tasks are aged out by the entity classifier, not a UI toggle.** Per review
  decision, the entity classifier's task instructions mark a task with the `not-interesting` interest
  when it has been in a terminal state (`completed` or `cancelled`) **and** has not been modified for
  a week. Those tasks then drop out of the workstreams view via the `not-interesting` filter, while
  recently-closed tasks remain visible. This rule belongs in the entity classifier instructions (todo
  `classifier-interest-instructions`), keeping the workstreams view itself free of status-specific
  filtering logic.
- The `actionable` interest is what the **inbox** view keys on (`inbox-actionable-view`); workstreams
  is the broader hierarchical view of all assigned tasks, so the two are complementary: inbox =
  "what to act on now", workstreams = "everything I own, in context".

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
3. **Completed/cancelled aging** is handled by the entity classifier: a terminal task unmodified for
   over a week is marked `not-interesting`, which hides it via the `not-interesting` filter — there
   is no separate show-completed toggle in the view.
4. **No bespoke view-model/AXAML.** Use the ordinary view's model. The standard view model
   (`MainWindowViewModel` + `ViewEntityViewModel` + the `view-definition` JSON) is extended
   generically to support contextual entities and parent/child relationship nesting; the workstreams
   view is then pure configuration. (Incorporated throughout this document.)
