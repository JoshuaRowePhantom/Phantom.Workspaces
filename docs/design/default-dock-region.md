# Design: Default Dock Region

Status: **Phase 4 — Implementation Plan (selected: Option D — nullable `DefaultRegionConfig` object on the region)**

Feature: `default-dock-region`
Title prefix for filed bugs: `[default-dock-region]`

## Problem

A workspace can be split into multiple dock regions (multiple `DocumentDock`
areas within a `ProportionalDock` tree). Today, when a new tab is opened, it is
always placed into the **first `DocumentDock` found by a depth-first traversal**
of the selected workspace pane's content layout (`FindDocumentDock`,
`MainWindowViewModel.cs:2931`). The user has no way to say "open new tabs over
*here* instead". Once a user has split their workspace, new tabs land in the
wrong region and must be dragged over manually every time.

## Requirements

- **R1 — Designate a default region.** The user can mark one dock region within
  a workspace as the *default region* — the region into which newly opened tabs
  are placed.

- **R2 — New tabs honor the default.** When a new tab is opened without an
  explicit target (the common case — `OpenTabAsync` with `workspacePaneId` /
  region unspecified), it opens into the workspace's default region rather than
  the first-found `DocumentDock`.

- **R3 — Applies to all new-tab paths.** The behavior applies to every path that
  opens a tab into the workspace: opening an entity, opening a URL, opening a
  shell, starting an agent-chat session, and the `open_tab` tool — any path that
  currently resolves to "the selected pane's first DocumentDock".

- **R4 — Explicit targets still win.** If a caller explicitly specifies a target
  region/pane (e.g. "open here", drag-drop, or a caller passing an explicit
  region id), that explicit target takes precedence over the default region.

- **R5 — Persistence.** The default-region designation is persisted with the
  workspace layout (alongside the existing `dock-layout` serialization) and is
  restored when the workspace is reopened, so the choice survives app restarts.

- **R6 — Graceful fallback.** If the designated default region no longer exists
  when a tab is opened (the region was closed, or the layout changed so the id
  is gone), the system falls back to the current behavior (first-found
  `DocumentDock` of the selected pane) without error.

- **R7 — Discoverable affordance.** The user can set (and clear) the default
  region through a visible UI affordance on the region, and there is a visual
  indication of which region is currently the default.

- **R8 — Scope: per workspace.** The default region is a property of a single
  workspace's layout. Different workspaces have independent default regions.

## Resolved decisions (Phase 1)

1. **Region granularity.** A "dock region" is an individual `DocumentDock` — the
   kind of region the user creates by right-clicking a content tab and choosing
   **"New Horizontal/Vertical Document Dock"**. It is *not* the outer workspace
   pane.
2. **Set-default affordance.** A **toggle button on the left side of the region's
   tab strip**, with a glyph and a mouseover tooltip. Toggling it on makes the
   region the default; toggling it off clears the default.
3. **Visual indication.** Carried by the same left-side toggle button's glyph
   state (checked = default). No separate border/highlight required.
4. **Initial state.** **No implicit default.** Until the user toggles a region
   on, there is no default and new tabs use today's behavior (R6 fallback). The
   toggle can be turned off again to remove the default entirely.
5. **Scope.** **One default per whole workspace**, shared across all outer
   workspace panes — a single choice, not one per outer pane.

## Shared prerequisites (independent of which option is chosen)

The exploration surfaced two facts that both options must handle, so they are
**not** differentiators — they are baseline work either way:

- **P1 — Split regions are the wrong type today.** When the user splits via "New
  Horizontal/Vertical Document Dock", `FactoryBase.NewHorizontalDocumentDock`
  (`C:\dev\avalonia\Dock\src\Dock.Model\FactoryBase.Dockable.cs:1412-1450`) calls
  `CreateDocumentDock()`, which returns a **plain `Dock.Model.Mvvm.Controls.DocumentDock`**,
  not our `WorkspaceContentDock`. `WorkspaceDockFactory` does not override
  `CreateDocumentDock()` today. So split-created regions get the generic template
  (`DockDataTemplates.axaml:90-92`) and would carry neither our toggle nor any
  per-region state. **Both options require overriding `WorkspaceDockFactory.CreateDocumentDock()`
  to return a `WorkspaceContentDock` with a freshly-minted unique `Id`.**

- **P2 — The left-side toggle needs the region's tab strip retemplated.** The
  content region's tab strip is fully owned by Dock.Avalonia's `DocumentControl`
  (`DockDataTemplates.axaml:51-60`); the local `HeaderTemplate` only styles
  individual tab headers, not the strip chrome. To place a `ToggleButton` to the
  **left** of the tab items, we must supply a customized `DocumentControl`
  template (based on the Dock.Avalonia default at `C:\dev\avalonia`) that adds a
  left-aligned toggle bound to the region view model. **Both options share this
  UI work.**

The two options below differ only in **how the default region's identity is
represented and persisted.**

## Options

### Option A — Workspace-level `default-content-dock-id` (identity by region Id) — *considered, not chosen (background)*

**Architecture:** Give every `WorkspaceContentDock` a stable, **unique** `Id`
(minted in `CreateWorkspaceContentLayout` and in the new `CreateDocumentDock`
override from P1). Store a single scalar `default-content-dock-id` string on the
**workspace entity JSON**, written next to `dock-layout`/`tabs`/`active-tab-id`
in `WriteBackWorkspaceTabs` (`MainWindowViewModel.cs:~2708-2790`) and read back
in the restore path (`~:3167-3320`). The left-side toggle sets/clears this single
workspace field to this region's `Id`. At open time, `OpenTabAsync` resolves the
destination by searching **all** panes' content layouts for the `DocumentDock`
whose `Id == default-content-dock-id` (new `FindDocumentDockById`), falling back
to today's `FindDocumentDock` (`:2931`) if the id is absent or not found (R6).

**Pros:**
- Models "one default for the whole workspace" (decision 5) **directly** as a
  single scalar — exactly one value can exist, so the singleton invariant is
  free; no need to scan-and-clear other regions on set.
- The workspace-level field is trivially the source of truth for "activate the
  pane that owns the default region" when the default lives in a non-selected
  outer pane.
- `Id` is already serialized inside `dock-layout` and is stable across restore,
  so the reference resolves after reload.
- Toggle logic is one assignment: set field to my `Id`, or clear it.

**Cons:**
- Requires region `Id`s to be **globally unique**. Dock's split path currently
  **copies the parent's `Id`** onto the new dock
  (`FactoryBase.Dockable.cs:1425`), so the `CreateDocumentDock` override (P1)
  must also *overwrite* that copied `Id` with a fresh unique one — easy to get
  wrong if a future Dock update changes split behavior.
- Two persisted artifacts to keep coherent: the scalar id and the layout tree.
  A dangling id (region removed) is possible — mitigated by the R6 fallback.

### Option B — `IsDefaultRegion` bool flag on the region dockable (identity by flag) — *considered, not chosen (background)*

**Architecture:** Add `bool IsDefaultRegion { get; set; }` to
`WorkspaceContentDock`. It round-trips automatically through the dock-layout
serializer (`WorkspaceDockTypeInfoResolver` strips only `Type`/`ICommand`/`Owner`/
Avalonia props, so a plain `bool` is serialized), and the *same instance* is
rehydrated on restore (`TryRestoreFromDockLayoutAsync:3270-3321`), so no separate
workspace field is needed. The toggle sets `IsDefaultRegion = true` on this
region and, to preserve the singleton invariant, walks all panes' layouts to set
every other region's flag `false`. At open time, `OpenTabAsync` searches all
content layouts for the region with `IsDefaultRegion == true`, falling back to
`FindDocumentDock` (R6).

**Pros:**
- **No id-uniqueness requirement** — sidesteps the Dock split-copies-Id hazard
  entirely; identity is the flag, not the `Id`.
- Single persisted artifact: the flag rides inside the existing `dock-layout`
  serialization; nothing new to add to the workspace entity JSON, and the flag
  and the region can never diverge (they are the same object).
- Fallback is inherent: no flag anywhere → today's behavior (R6).

**Cons:**
- The "one per whole workspace" invariant is **not free** — it must be enforced
  imperatively by scanning all panes and clearing other regions' flags whenever
  one is set. A bug there could leave two defaults (resolved arbitrarily by
  first-match).
- The truth is **distributed** across dockables in possibly several layouts, so
  "which region is the default, workspace-wide?" requires a scan rather than a
  single field read.
- `dock-layout` is serialized per content layout; the flag is only meaningful
  once gathered across all of them.

### Option C — Identity by structural path — *considered, not chosen (background)*

Store the default as an index-path into the layout tree (e.g. `[0,1,0]`).
Rejected: paths are invalidated by any resplit/close/reorder, violating R6's
"survive layout changes" spirit and producing silent wrong-region targeting.
Recorded only as background.

### Option D — Nullable `DefaultRegionConfig` object on the region dockable — **SELECTED**

**Architecture.** Add a nullable reference-type property
`DefaultRegionConfig? DefaultRegionConfig { get; set; }` to
`WorkspaceContentDock`, where `DefaultRegionConfig` is a small, plain
serializable POCO living next to `WorkspaceContentDock` in
`Phantom.Workspaces.ViewModels`. The property's *presence* (non-null) is the
identity of the default region; its *contents* describe *how* the region
defaults. In the initial cut the type is intentionally an **empty** object
(no fields) whose semantics are "accept all new tabs" — but the type exists
specifically so future fields (content-type predicates, tab-kind filters,
priority, etc.) can be added **without another data-model migration**.

- `DefaultRegionConfig == null` → this region is NOT a default target.
- `DefaultRegionConfig != null` → this region IS a default target; today the
  empty config unconditionally accepts every new tab.

The toggle button on the left of the region's tab strip flips the property
between `null` and `new DefaultRegionConfig()`. Setting a new default on
region X walks all content layouts in the workspace and nulls the property
on every other `WorkspaceContentDock`, preserving the "exactly one default
per workspace" invariant (decision 5).

**Why an object over a bool or a workspace-level scalar id.** A bool
(Option B) encodes the current binary requirement but paints us into a
corner: any future "route markdown tabs here, shell tabs there" feature
would require introducing an entirely new schema element and a migration
for every persisted workspace. A workspace-level scalar id (Option A)
similarly hard-codes the "exactly one region, accepts everything" shape.
By making the property a nullable **object** now, the *shape* on disk is
already `{ "DefaultRegionConfig": { ...future fields... } }` from the very
first release — future extension is a purely additive JSON change that
older builds tolerate (unknown JSON properties are ignored by the
default STJ resolver used by `WorkspaceDockTypeInfoResolver`).

**How persistence works.** `WorkspaceContentDock` is already a persisted
dockable inside `dock-layout`. `WorkspaceDockTypeInfoResolver`
(`ViewModels/WorkspaceDockTypeInfoResolver.cs:25`) strips only
`[IgnoreDataMember]`, `ICommand`-typed, `Type`-typed, Avalonia-namespace,
and `Owner` back-reference properties (see `RemoveIgnoredMembers` at :80
and helpers at :104/:117). A CLR reference-type property whose declared
type lives in `Phantom.Workspaces.ViewModels` (a nullable POCO) is
therefore **retained** by the resolver and round-trips automatically as
nested JSON. Nothing has to be added to the workspace entity JSON itself;
the config rides inside `dock-layout` on the exact instance that is
rehydrated by `TryRestoreFromDockLayoutAsync`
(`MainWindowViewModel.cs:3270`). This mirrors the mechanism explored for
Option B but with an extensible payload.

**Trade-offs (accepted).** Like Option B, the "one per whole workspace"
invariant is imperative rather than free — the toggle handler must scan
all panes' content layouts and null the property on other regions. That
scan is O(#regions) and already needed for the R7 visual-state refresh,
so we colocate the two.

## Chosen design

**Option D — nullable `DefaultRegionConfig` object on `WorkspaceContentDock`.**
This directly encodes decision 5's binary state per region while preserving
a forward-compatible schema slot for later per-tab/per-content routing rules
without a second migration. It degrades to today's simple "one default,
accepts all tabs" behavior when the config is empty, and the entire feature
sits inside the existing `dock-layout` serialization pipeline — no new
workspace-entity JSON fields are required.

## Phase 3 — Detailed design

### 3.1 Model

New POCO in `Phantom.Workspaces/ViewModels/DefaultRegionConfig.cs`:

```csharp
namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Marker/settings object attached to a <see cref="WorkspaceContentDock"/> to
/// designate it as the workspace's default target region for newly opened
/// tabs. An instance's *presence* on the dock means "this region is the
/// default"; its *contents* (future fields) will describe filtering rules
/// (e.g. per-content-type or per-tab-kind predicates). Today the type is
/// intentionally empty and means "accept all new tabs".
/// </summary>
public sealed class DefaultRegionConfig
{
    // Intentionally empty in the initial cut. Future fields (e.g.
    // ContentTypePredicate, TabKindFilter, Priority) go here and are
    // additive on-disk without a schema migration.
}
```

Property added to `WorkspaceContentDock`
(`ViewModels/WorkspaceContentDock.cs:14`):

```csharp
/// <summary>
/// When non-null, marks this region as the workspace's default target for
/// newly opened tabs. Null means "not a default region". Round-trips via
/// WorkspaceDockTypeInfoResolver as nested JSON on the dock layout.
/// </summary>
public DefaultRegionConfig? DefaultRegionConfig { get; set; }
```

Because `DefaultRegionConfig` lives in `Phantom.Workspaces.ViewModels` (not
`Avalonia*`), is not `ICommand`/`Type`/`Owner`, and carries no
`[IgnoreDataMember]`, `WorkspaceDockTypeInfoResolver.RemoveIgnoredMembers`
(`WorkspaceDockTypeInfoResolver.cs:80-96`) leaves it in the type info;
`DockSerializer` emits it as a normal nested object and rehydrates the
same-instance dockable on restore
(`MainWindowViewModel.cs:3270` — `TryRestoreFromDockLayoutAsync`).

### 3.2 The "exactly one default workspace-wide" invariant

Enforced in a single helper on `MainWindowViewModel` (or, equivalently, an
extension on `WorkspacePaneViewModel`):

```csharp
internal void SetDefaultRegion(WorkspaceContentDock target)
{
    foreach (var pane in this.Workspace.Panes)
    {
        foreach (var dock in EnumerateContentDocks(pane.ContentLayout))
        {
            if (dock is WorkspaceContentDock wcd)
                wcd.DefaultRegionConfig = ReferenceEquals(wcd, target)
                    ? new DefaultRegionConfig()
                    : null;
        }
    }
    // Persist: same call site as any other dock-layout mutation.
    _ = this.WriteBackWorkspaceTabs(target's owning pane);
}

internal void ClearDefaultRegion(WorkspaceContentDock target)
{
    target.DefaultRegionConfig = null;
    _ = this.WriteBackWorkspaceTabs(target's owning pane);
}
```

`EnumerateContentDocks` is the DFS walker already implicit in
`FindDocumentDock` (`MainWindowViewModel.cs:2931`) generalized to yield
every match rather than the first. Both entry points are called by the
toggle-button command; nothing else mutates the property.

### 3.3 New-tab routing

`OpenTabAsync` (`MainWindowViewModel.cs:2320`) currently resolves the
target region at `:2336` with

```csharp
var documentDock = this.FindDocumentDock(targetPane.ContentLayout);
```

Introduce a new resolver `FindDefaultDocumentDock(IDockable layout)` that
does the same DFS but yields the first `WorkspaceContentDock` whose
`DefaultRegionConfig != null`. `OpenTabAsync` becomes:

```csharp
var documentDock =
    this.FindDefaultDocumentDock(targetPane.ContentLayout)
    ?? this.FindDocumentDock(targetPane.ContentLayout); // R6 fallback
```

R4 (explicit target wins) is preserved because callers that already pass a
`workspacePaneId` short-circuit ahead of this line; only the "no explicit
region" path consults the default.

R3 coverage: every currently-known "new tab" call site funnels through
`OpenTabAsync` (or its `FindDocumentDock` neighbours at `:302`, `:313`,
`:990`, `:1951`, `:1995`, `:2159`, `:2187`, `:2208`, `:2224`, `:2470`,
`:2523`, `:2621`, `:2656`, `:3040`). These are audited in commit (d) below
and updated to prefer `FindDefaultDocumentDock` where they open a new
document into the workspace (excluding the "which dock am I already in?"
lookups such as `:2770` inside `WriteBackWorkspaceTabs`).

Since the initial `DefaultRegionConfig` is empty and unconditionally
accepts all tabs, no per-tab predicate is evaluated in this cut. The
resolver signature already takes the tab under consideration so future
schema fields can filter without another API change:

```csharp
private WorkspaceContentDock? FindDefaultDocumentDock(
    IDockable layout, WorkspaceTabViewModel tab);
```

### 3.4 Shared prerequisites (recap, unchanged from Phase 2)

- **P1** — `WorkspaceDockFactory` must override `CreateDocumentDock()` to
  return a `WorkspaceContentDock` with a fresh unique `Id`, so that splits
  produced by `FactoryBase.NewHorizontalDocumentDock` /
  `NewVerticalDocumentDock`
  (`C:\dev\avalonia\Dock\src\Dock.Model\FactoryBase.Dockable.cs:1412-1450`,
  parent `Id` copied at `:1425`) become the type that can carry
  `DefaultRegionConfig`. This is a hard prerequisite for the feature;
  without it, split-created regions are plain `DocumentDock` and cannot
  hold the config.
- **P2** — Left-side toggle button requires retemplating Dock.Avalonia's
  `DocumentControl` (Dock.Avalonia default under `C:\dev\avalonia`) with a
  left-aligned `ToggleButton` bound to a per-region view-model command,
  fronted by the local `DockDataTemplates.axaml` entry for
  `WorkspaceContentDock`.

### 3.5 Toggle button and visual state

- **Placement.** Left side of the region tab strip, inside the retemplated
  `DocumentControl` from P2.
- **Command.** A `ToggleDefaultRegionCommand` on `WorkspaceContentDock` (or
  hosted by `MainWindowViewModel` and bound via the dock's owner) that
  calls `SetDefaultRegion(this)` when checked and
  `ClearDefaultRegion(this)` when unchecked.
- **Glyph.** A pin-like glyph (checked/filled = default, unchecked/outline
  = not default), sourced from the same icon system used elsewhere in the
  workspace chrome.
- **Tooltip.**
  - Unchecked: "Make this the default region for new tabs."
  - Checked: "Default region for new tabs. Click to clear."
- **Visual state.** The `IsChecked` binding is
  `{Binding DefaultRegionConfig, Converter={StaticResource NotNullToBool}}`.
  No separate border or highlight; the button glyph is the sole indicator
  (per decision 3).
- **Uncheckable.** The toggle is a plain two-state `ToggleButton`, so the
  user can click a checked default off to remove the workspace default
  entirely (decision 4 + R7).

### 3.6 Persistence and restore

- **Write path.** `WriteBackWorkspaceTabs`
  (`MainWindowViewModel.cs:2708`) already serializes `dock-layout` via
  `DockSerializer` with `WorkspaceDockTypeInfoResolver`
  (`:2753`). No change needed — `DefaultRegionConfig` is now part of that
  JSON automatically.
- **Read path.** `TryRestoreFromDockLayoutAsync`
  (`MainWindowViewModel.cs:3270`, resolver at `:3280`) rehydrates the same
  `WorkspaceContentDock` instances with the property populated. New-tab
  routing (§3.3) picks up the restored default with no extra restore
  code.
- **R6 (fallback).** If a restore loses the default (e.g. an older JSON
  without the property, or a region that used to hold it was closed),
  every dock's `DefaultRegionConfig` is null and
  `FindDefaultDocumentDock` returns null, so `OpenTabAsync` naturally
  falls back to `FindDocumentDock`.

## Phase 4 — Implementation plan

Ordered list of small, independently-implementable commits. Each commit
compiles, ships passing tests, and does not depend on later commits at
runtime. Test names follow the `Subject_Scenario_ExpectedOutcome`
convention.

### Commit (a) — Add `DefaultRegionConfig` type + nullable property + serialization round-trip

**Files touched:**
- `Phantom.Workspaces/ViewModels/DefaultRegionConfig.cs` (new)
- `Phantom.Workspaces/ViewModels/WorkspaceContentDock.cs` (add property)
- `Phantom.Workspaces.Tests/WorkspaceDocumentSerializationTests.cs` (add tests)

**Expected tests:**
- `WorkspaceContentDock_DefaultRegionConfigNull_RoundTripsAsNull`
- `WorkspaceContentDock_DefaultRegionConfigEmpty_RoundTripsAsNonNullInstance`
- `WorkspaceDockTypeInfoResolver_WorkspaceContentDock_RetainsDefaultRegionConfigProperty`
- `WorkspaceContentDock_LegacyJsonWithoutDefaultRegionConfig_DeserializesWithNullProperty`

### Commit (b) — Override `WorkspaceDockFactory.CreateDocumentDock()` so splits produce `WorkspaceContentDock` (P1)

**Files touched:**
- `Phantom.Workspaces/ViewModels/WorkspaceDockFactory.cs` (override)
- `Phantom.Workspaces.Tests/MainWindowDockTemplateTests.cs` (extend existing split tests around `:325`, `:351`, `:1040`)

**Expected tests:**
- `WorkspaceDockFactory_CreateDocumentDock_ReturnsWorkspaceContentDock`
- `WorkspaceDockFactory_CreateDocumentDock_AssignsFreshUniqueId`
- `WorkspaceDockFactory_NewHorizontalDocumentDockSplit_ProducesWorkspaceContentDockWithFreshId`
- `WorkspaceDockFactory_NewVerticalDocumentDockSplit_ProducesWorkspaceContentDockWithFreshId`

### Commit (c) — One-default-per-workspace invariant: `SetDefaultRegion` / `ClearDefaultRegion` + `EnumerateContentDocks`

**Files touched:**
- `Phantom.Workspaces/ViewModels/MainWindowViewModel.cs` (helpers)
- `Phantom.Workspaces.Tests/MainWindowViewModelTests.cs` (unit tests)

**Expected tests:**
- `MainWindowViewModel_SetDefaultRegion_TargetGetsNonNullConfig`
- `MainWindowViewModel_SetDefaultRegion_SiblingRegionsGetNullConfig`
- `MainWindowViewModel_SetDefaultRegion_RegionsInOtherPanesGetNullConfig`
- `MainWindowViewModel_ClearDefaultRegion_TargetGetsNullConfig`
- `MainWindowViewModel_SetDefaultRegion_PersistsViaWriteBackWorkspaceTabs`

### Commit (d) — New-tab routing consults the default (`FindDefaultDocumentDock` + `OpenTabAsync` audit)

**Files touched:**
- `Phantom.Workspaces/ViewModels/MainWindowViewModel.cs` (`FindDefaultDocumentDock`, `OpenTabAsync:2320`, and the R3 audit of the other `FindDocumentDock` new-tab call sites)
- `Phantom.Workspaces.Tests/MainWindowIntegrationTests.cs` (routing tests)

**Expected tests:**
- `OpenTabAsync_NoDefaultRegionConfigured_UsesFirstFoundDocumentDock`
- `OpenTabAsync_DefaultRegionInSelectedPane_OpensTabIntoDefaultRegion`
- `OpenTabAsync_DefaultRegionInNonFirstSplit_OpensTabIntoDefaultRegionNotFirstDfsMatch`
- `OpenTabAsync_ExplicitWorkspacePaneIdProvided_IgnoresDefaultRegion`
- `OpenTabAsync_DefaultRegionRemovedFromLayout_FallsBackToFirstFoundDocumentDock`
- `OpenTabViaEntity_DefaultRegionSet_UsesDefaultRegion`
- `OpenTabViaUrl_DefaultRegionSet_UsesDefaultRegion`
- `OpenTabViaShell_DefaultRegionSet_UsesDefaultRegion`
- `OpenTabViaAgentChat_DefaultRegionSet_UsesDefaultRegion`

### Commit (e) — Left-side toggle button on the region tab strip (retemplate `DocumentControl`, P2)

**Files touched:**
- `Phantom.Workspaces/Templates/DockDataTemplates.axaml` (custom `DocumentControl` template for `WorkspaceContentDock`)
- `Phantom.Workspaces/ViewModels/WorkspaceContentDock.cs` (`ToggleDefaultRegionCommand` and `IsDefault` computed property, or an equivalent binding-friendly notifier)
- `Phantom.Workspaces/Converters/NotNullToBoolConverter.cs` (if not already present)
- `Phantom.Workspaces.Tests/MainWindowDockTemplateTests.cs` (template tests)

**Expected tests:**
- `WorkspaceContentDockTemplate_RendersLeftSideDefaultToggle`
- `WorkspaceContentDockTemplate_DefaultRegionConfigNull_ToggleIsUnchecked`
- `WorkspaceContentDockTemplate_DefaultRegionConfigNonNull_ToggleIsChecked`
- `WorkspaceContentDockTemplate_ToggleTooltipReflectsCheckedState`
- `WorkspaceContentDock_ToggleDefaultRegionCommand_CheckingSetsNonNullConfig`
- `WorkspaceContentDock_ToggleDefaultRegionCommand_UncheckingSetsNullConfig`
- `WorkspaceContentDock_ToggleDefaultRegionCommand_CheckingClearsOtherRegionsConfigs`

### Commit (f) — End-to-end persistence and restore

**Files touched:**
- `Phantom.Workspaces.Tests/MainWindowIntegrationTests.cs` (persistence e2e)
- `Phantom.Workspaces.Tests/WorkspaceDocumentSerializationTests.cs` (already covered structurally in (a); e2e covers the write→restore pane path)

**Expected tests:**
- `Workspace_DefaultRegionConfigured_PersistsAcrossWriteBackAndRestore`
- `Workspace_DefaultRegionInSecondSplit_AfterRestore_NewTabsGoToRestoredDefault`
- `Workspace_DefaultRegionCleared_PersistsAsAllNullAfterRestore`
- `Workspace_LegacyDockLayoutWithoutProperty_RestoresWithNoDefaultAndUsesFallback`

## Architectural grounding (from codebase exploration)

- Dock layout: custom `WorkspaceContentDock : DocumentDock`
  (`ViewModels/WorkspaceContentDock.cs:14`) inside a `ProportionalDock` tree,
  built by `WorkspaceDockFactory` (`ViewModels/WorkspaceDockFactory.cs:13`,
  `CreateWorkspaceContentLayout():116`).
- New-tab entry point: `MainWindowViewModel.OpenTabAsync()`
  (`MainWindowViewModel.cs:2320`, interface `IWorkspaceTabService:23`). Target
  region chosen by `FindDocumentDock(targetPane.ContentLayout)`
  (`:2335`, `:2931`) — first `IDocumentDock` in a DFS walk.
- Region tracking: no explicit region id today; only `SelectedWorkspacePane`
  (outer tab) is tracked. Splits are anonymous.
- Persistence: workspace entity JSON stores `dock-layout` (serialized
  `IRootDock` tree) via `WriteBackWorkspaceTabs()` (`:2708`) /
  `TryRestoreFromDockLayoutAsync()` (`:3270`). A default-region id would live
  alongside `dock-layout` and/or as a `DocumentDock.Id` on the layout.
- Tests: `MainWindowViewModelTests`, `MainWindowDockTemplateTests`,
  `MainWindowIntegrationTests`, `WorkspaceDocumentSerializationTests`,
  `WorkspacePaneViewModelTests` (`Phantom.Workspaces.Tests`). Naming style
  `ClassName_Scenario_Expectation`.
