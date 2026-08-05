# Design: view-schema-grouping

> **Bug title prefix:** `[view-schema-grouping]`
> **Authoritative source root:** `C:\dev\Phantom.Workspaces-LLM` (verified against `features` — LLM has the current `JsonEntities/`, `JsonSchemas/`, and view-renderer code; the `features` worktree mirrors the same tree under `C:\dev\Phantom.Workspaces-Skills\features\Phantom.Workspaces.Data.Core\…`).
> **Repository for bugs:** `JoshuaRowePhantom/Phantom.Workspaces`.

---

## Requirements

The Sessions view (canonical name `["views","sessions"]`) currently lists three flat sections — agent-manifests, agent-definitions, and agent-sessions — each preceded by a `note` entity used as a section header. The owner wants the child entities to be **grouped underneath the appropriate parent node** so a session appears under its definition (or manifest), and (subject to Open Question 2) a definition appears under its manifest. The mechanism must be expressed **generically in the view schema** so any future view can declare the same behaviour without new C# code.

### Unambiguous requirements

1. **R1 — Grouping in the Sessions view.** After this feature, `views/sessions` renders as a tree: each `agent-session` whose `agent-definition-reference` targets an `agent-definition` or `agent-manifest` MUST appear as a child of that parent node in the sticky-parent hierarchy. Sub-agent sessions (those with `parent-agent-session-ids[0]`) MUST continue to be excluded from the top-level query, unchanged.
2. **R2 — Definition-under-manifest grouping (subject to Open Q2).** Where an `agent-definition` can be resolved back to an owning `agent-manifest`, the definition MUST appear as a child of that manifest in the tree.
3. **R3 — Generic view-schema construct.** The grouping behaviour MUST be expressed as a declarative construct in the view schema (JSON), reusable by any view/entity-type-view; it MUST NOT be hardcoded for the Sessions view.
4. **R4 — Relationship-based grouping.** The construct MUST support declaring the parent-child edge as either a **field/property reference** (existing precedent) OR a **relationship traversal** (relationship-type + role) so relationships modelled as first-class relationship entities work too.
5. **R5 — Backwards-compatible default.** When no grouping is declared on an entity-type-view, the view MUST render exactly as it does today (flat list, filtered by the sub-view query). Existing views (`git-workspaces`, `workspaces`, `inbox`, `pull-requests`, `workstreams`) MUST not change behaviour.
6. **R6 — Reuse existing tree machinery.** The grouped tree MUST be produced by the existing `EntityListItemViewModel` (`Level` / `ParentItemKey` / `ChildItemKeys` / `StickyRow`) plus `EntityBrowserWorkspaceTabViewModel` render path. No new UI control.
7. **R7 — Multi-level (chained) hierarchy.** Manifest → Definition → Session (three levels) MUST work by declaring grouping on each intermediate entity-type-view; the assembler must chain them into a single tree.
8. **R8 — Synthesised parent nodes.** When a leaf entity's declared parent is not currently loaded by the view query, a synthesized/loaded parent node MUST appear (analogous to `AncestorSynthesizer`), so grouping does not silently drop items.
9. **R9 — Tests.** Unit tests MUST cover: schema parsing of the new grouping shape, per-source (field vs relationship) parent-key extraction, chained multi-level grouping, backwards-compat (no grouping declared → flat), and the Sessions view integration case. Naming: `<Subject>Tests` with `Subject_Scenario_ExpectedOutcome` (per existing `EntityListItemViewModelTests`, `EntityBrowserWorkspaceTabViewModelTests`, `SessionsViewParentFilterTests`).

### Assumptions

* **A1.** The view schema at `Phantom.Workspaces.Data.Core\JsonSchemas\view.json` and the per-type presentation at `entity-type-view.json` are the correct places to extend. `entity-type-view.json` already carries `group-by-parent` and `traverse-relationships` (verified below), so the extension is additive.
* **A2.** `EntityListItemViewModel` already exposes the fields needed for a grouped, sticky-parent tree (`Level`, `ParentItemKey`, `ChildItemKeys`, `StickyRow`, `IndentMargin`). Verified in `Phantom.Workspaces\ViewModels\EntityListItemViewModel.cs`.
* **A3.** `agent-session.agent-definition-reference` is the correct edge to follow session→definition. Its `x-entity-types` in `JsonSchemas\agent-session.json` is `["agent-manifest","agent-definition"]`, so the grouping declaration must accept multiple candidate parent types.
* **A4.** The three existing section-header `note` entities in `sessions-view.json` (`views/sessions/notes/agent-manifests`, `.../agent-definitions`, `.../agent-sessions`) remain as top-level section headers. Grouping happens *inside* each section, not by replacing the notes. (See Open Q1 for the alternative reading.)

### Open Questions

* **Open Q1 — "note" vs "node" in the owner's request.** The owner wrote "underneath the appropriate note". The Sessions view really does have three top-level `note` entities acting as section headers (`sessions-view.json` sub-views 1/3/5). Two readings:
  * **(a) "node" (typo).** Group each child under its true parent entity (session → definition → manifest). This is the primary interpretation used in the design below.
  * **(b) literal "note".** Each of the three sections should stay as-is but visually anchor its items under the section-header note (i.e. the note becomes a synthesized parent node of the sub-view's rows). The proposed generic construct can express this too — the entity-type-view for `agent-manifest` etc. would declare a `group-by-parent` whose target is the specific section-header note entity — but the owner should confirm.
* **Open Q2 — definition→manifest edge.** `agent-manifest.json` carries a `manifest-reference` used for sub-agent dispatch, but there is no field on `agent-definition` pointing to its owning `agent-manifest` in the schemas surveyed. If definitions do not have a resolvable back-link to a manifest, R2 either (i) needs a new property added to `agent-definition`, or (ii) needs a relationship entity (e.g. `agent-manifest-of-definition`) added, or (iii) R2 is dropped and manifests/definitions remain as sibling top-level items. Owner decision required.
* **Open Q3 — parent inclusion when target entity is out of query.** For R8 the assembler must materialise a parent node whose entity was not returned by the sub-view's `query`. Should the assembler (a) issue a supplementary `get-entity` for the parent, or (b) require the view definition to include the parent's `entity-type-names` in its query, or (c) fall back to a "no parent — orphan bucket" group? Recommendation: (a), matching how `AncestorSynthesizer` already synthesises missing ancestors.

---

## Options

Four ways to express grouping generically in the view schema. All four reuse `EntityListItemViewModel`'s existing hierarchy fields; they differ in *where* the parent/child edges are declared and *how* they are computed.

### Option A — Extend the existing `group-by-parent` block on `entity-type-view` with a `source` discriminator

**Architecture.** Today `entity-type-view.group-by-parent` already exists (see `Phantom.Workspaces.Data.Core\JsonSchemas\entity-type-view.json:57-78`) and is applied by the view assembler for the git-workspaces precedent (`git-worktree-entity-type-view.json` groups worktrees under `user-computer-profile` via `field-path: ["computer-user-profile-id"]`). Extend the schema so `source` is a discriminator: `"field"` (existing) or `"relationship"` (new). For `"relationship"`, the block carries `relationship-type-names` and `relationship-role-names` and the assembler traverses the relationships-to-return graph instead of reading a field. Multi-level (manifest → definition → session) composes by declaring grouping on each intermediate entity-type-view.

**Pros.**
* Reuses the exact construct the codebase already documents and implements for git-workspaces sticky-parent.
* Purely additive to the schema — Option E (compat) trivially preserved; unmodified entity-type-views keep working.
* Fits `EntityListItemViewModel`'s `Level`/`ParentItemKey`/`ChildItemKeys` directly — no new VM shape.
* Chained grouping (multi-level) is the emergent behaviour of applying it per-type — matches `AncestorSynthesizer`'s composable model.
* Relationship-based mode maps cleanly to the existing `relationships-to-return` request in `workspace-entities-data-access-layer.json`; the data the assembler needs is already available.

**Cons.**
* Splits grouping across many small `entity-type-view` JSONs instead of one central declaration on the view itself — harder to see the whole hierarchy at a glance. *Mitigation:* documentation + the assembler emits a debug dump.
* Field-mode already collides with relationship-mode ergonomically (`field-path` is only meaningful for `field`). *Mitigation:* use a JSON Schema `oneOf` on `source` so bad shapes are rejected at load.

### Option B — Add a `hierarchy` block on the view definition itself

**Architecture.** Add a new top-level `hierarchy` property on the view definition (`view.json`) that lists levels `[ { entity-type, parent-from: { source, ... } }, ... ]` centrally on the view. The view assembler reads it, matches each returned entity to its level, and traverses the declared edge to place items.

**Pros.**
* Single, readable place per view — the whole tree is visible in one JSON block.
* Independent of `entity-type-view` shape.

**Cons.**
* Duplicates or contradicts the existing `entity-type-view.group-by-parent` construct — two grouping systems.
* Doesn't reuse the git-workspaces sticky-parent precedent.
* Non-generic across views: a view that reuses the same entity type must re-declare the level.

### Option C — Query-time nesting via `enumerate-children` + `relationships-to-return`

**Architecture.** Extend the sub-view's `query`/`get-entity` to return a hierarchy directly using the existing `enumerate-children` (`self`/`children`/`all-children`) and `relationships-to-return`. The renderer treats the returned nested entity structure as the tree and flattens it into `EntityListItemViewModel`s.

**Pros.**
* Uses the data-access layer's built-in hierarchy traversal — nothing new in the schema at all for simple cases.
* Naturally handles arbitrary depth.

**Cons.**
* `enumerate-children` is built on **entity-name hierarchy**, not on user-facing relationships like `agent-definition-reference`. It cannot express "session under its definition" unless the underlying entity names are already parented that way (they aren't).
* No control over which types get promoted to grouping nodes vs shown as leaves.
* Would require server changes to synthesise cross-relationship children, defeating the "declarative in the view schema" goal.

### Option D — View-model-side hardcoding (baseline to reject)

**Architecture.** Add specific code in `EntityBrowserWorkspaceTabViewModel` (or a new `SessionsGroupingBuilder`) that recognises `agent-session` items and reparents them under their `agent-definition-reference`. No schema change.

**Pros.**
* Smallest immediate diff; fastest to ship for Sessions only.

**Cons.**
* **Not generic** — violates R3. Every future view needing grouping repeats the work.
* Divergence from the git-workspaces precedent, which is already schema-driven.
* Adds a hardcoded knowledge of agent entity relationships to a general-purpose view renderer.

### Recommendation

**Option A.** It is a direct, additive extension of an existing, working, schema-driven construct (`entity-type-view.group-by-parent`). It reuses `EntityListItemViewModel`, matches the git-workspaces sticky-parent precedent, satisfies R3/R4/R5/R6/R7 with the smallest surface change, and composes naturally into multi-level chains for R7.

---

## Chosen design

**Approach:** Option A — extend `entity-type-view.group-by-parent` with a `source` discriminator (`field` | `relationship`) and add a chained multi-level assembler pass.

**Rationale.**
* Reuses the only construct in the codebase that is already schema-driven grouping — the git-worktree precedent (`git-worktree-entity-type-view.json`) shows this works end-to-end. Extending it costs one JSON `oneOf` and one C# discriminated model.
* Backwards-compat (R5) is free: today's `group-by-parent` shape parses as `source: "field"` with a small default rule during load.
* Cons of Option A (grouping spread across many small files) are mitigated by (i) validating shapes with JSON Schema `oneOf`, and (ii) a debug helper (`ViewHierarchyAssembler.Describe`) that dumps the effective chain when a view is opened in dev mode.

### Schema change — generic form

Change `entity-type-view.json` `group-by-parent` from a single-shape object to a `oneOf` on `source`:

```jsonc
// Phantom.Workspaces.Data.Core/JsonSchemas/entity-type-view.json
"group-by-parent": {
  "description": "When present, the view assembler groups leaf entities under a parent node resolved from the declared source. Chain by declaring group-by-parent on the parent's entity-type-view too.",
  "oneOf": [
    {
      "type": "object",
      "properties": {
        "source": { "const": "field" },
        "field-path": { "$ref": "core.json#/$defs/field-path" },
        "parent-entity-type-names": {
          "type": "array",
          "items": { "$ref": "core.json#/$defs/entity-type-id" }
        }
      },
      "required": ["source", "field-path", "parent-entity-type-names"],
      "unevaluatedProperties": false
    },
    {
      "type": "object",
      "properties": {
        "source": { "const": "relationship" },
        "relationship-type-names": {
          "type": "array",
          "items": { "$ref": "core.json#/$defs/entity-type-id" }
        },
        "relationship-role-names": {
          "type": "array",
          "items": { "type": "string" },
          "description": "Roles on the relationship that identify the PARENT participant (the child is the entity under grouping)."
        },
        "parent-entity-type-names": {
          "type": "array",
          "items": { "$ref": "core.json#/$defs/entity-type-id" }
        }
      },
      "required": ["source", "relationship-type-names", "relationship-role-names", "parent-entity-type-names"],
      "unevaluatedProperties": false
    }
  ]
}
```

### Concrete Sessions-view application

Two new (or one modified) entity-type-views, plus no change to `sessions-view.json` itself except optionally adding `relationships-to-return` when relationship-mode is chosen.

**`JsonEntities\entity-type-views\agent-session-entity-type-view.json` (new)**
```jsonc
{
  "entity-id": "<new-guid>",
  "entity-types": ["entity", "entity-type-view"],
  "names": [["entity-type-views", "agent-session"]],
  "display-name": { "default": "Agent Session View" },
  "fields": [
    { "field-path": ["agent-definition-reference"] },
    { "field-path": ["sub-agent-description"] }
  ],
  "group-by-parent": {
    "source": "field",
    "field-path": ["agent-definition-reference"],
    "parent-entity-type-names": ["agent-definition", "agent-manifest"]
  }
}
```

**`JsonEntities\entity-type-views\agent-definition-entity-type-view.json` (new, gated on Open Q2)**
```jsonc
{
  "entity-id": "<new-guid>",
  "entity-types": ["entity", "entity-type-view"],
  "names": [["entity-type-views", "agent-definition"]],
  "display-name": { "default": "Agent Definition View" },
  "group-by-parent": {
    // Illustrative: exact shape depends on Open Q2's resolution.
    "source": "relationship",
    "relationship-type-names": ["agent-manifest-of-definition"],
    "relationship-role-names": ["manifest"],
    "parent-entity-type-names": ["agent-manifest"]
  }
}
```

If Open Q2 resolves that no definition→manifest edge exists, drop this file and Sessions renders as a two-level tree: manifests (flat), definitions (flat), sessions (grouped under def/manifest).

---

## Detailed design

### Code organisation

Authoritative paths shown from `Phantom.Workspaces-LLM`; `features` mirrors the same tree.

**New files:**
* `Phantom.Workspaces.Data.Core\JsonEntities\entity-type-views\agent-session-entity-type-view.json`
* `Phantom.Workspaces.Data.Core\JsonEntities\entity-type-views\agent-definition-entity-type-view.json` *(gated on Open Q2)*
* `Phantom.Workspaces\ViewModels\GroupByParentResolver.cs` — resolves the parent entity-id for one leaf entity, per `source`.
* `Phantom.Workspaces.Tests\GroupByParentResolverTests.cs`
* `Phantom.Workspaces.Tests\ViewHierarchyAssemblerGroupingTests.cs`
* `Phantom.Workspaces.Data.Core.Tests\EntityTypeViewGroupByParentSchemaTests.cs`
* `Phantom.Workspaces.Data.Offline.Tests\SessionsViewGroupingTests.cs`

**Modified files:**
* `Phantom.Workspaces.Data.Core\JsonSchemas\entity-type-view.json` — `group-by-parent` becomes `oneOf` on `source`.
* `Phantom.Workspaces.Data.Core\` model type for `entity-type-view` (search: `EntityTypeView`, `EntityTypeViewGroupByParent`) — split into `EntityTypeViewGroupByParentFieldSource` / `EntityTypeViewGroupByParentRelationshipSource`, `GroupByParentSource` enum.
* `Phantom.Workspaces\ViewHierarchyAssembler.cs` — extend to consume the new discriminated union; chain assemblers when a parent's own entity-type-view also declares `group-by-parent`.
* `Phantom.Workspaces\ViewModels\EntityBrowserWorkspaceTabViewModel.cs` — feed `GroupByParentResolver` output into `AddItemsDepthFirst` so synthesized parent nodes get `Level`/`ParentItemKey`/`ChildItemKeys` correctly.
* `Phantom.Workspaces.Data.Core\JsonEntities\views\sessions-view.json` — add `relationships-to-return` for the definition/manifest relationship types if Option Q2 chooses relationship-mode.

### Classes and interfaces

#### `EntityTypeViewGroupByParent` (modified)

**Namespace:** `Phantom.Workspaces.Data.Core.JsonEntities` (matching the existing `EntityTypeView` model)
**Kind:** discriminated record (`abstract record` + `sealed record` variants)
**Responsibility:** Strongly-typed model for the JSON `group-by-parent` block after `oneOf` split.

**Members:**
* `GroupByParentSource Source { get; }` — enum: `Field`, `Relationship`.
* `IReadOnlyList<EntityTypeId> ParentEntityTypeNames { get; }` — candidate parent types.
* Variant `FieldSource`: `FieldPath FieldPath { get; }`.
* Variant `RelationshipSource`: `IReadOnlyList<EntityTypeId> RelationshipTypeNames { get; }`, `IReadOnlyList<string> RelationshipRoleNames { get; }`.

#### `GroupByParentResolver` (new)

**Namespace:** `Phantom.Workspaces.ViewModels`
**Kind:** sealed class
**Responsibility:** Given one child `SubscribedEntityViewModel` and its `EntityTypeViewGroupByParent`, return the target parent `EntityId` (or `null` if unresolvable).

**Members:**
* `EntityId? Resolve(SubscribedEntityViewModel child, EntityTypeViewGroupByParent groupBy, IRelationshipLookup relationships)` — dispatches on `Source`.
* Private `ResolveField(...)` — reads the field-path from the child entity's JSON body, coerces to `EntityId`.
* Private `ResolveRelationship(...)` — inspects the entity's returned relationships (from `relationships-to-return`) filtered by `RelationshipTypeNames`, and returns the participant with a matching `RelationshipRoleNames` role.

#### `ViewHierarchyAssembler` (modified)

**Namespace:** `Phantom.Workspaces`
**Kind:** static class (extension to the existing file that hosts `AncestorSynthesizer`)
**Responsibility:** Given the flat entity set returned by a sub-view query and the effective `EntityTypeView`s per type, build a tree of `EntityListNodeViewModel`s with synthesized parent nodes.

**Members:**
* `IReadOnlyList<EntityListNodeViewModel> AssembleGrouped(IReadOnlyList<SubscribedEntityViewModel> entities, IReadOnlyDictionary<EntityTypeId, EntityTypeView> entityTypeViews, IRelationshipLookup relationships, IParentEntityLoader parentLoader)` — main entry.
* Private `ChainGroupBy(...)` — walks the chain: while the current parent's own entity-type-view also declares `group-by-parent`, resolve *its* parent and repeat, until reaching a type with no grouping.
* Private `SynthesizeMissingParent(...)` — for Open Q3(a), asks `parentLoader` to fetch a parent entity when it wasn't in the query results.

#### `EntityBrowserWorkspaceTabViewModel` (modified)

Change to `RebuildTreeAsync` / `BuildChildrenAsync` (`Phantom.Workspaces\ViewModels\EntityBrowserWorkspaceTabViewModel.cs:105-255`):
* Before the existing name-hierarchy tree assembly, call `ViewHierarchyAssembler.AssembleGrouped` for each sub-view whose returned entities include any type with `group-by-parent`.
* Preserve the existing name-hierarchy path for backwards-compat when no `group-by-parent` applies (R5).

### Data flow

1. **Load.** `views/sessions` (unchanged JSON) is opened. `EntityBrowserWorkspaceTabViewModel` runs each sub-view's `query`/`get-entity`. For the sessions sub-view, the request additionally sets `relationships-to-return` if any grouping in scope is `source: relationship`.
2. **Per-type views.** For each type present in the results, the effective `EntityTypeView` is looked up (existing behaviour). New: if it carries `group-by-parent`, that block is attached.
3. **Resolve parents.** `GroupByParentResolver.Resolve` is called for each leaf entity, producing `(childId, parentId)` pairs. Field-source reads `child.Data[field-path]`; relationship-source looks up the participant on the returned relationship entities.
4. **Chain.** `ViewHierarchyAssembler.ChainGroupBy` walks upward: if the parent's own entity-type-view has `group-by-parent`, resolve *its* parent, and so on. Missing entities are fetched via `IParentEntityLoader`.
5. **Assemble.** Nodes are wired into `EntityListNodeViewModel`s. `AddItemsDepthFirst` (already existing) flattens the tree, assigning each `EntityListItemViewModel` its `Level`, `ParentItemKey`, `ChildItemKeys`, `IndentMargin`, and `StickyRow` — no changes to `EntityListItemViewModel` itself.
6. **Render.** Existing `entity-card-tree` / `EntityCardTreeView` renders the sticky-parent tree, identical mechanics to git-workspaces.

For the Sessions view specifically, the resulting tree looks like:

```
[note] agent-manifests
  ├─ agent-manifest A          (Level 0)
  │    └─ agent-definition A1  (Level 1, if Q2 resolves)
  │         └─ agent-session S1 (Level 2)
  │    └─ agent-session S2      (Level 1, session pointing directly at manifest)
[note] agent-definitions
  └─ …                         (definitions not owned by any listed manifest fall back to flat)
[note] agent-sessions
  └─ …                         (top-level sessions whose parent isn't in view fall to orphan bucket)
```

### Tests

Naming convention: `<Subject>Tests` class, `Method_Scenario_ExpectedOutcome` per existing `EntityListItemViewModelTests`, `EntityBrowserWorkspaceTabViewModelTests`, `SessionsViewParentFilterTests`.

#### `EntityTypeViewGroupByParentSchemaTests` (in `Phantom.Workspaces.Data.Core.Tests`)
* `GroupByParent_FieldSourceShape_ParsesIntoFieldSourceVariant`
* `GroupByParent_RelationshipSourceShape_ParsesIntoRelationshipSourceVariant`
* `GroupByParent_MissingSource_FailsSchemaValidation`
* `GroupByParent_FieldSourceWithRelationshipFields_FailsSchemaValidation`
* `GroupByParent_LegacyShapeWithoutSource_DefaultsToFieldSource` *(compat)*

#### `GroupByParentResolverTests` (in `Phantom.Workspaces.Tests`)
* `Resolve_FieldSourceWithEntityReference_ReturnsReferencedEntityId`
* `Resolve_FieldSourceMissingField_ReturnsNull`
* `Resolve_RelationshipSourceWithMatchingRole_ReturnsParentParticipantId`
* `Resolve_RelationshipSourceWithNoMatchingRelationship_ReturnsNull`
* `Resolve_ParentEntityTypeNotInAllowedList_ReturnsNull`

#### `ViewHierarchyAssemblerGroupingTests` (in `Phantom.Workspaces.Tests`)
* `AssembleGrouped_NoGroupByParent_ReturnsFlatList`
* `AssembleGrouped_SingleLevelFieldGrouping_ProducesTwoLevelTree`
* `AssembleGrouped_ChainedMultiLevelGrouping_ProducesThreeLevelTree`
* `AssembleGrouped_ParentNotInQueryResults_FetchesViaParentEntityLoader`
* `AssembleGrouped_ChildWithUnresolvableParent_FallsBackToOrphanBucket`
* `AssembleGrouped_RelationshipSource_UsesReturnedRelationshipEntities`
* `AssembleGrouped_ChildAndParentAlreadyInResults_DoesNotFetchAgain`

#### `SessionsViewGroupingTests` (in `Phantom.Workspaces.Data.Offline.Tests`, alongside `SessionsViewParentFilterTests`)
* `SessionsView_TopLevelSessionUnderDefinition_AppearsAsChildOfDefinition`
* `SessionsView_TopLevelSessionUnderManifestDirectly_AppearsAsChildOfManifest`
* `SessionsView_SubAgentSession_RemainsExcluded` *(regression pin on the existing filter)*
* `SessionsView_DefinitionUnderManifest_AppearsAsChildOfManifest` *(gated on Open Q2)*

#### `EntityBrowserWorkspaceTabViewModelTests` (add, in `Phantom.Workspaces.Tests`)
* `BrowserList_ViewWithGroupByParent_AssignsIncreasingLevels`
* `BrowserList_ViewWithGroupByParent_MarksParentAsStickyRow`
* `BrowserList_ViewWithoutGroupByParent_RetainsFlatBehavior` *(R5 regression pin)*

---

## Implementation plan

Each commit leaves the codebase building and all tests passing. Titles use the `[view-schema-grouping]` prefix.

### Commit 1 — [view-schema-grouping] Split `group-by-parent` schema into field/relationship variants

**Scope.** Modify `entity-type-view.json` so `group-by-parent` is a `oneOf` on `source` (`field` | `relationship`). Update the `EntityTypeView` / `EntityTypeViewGroupByParent` C# model type into a discriminated union (`GroupByParentSource` enum + `FieldSource` / `RelationshipSource` records). Default-parse legacy shape (no `source`) as `FieldSource` for compat.
**Files.**
* `Phantom.Workspaces.Data.Core\JsonSchemas\entity-type-view.json`
* `Phantom.Workspaces.Data.Core\…\EntityTypeView*.cs` (existing model file for `entity-type-view`)
* `Phantom.Workspaces.Data.Core.Tests\EntityTypeViewGroupByParentSchemaTests.cs` (new)
**Tests.** `EntityTypeViewGroupByParentSchemaTests` (all).
**Dependencies.** none.

### Commit 2 — [view-schema-grouping] Add `GroupByParentResolver`

**Scope.** New `GroupByParentResolver` class that resolves a child entity's parent ID from either source. No wiring into the assembler yet.
**Files.**
* `Phantom.Workspaces\ViewModels\GroupByParentResolver.cs` (new)
* `Phantom.Workspaces\ViewModels\IRelationshipLookup.cs` (new small interface for testability)
* `Phantom.Workspaces.Tests\GroupByParentResolverTests.cs` (new)
**Tests.** `GroupByParentResolverTests` (all).
**Dependencies.** Commit 1.

### Commit 3 — [view-schema-grouping] Extend `ViewHierarchyAssembler` with chained grouping

**Scope.** Add `AssembleGrouped`, `ChainGroupBy`, `SynthesizeMissingParent`, and `IParentEntityLoader` to `ViewHierarchyAssembler.cs`. Include an in-memory `IParentEntityLoader` used by tests. Not yet called by the view model.
**Files.**
* `Phantom.Workspaces\ViewHierarchyAssembler.cs`
* `Phantom.Workspaces\ViewModels\IParentEntityLoader.cs` (new)
* `Phantom.Workspaces.Tests\ViewHierarchyAssemblerGroupingTests.cs` (new)
**Tests.** `ViewHierarchyAssemblerGroupingTests` (all).
**Dependencies.** Commit 2.

### Commit 4 — [view-schema-grouping] Wire grouping into `EntityBrowserWorkspaceTabViewModel`

**Scope.** In `RebuildTreeAsync` / `BuildChildrenAsync`, when any type in the result set has `group-by-parent`, call `ViewHierarchyAssembler.AssembleGrouped`, then feed the resulting nodes into the existing `AddItemsDepthFirst`. Preserve the existing name-hierarchy path when no grouping applies (R5).
**Files.**
* `Phantom.Workspaces\ViewModels\EntityBrowserWorkspaceTabViewModel.cs`
* `Phantom.Workspaces.Tests\EntityBrowserWorkspaceTabViewModelTests.cs` (add three new tests)
**Tests.** New tests: `BrowserList_ViewWithGroupByParent_AssignsIncreasingLevels`, `BrowserList_ViewWithGroupByParent_MarksParentAsStickyRow`, `BrowserList_ViewWithoutGroupByParent_RetainsFlatBehavior`. Existing browser-list tests remain green.
**Dependencies.** Commit 3.

### Commit 5 — [view-schema-grouping] Apply `group-by-parent` to the Sessions view

**Scope.** Add `agent-session-entity-type-view.json` with `source: field, field-path: ["agent-definition-reference"], parent-entity-type-names: ["agent-definition","agent-manifest"]`. If Open Q2 resolves in favour of definition→manifest, add `agent-definition-entity-type-view.json` too and update `sessions-view.json` to include the required `relationships-to-return`. No C# changes.
**Files.**
* `Phantom.Workspaces.Data.Core\JsonEntities\entity-type-views\agent-session-entity-type-view.json` (new)
* `Phantom.Workspaces.Data.Core\JsonEntities\entity-type-views\agent-definition-entity-type-view.json` (new, gated on Q2)
* `Phantom.Workspaces.Data.Core\JsonEntities\views\sessions-view.json` (edit only if relationship-mode chosen)
* `Phantom.Workspaces.Data.Offline.Tests\SessionsViewGroupingTests.cs` (new)
**Tests.** `SessionsViewGroupingTests` (all). Existing `SessionsViewParentFilterTests` must remain green.
**Dependencies.** Commit 4.

### Commit 6 — [view-schema-grouping] Document the generic construct

**Scope.** Update `Phantom.Workspaces.Docs` with a short page describing `group-by-parent` (both sources), the chaining behaviour, and the git-workspaces + sessions worked examples. No test changes beyond the existing `Phantom.Workspaces.Docs.Tests` link-check pass.
**Files.**
* `Phantom.Workspaces.Docs\…\view-schema-grouping.md` (new)
**Tests.** Existing docs tests remain green.
**Dependencies.** Commit 5.

---

## Appendix — Key source citations

* `Phantom.Workspaces.Data.Core\JsonSchemas\view.json` — view schema; `sub-views` with `query` / `get-entity` / `relationships-to-return`.
* `Phantom.Workspaces.Data.Core\JsonSchemas\entity-type-view.json:57-78` — existing `group-by-parent` (field-mode).
* `Phantom.Workspaces.Data.Core\JsonSchemas\entity-type-view.json:112-127` — existing `traverse-relationships` with ancestor mode.
* `Phantom.Workspaces.Data.Core\JsonSchemas\workspace-entities-data-access-layer.json:42-107` — `enumerate-children` and `relationships-to-return`.
* `Phantom.Workspaces.Data.Core\JsonEntities\views\sessions-view.json` — three-section flat layout with `note` headers and the top-level-agent-sessions filter.
* `Phantom.Workspaces.Data.Core\JsonEntities\views\git-workspaces-view.json` + `entity-type-views\git-worktree-entity-type-view.json` — schema-driven grouping precedent.
* `Phantom.Workspaces.Data.Core\JsonSchemas\agent-session.json:29-46` — `agent-definition-reference` (target `agent-manifest`|`agent-definition`) and `parent-agent-session-ids`.
* `Phantom.Workspaces\ViewModels\EntityListItemViewModel.cs:1-56` — `Level`, `ParentItemKey`, `ChildItemKeys`, `StickyRow`, `IndentMargin`.
* `Phantom.Workspaces\ViewModels\EntityBrowserWorkspaceTabViewModel.cs:105-402` — `RebuildTreeAsync`, `BuildChildrenAsync`, `AddItemsDepthFirst`.
* `Phantom.Workspaces\ViewHierarchyAssembler.cs` — `AncestorSynthesizer` (parent synthesis precedent).
* `Phantom.Workspaces.Data.Offline.Tests\SessionsViewParentFilterTests.cs` — parent-filter regression pin.
* `Phantom.Workspaces.Tests\EntityListItemViewModelTests.cs`, `EntityBrowserWorkspaceTabViewModelTests.cs` — test-name convention.
