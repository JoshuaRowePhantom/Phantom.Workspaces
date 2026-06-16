# Workstreams view: task hierarchy assigned to the user

> **Status: draft — review feedback incorporated.** Defines how the `workstreams` view presents
> tasks assigned to the current user in their hierarchy. Per review feedback, there is **no bespoke
> workstreams view-model or AXAML**: the view is pure `view-definition` JSON. Current-user context is
> resolved in the **query DAL layer** via the session object; the **standard view model**
> (`MainWindowViewModel`) is extended only to nest parent/child tasks and `related` members. Tracks
> todo `workstreams-task-hierarchy-view`.

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
- `assigned-to`: a **source-system-dependent** string carried over from the originating system (e.g.
  an Azure DevOps assignee). Its meaning varies by source; the task schema description is updated to
  say so. This raw field is **not** queried directly by the view — it is the input the classifier
  uses to choose which user to assign (below).

Modeling decisions (confirmed in review):

1. **Assignment is expressed as an `assigned-to` interest, derived from the source field.** The
   current user's tasks are those carrying an **`assigned-to` interest** that references that user
   (interests are user-scoped relationships per `docs/design/interests.md`). The raw `assigned-to`
   string is retained (source-system dependent). The entity classifier's **task** instructions say:
   for a task that does not yet have an `assigned-to` interest, choose the user to assign based on its
   `assigned-to` source field and apply the `assigned-to` interest to that user. The workstreams query
   then selects tasks by the `assigned-to` interest for the current user, using the **existing
   query/get syntax** (interest/participation clauses), extended only if necessary — no bespoke
   migration. This requires an `assigned-to` (by-user) interest type (todo
   `actionable-blocked-interest-types` covers the new by-user interest types).

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

- `get-entity`: an entity query filtered to `entity-type = task` carrying the **`assigned-to`
  interest** for the **current user** (see "Contextual entities" below), using the existing
  query/get syntax (interest/participation clauses). The view does **not** itself filter out
  completed/cancelled tasks; stale terminal tasks are hidden via the `not-interesting` interest
  applied by the entity classifier (see "Interaction with interests").
- `relationships-to-return`: request the parent/child task relationships (for hierarchy) **and the
  `related` relationships** so the standard view model can nest both child tasks and related member
  entities under each task in one round-trip rather than N queries.
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

1. **Contextual entities (resolved in the query/DAL layer, not the view model).** A `get-entity`
   query needs to reference well-known context entities — the **current user** (and, where relevant,
   the current profile/computer). Per review, this resolution belongs in the **query data-access
   layer**, where a **session object** already carries the current user / profile / computer, rather
   than in `MainWindowViewModel`. A query clause names the current-user context placeholder; the DAL
   binds it from the session when executing the query. This keeps contextual resolution server-side
   and reusable by any caller (GUI views, the inbox, agents), not just the view model.

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

Of these, **contextual resolution lives in the query/DAL layer** (session-bound), while the
**relationship nesting** lives in `MainWindowViewModel`'s view-definition handling and the shared
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

- **Assignment via `assigned-to` interest:** a test that a task carrying the `assigned-to` interest
  for the current user is returned by the workstreams query and one assigned to another user is not.
- **Classifier derives assignment (in `classifier-interest-instructions`):** a task with no
  `assigned-to` interest but with a source `assigned-to` field gets the `assigned-to` interest applied
  to the user chosen from that field.
- **Contextual entities (DAL/session):** the query layer binds the current-user context from the
  session object so the workstreams query returns only the current user's tasks; a task assigned to a
  different user is not returned. Deterministic, no `Task.Delay`.
- **Relationship nesting (view-model):** given a `view-definition` result with parent/child and
  `related` `relationships-to-return`, the standard view model emits `ViewEntityViewModel`s with the
  expected `indentLevel`s — correct roots, correct nesting, member entities nested under their task,
  and a returned task under an out-of-set parent surfacing as a root.
- **Classifier aging (in `classifier-interest-instructions`):** a completed/cancelled task not
  modified for over a week is marked `not-interesting`; a recently-closed one is not.
- **Interest filtering:** `not-interesting` tasks hidden unless "show hidden items" is set.
- **View entity validity:** `SchemaPopulatorTests` continues to pass with the populated
  `workstreams-view.json` view-definition (populate is all-or-nothing).

## Implementation steps (after approval)

1. Add the `assigned-to` (by-user) interest type and update the entity classifier's **task**
   instructions to derive it from the source-system `assigned-to` field when missing. Update the
   `task` schema description to note the field's meaning is source-system dependent. Use the existing
   query/get syntax to select tasks by the `assigned-to` interest for a user (extend the clause set
   only if necessary), with tests (InMemory + MongoDB).
2. Resolve the **current-user context in the query DAL layer** from the session object (current user /
   profile / computer); bind a current-user placeholder in the query, with tests.
3. Add **relationship-driven nesting** (forest assembly → depth-first `indentLevel`, parent/child +
   `related`) to the standard view-definition rendering path, with tests.
4. Populate `workstreams-view.json` with the inline `view-definition` (query selecting tasks by the
   current-user `assigned-to` interest + `relationships-to-return` for parent/child + `related` +
   `entity-type-views`).
5. Add the stale-terminal-task → `not-interesting` rule and the workstream-membership `related`
   rule to the entity classifier instructions (todo `classifier-interest-instructions`).

## Resolved decisions (from review)

1. **Assignment** is expressed as an `assigned-to` (by-user) **interest**, derived by the entity
   classifier from the retained, **source-system-dependent** `assigned-to` field; the workstreams
   query selects tasks by that interest using the existing query/get syntax (no bespoke migration).
2. **Hierarchy** is task-to-task: a task's parent is always another `task`; there are no non-task
   ancestors.
3. **Workstream membership** uses the existing **`related`** relationship type (confirmed in review):
   the classifier associates an entity with a workstream by creating a `related` relationship between
   the entity and the workstream's `task`; the view shows all such members under the task.
4. **Completed/cancelled aging** is handled by the entity classifier: a terminal task unmodified for
   over a week is marked `not-interesting`, which hides it via the `not-interesting` filter — there
   is no separate show-completed toggle in the view.
5. **No bespoke view-model/AXAML.** Use the ordinary view's model. Contextual current-user resolution
   lives in the **query DAL layer** via the session object (not `MainWindowViewModel`); the standard
   view model adds only parent/child + `related` nesting; the workstreams view is then pure
   configuration.
6. **`actionable` / `blocked` interests (by a user)** are new interest types (todo
   `actionable-blocked-interest-types`); the inbox shows **all entities** carrying `actionable` (any
   type, not just tasks), and each entity type's classifier instructions define its
   actionable/not-actionable/blocked/not-blocked transitions (todo `classifier-interest-instructions`).
