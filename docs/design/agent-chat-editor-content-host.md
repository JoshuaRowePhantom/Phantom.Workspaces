# Agent chat editor detail content host

## Context

The agent chat editor (`AgentChatEditorControl`) presents a navigation tree on the
left and a detail region on the right. Each navigation node carries a `DetailContent`
view-model (the conversation view, "Chat details", "Tools", "Background tasks",
"Sub-agents"). The detail region must behave as a **cache-N, show-one** deck: every
node's view is materialised once and kept alive so that view state (scroll position,
expansion, in-progress edits) survives navigating away and back, while only the
active node's view is visible.

This document records the decision to replace the hand-rolled detail deck with a
**locked, tab-strip-less, `ItemsSource`-bound Dock.Avalonia `DocumentDock`**, and the
alternatives that were evaluated and rejected. It is the design rationale behind
[#1035](https://github.com/JoshuaRowePhantom/Phantom.Workspaces/issues/1035) ("Sub-agent
'Chat details' panel renders blank").

Related prior art:
- [`agent-gui.md`](agent-gui.md) — agent GUI structure and the editor controls.
- [`sub-agent-dispatcher-chat-client.md`](sub-agent-dispatcher-chat-client.md) — how
  sub-agents are materialised as nested `AgentViewModel`s.
- `Phantom.Workspaces/ViewModels/WorkspaceDockFactory.cs` — the in-repo Dock factory
  template that the shell already uses for the main workspace document dock.

---

## Problem

The detail region is a hand-rolled "cache all views, show the active one" deck plus a
manual slot-registration table. Two coupled structures produce a blank panel for
sub-agent child nodes.

### 1. Hand-rolled `Panel` + `IsVisible` deck

`Phantom.Workspaces.Agent.Gui/Controls/AgentChatEditorControl.axaml` (Grid.Column 2,
**lines 284–298**) hosts an `ItemsControl` over `DetailContentSlots` with an
overlapping `Panel` ItemsPanel; each `DetailContentSlot` renders a `ContentControl`
whose visibility is toggled:

```xml
<Panel Grid.Column="2">
  <ItemsControl ItemsSource="{Binding DetailContentSlots}">
    <ItemsControl.ItemsPanel><ItemsPanelTemplate><Panel/></ItemsPanelTemplate></ItemsControl.ItemsPanel>
    <ItemsControl.ItemTemplate>
      <DataTemplate DataType="vm:DetailContentSlot">
        <ContentControl Content="{Binding Content}" IsVisible="{Binding IsVisible}"/>
      </DataTemplate>
    </ItemsControl.ItemTemplate>
  </ItemsControl>
</Panel>
```

All slot views are instantiated once and kept alive; only visibility flips. This is a
correct cache-N/show-one deck, but it is bespoke chrome maintained by hand.

### 2. Slot registration is built once, only for the parent's own details

The slot collection is built in the `AgentViewModel` constructor and contains **only
the parent's five detail objects** — `AgentViewModel.cs` **lines 89–95**:

```csharp
this.detailContentSlots.Add(new DetailContentSlot(this.conversationDetail) { IsVisible = true });
this.detailContentSlots.Add(new DetailContentSlot(this.chatDetailsDetail));
this.detailContentSlots.Add(new DetailContentSlot(this.toolsDetail));
this.detailContentSlots.Add(new DetailContentSlot(this.backgroundTasksDetail));
this.detailContentSlots.Add(new DetailContentSlot(this.subAgentsContainerDetail));
```

### 3. Visibility is a `ReferenceEquals` match — sub-agent details never match

`AgentViewModel.SelectedEditorItem` setter — `AgentViewModel.cs` **lines 291–296**:

```csharp
// Update detail content slot visibility.
var selected = value?.DetailContent;
foreach (var slot in this.detailContentSlots)
{
    slot.IsVisible = ReferenceEquals(slot.Content, selected);
}
```

If `selected` is not reference-equal to any slot `Content`, **every** slot is hidden
and the panel is blank.

Each sub-agent is materialised as a full nested `AgentViewModel`
(`AgentViewModel.cs` line 591, `AddSubAgentSlotEager`). The sub-agent nav item is built
by `SubAgentsCollectionTransformer.Create` — `AgentViewModel.cs` **lines 715–727** — and
its **children are taken directly from the sub-agent's own editor tree**:

```csharp
protected override AgentEditorNavigationItemViewModel Create(SubAgentSlotViewModel slot)
{
    var subRoot = slot.SubAgentViewModel.EditorItems.FirstOrDefault();
    return new AgentEditorNavigationItemViewModel(
        $"sub-agent-{slot.AgentId}",
        slot.SubAgentViewModel.DisplayName, null, slot.SubAgentViewModel.Description, null,
        this.subAgentsNavItem.DetailContent,   // parent's SubAgentsContainerViewModel (top node only)
        subRoot?.Children.ToArray() ?? [],      // <-- children come from the SUB-AGENT's own tree
        runningSubAgent: slot.RunningSubAgent);
}
```

`subRoot.Children` carry the sub-agent's own detail objects (`chat-details`,
`chat-tools`, `chat-background-tasks`, `chat-sub-agents`). Those detail VMs are **never
added to the parent's `detailContentSlots`**, so the `ReferenceEquals` loop matches
nothing and the panel is blank.

**Chain that produces the blank panel:**
1. User selects the sub-agent's **Chat details** child (`Id == "chat-details"`).
2. `OnEditorSelectionChanged` (`AgentChatEditorControl.axaml.cs` lines 72–80) sets the
   **parent** `AgentViewModel.SelectedEditorItem = selected`.
3. `selected.DetailContent` is the **sub-agent's** `AgentChatDetailsViewModel` instance.
4. The sub-agent-container special-case (setter lines 276–289) does not fire (not
   reference-equal to the parent's `subAgentsContainerDetail`).
5. The visibility loop (lines 291–296) finds no matching slot → all slots hidden →
   blank.

The sub-agent's `AgentChatDetailsViewModel` is correctly populated (`DisplayName`,
`AgentSessionId`, `ModelProvider`, `ModelId`, `ModelApiType`, `ModelConnectionType`)
but nothing renders it.

---

## Decision (authoritative)

**The detail region will be reimplemented as a locked, tab-strip-less,
`ItemsSource`-bound Dock.Avalonia `DocumentDock`.** Every navigation node's
`DetailContent` — including each sub-agent child — becomes a first-class cached
`Document`; the tree selection drives which document is active. This fixes the blank
sub-agent panel **by construction** (there is always a real host for every detail VM)
and replaces the fragile hand-rolled deck with Dock's first-class cache-N/show-one
primitive.

Verified against the installed **Dock.Avalonia `12.0.0.2`** (source tag `v12.0.0.2`,
commit `ebbf3d46`), which is already a repo dependency
(`Directory.Packages.props:16-20`). Use `WorkspaceDockFactory.cs:79-141` +
`WorkspaceDocumentGenerator.cs` as the in-repo template.

### 1. Hide the tab strip via a scoped resource (no retemplating)

The Fluent theme binds the strip's visibility to a `DynamicResource`:
`DocumentControl.axaml` →
`<Style Selector="^/template/ DocumentTabStrip#PART_TabStrip"><Setter Property="IsVisible" Value="{DynamicResource DockDocumentControlTabStripVisible}"/></Style>`,
default `True` in `Accents/Fluent.axaml`. Override it to `False` in a scoped
`ResourceDictionary` on the hosting `DockControl`. The separator host collapses
automatically (`PART_DocumentSeperatorHost` binds to `#PART_TabStrip.IsVisible`).

### 2. Lock the layout (freeze all docking interactions)

There is no `IsLocked`; use the master switch plus per-dockable flags:
- `DockControl.IsDockingEnabled = false` (flows to
  `_dockManagerOptions.IsDockingEnabled = false` — disables drag/drop/float/reorder in
  one line).
- On every `IDockable` (documents and dock):
  `CanClose = CanFloat = CanDrag = CanDrop = CanPin = CanDockAsDocument = false`.
- `DocumentDock.CanCreateDocument = false` (removes the "+" affordance).
- `DockControl.EnableManagedWindowLayer = false` (no floating-window overlay).
- Optional belt-and-braces: `DockProperties.IsDragEnabled = IsDropEnabled = false` on
  the subtree.

### 3. Bind `ItemsSource`, cache inactive views, drive active from the tree

`DocumentDock.ItemsSource` + `ItemContainerGenerator`/`DocumentTemplate` are first-class
properties; on any change Dock runs `RegenerateGeneratedDocuments → AddDocumentFromItem`,
i.e. **each source item becomes a cached `Document`**. With
`CacheDocumentTabContent="True"` (already global at `App.axaml:10`) the theme swaps in
`DockDocumentControlCachedContentTemplate`, which is literally an overlapping-`Grid` deck
of `DockableControl`s with `IsVisible = ReferenceEquals(item, Owner.ActiveDockable)` —
the same shape the editor hand-rolls today, but as first-class chrome. Selection is
programmatic via `SetActiveDockable`, bound to the tree selection.

**Factory sketch** (mirrors `WorkspaceDockFactory.cs:79-141`, interactions locked):
```csharp
var detailDock = new DocumentDock {
    Id = "AgentDetail",
    CanCreateDocument = false, CanFloat = false, CanDrag = false,
    CanDrop = false, CanPin = false, CanClose = false, CanDockAsDocument = false,
    IsCollapsable = false,
    VisibleDockables = CreateList<IDockable>(),
    ItemsSource = agentVm.AllDetailContents,   // flat list of every node's DetailContent (incl. sub-agents)
    ItemContainerGenerator = new AgentDetailDocumentGenerator(
        doc => this.documentsByDetail[doc.Context] = doc,
        key => this.documentsByDetail.Remove(key)),
};
```

**View sketch** (strip hidden, interactions off, caching global):
```xml
<dock:DockControl Layout="{Binding DetailLayout}"
                  IsDockingEnabled="False"
                  EnableManagedWindowLayer="False"
                  AutoCreateDataTemplates="False">
  <dock:DockControl.Resources>
    <x:Boolean x:Key="DockDocumentControlTabStripVisible">False</x:Boolean>
  </dock:DockControl.Resources>
  <!-- Scoped DataTemplates for DocumentDock/Document declared here to avoid Dock's
       auto-registered FuncDataTemplate<IDocumentDock> shadowing (regression #374 class). -->
</dock:DockControl>
```

**VM binding — tree selection activates the cached document:**
```csharp
void OnEditorSelectionChanged(node) =>
    factory.SetActiveDockable(factory.GetDocumentForDetail(node.DetailContent));
```
The **tree view keeps the navigation / tab-strip role**; selecting a node activates the
corresponding cached document.

### 3a. Content collection: ownership and availability

**Where the collection lives (ownership).** The editor-level `AgentViewModel` owns the
content collection — the direct successor to today's `detailContentSlots` /
`DetailContentSlots` (`AgentViewModel.cs:31, 86-90, 260`). It is a single owned
`ObservableCollection<AgentDetailDocumentItem>` exposed as
`ReadOnlyObservableCollection<AgentDetailDocumentItem> AllDetailContents`. The **root**
editor `AgentViewModel` (whose `AgentChatEditorControl.DataContext` is set) is the single
owner whose `AllDetailContents` feeds the one `DocumentDock`. The `AgentDetailDockFactory`
holds only the `Dictionary<AgentDetailDocumentItem, Document>` registry and the dock — it
does not own the collection (mirroring `WorkspaceDockFactory`, where `WorkspacePaneViewModel.Tabs`
owns the source and `documentsByTabId` is only a registry).

**Element type.** A new small wrapper VM `AgentDetailDocumentItem`, one per nav node /
detail slot, carrying: `Key` (stable, tree-unique id — drives `Document.Id` and node↔document
lookup), `Title` (display title), `Content` (the actual detail VM the cached template's
`ContentControl.Content` binds to), and `IsActive` (active-state mirror). This mirrors
`WorkspaceTabViewModel` → `WorkspaceDocument` (`WorkspaceDocumentGenerator.cs:25-37`): the
generator does `new Document()`, sets `doc.Id/Title/Context`, and registers
`documentsByItem[item] = doc`; `ClearDocumentContainer` unregisters and drops the cached doc.

**Projection, not the same object.** `EditorItems` is hierarchical and (for sub-agents)
completion-filtered (`SubAgentsCollectionTransformer.RefreshVisibleChildren`, `:800-830`);
`DocumentDock.ItemsSource` needs a flat, unfiltered list. These shapes are incompatible, so
the tree and dock bind to **different** collections that share **one source of truth — the
nav-node model**: the `TreeView.ItemsSource` binds `EditorItems`; the dock's `ItemsSource`
binds the projected flat `AllDetailContents`. Selection maps 1:1 by content identity.

**Flattening nested sub-agents at any depth.** Each `AgentViewModel` contributes its own
fixed detail VMs (conversation, chat-details, tools, sub-agents-container) to its
`allDetailContents` at construction. Sub-agents are nested `AgentViewModel`s, so the root's
flat list is a recursive aggregate: `root.AllDetailContents = root's own items ⊕ each
sub-agent's AllDetailContents` (observed). Because a sub-agent's collection already includes
*its* sub-agents, arbitrary depth is handled with no special-casing. Sync is piggybacked on
existing wiring:

- **Add:** in `AddSubAgentSlotEager` (`:600-612`), append the sub-agent's `AllDetailContents`
  and subscribe to its `CollectionChanged` for grand-child changes. Driven from
  `OnSubAgentsCollectionChanged` (`:562-571`) on the UI thread; the lazy path already marshals
  via `foregroundScheduler` (`:614-639`).
- **Remove:** `SubAgentsCollectionTransformer.OnRemoveAt/OnRemoved` (`:777-781`) unsubscribe
  and remove the departed sub-agent's items → the generator's `ClearDocumentContainer` drops
  the cached documents (no leak).
- **Complete:** hide-completed only re-projects the tree's *visible* children
  (`RefreshVisibleChildren`, `:800-830`); it must NOT touch `AllDetailContents`, so a completed
  sub-agent keeps cached documents and never blanks.

**Keying.** Sub-agent children reuse fixed ids (`chat-details`, …), so keys are qualified by
the owning agent (`sub-agent-{agentId}/chat-details`) to stay tree-unique. The node→document
lookup keys off the item whose `Content` is reference-equal to the node's `DetailContent`
(identity match — exactly today's `ReferenceEquals` test at `:299`, but selecting rather than
toggling visibility).

**Selection → active document.** The `SelectedEditorItem` setter (`:264-302`) drops the
`ReferenceEquals` visibility loop (`:295-300`) and instead resolves the item whose `Content`
matches the node's `DetailContent`, sets a bindable `SelectedDetailDocument`, and calls
`factory.SetActiveDockable(factory.GetDocument(item))`. Preferred wiring binds the dock's
active document to `SelectedDetailDocument`; the existing `OnEditorSelectionChanged`
code-behind (`:72-80`) already funnels into `vm.SelectedEditorItem`.

**Why the blank sub-agent panel is impossible.** Every node — including each sub-agent's
`chat-details`/`chat-tools`/`chat-sub-agents` — has a first-class entry in the single shared
`AllDetailContents`, contributed by the sub-agent's own `AgentViewModel` (the exact populated
VMs, e.g. its `AgentChatDetailsViewModel`, that are never registered in the parent's
`detailContentSlots` today). There is always a cached document to activate; no
`DetailContentSlots`, no `ReferenceEquals`-against-parent-slots miss.

### 4. Deliberately skip `Dock.Serializer`

This detail dock is ephemeral and fully derived from the editor tree, so it is never fed
to `Dock.Serializer` / `Dock.Serializer.SystemTextJson`. That avoids the `[JsonIgnore]`
shadowing the shell had to add for `Owner`/`StyleKey`/`ItemsSource`/`ItemContainerGenerator`
(`WorkspaceContentDock.cs:20-54`) and all the `DockState`/type-resolver friction.

---

## Alternatives considered

### (a) Minimal slot-registration fix — rejected

In `AddSubAgentSlotEager` (`AgentViewModel.cs:~591`), append each sub-agent's selectable
detail VMs (`chatDetailsDetail`, `toolsDetail`, `backgroundTasksDetail`,
`subAgentsContainerDetail`) as new `DetailContentSlot`s in the parent's
`detailContentSlots`, and remove them on sub-agent removal. The existing `ReferenceEquals`
loop (`:291-296`) would then match and show them; the existing `AgentChatDetailsViewModel`
DataTemplate renders them.

- **Pro:** ~10–20 lines, no XAML, no new dependency, **lowest risk**.
- **Con:** leaves the fragile hand-rolled `Panel`+`IsVisible` deck in place and keeps the
  manual slot bookkeeping (register/unregister on every sub-agent add/remove), which is
  exactly the class of code that produced this bug.

Rejected in favour of the reusable Dock host, which removes the bespoke registration
entirely.

### (b) `Dock.Controls.DeferredContentControl` standalone — rejected

`DeferredContentControl` is a `ContentControl` with a **single** `PART_ContentPresenter`;
its job is to *defer/stagger* materialisation of one `Content` onto a shared presentation
timeline. It holds exactly one realised subtree and has **no** notion of "select which of
N to show". Using N of them plus `IsVisible` toggling is functionally identical to the
current deck plus a new dependency — it is **not** a cache-N/select-one primitive.

### (c) Built-in Avalonia controls do NOT cache inactive views — rejected

Verified against Avalonia `12.1` source:
- **`Carousel`** — virtualises to the selected item (`VirtualizingCarouselPanel` keeps a
  single `_realized` control + recycle pool); inactive views are recycled, not retained.
- **`TabControl`** — a single `PART_SelectedContentHost` presenter; switching tabs
  re-assigns `Content`, detaching the previous tab's view (view state lost).
- **`TransitioningContentControl` / `ContentControl`** — single content, rebuilt on
  change.

So the only "N retained views, activate one" options are the current hand-rolled
`Panel`/`IsVisible` deck (caches but is fragile and has the registration gap) or Dock's
`DocumentDock` + `CacheDocumentTabContent` (the chosen first-class abstraction).

### Tab-strip-less Dock feasibility findings

The tab-strip-less locked-Dock approach was verified against Dock **v12.0.0.2**:
- The tab strip can be hidden purely via the `DockDocumentControlTabStripVisible`
  `DynamicResource` scoped on the `DockControl` — no retemplating required, and the
  separator host collapses with it.
- All interactive docking (drag, drop, float, reorder, pin, close, "+") can be frozen via
  `IsDockingEnabled=false` + per-dockable `Can*=false` + `CanCreateDocument=false` +
  `EnableManagedWindowLayer=false`.
- `ItemsSource` binding drives `RegenerateGeneratedDocuments → AddDocumentFromItem`, and
  `CacheDocumentTabContent="True"` selects the overlapping cached-content template — the
  exact cache-N/show-one behaviour needed.

A deeper source-level analysis (template names, exact XAML, per-flag citations) is
preserved in the "Dock.Avalonia deep-dive" comment on #1035.

---

## Consequences / Non-goals

- **Non-goals:** no floating windows, no tab reordering, no drag/drop, no docking layout
  persistence/serialization. The dock is deliberately locked and ephemeral.
- The detail-document collection must be **flattened per detail VM** (keyed by nav-node id
  / detail object identity), because the editor tree is nested.
- **Residual work:** `DataTemplate` scoping inside `DockControl`
  (`AutoCreateDataTemplates="False"` + templates declared on the `DockControl`) to dodge
  Dock's auto-registered `FuncDataTemplate<IDocumentDock>` — the shell hit and documented
  this as the regression #374 class (`Templates/DockDataTemplates.axaml:58-81`). This is
  the only non-trivial gotcha.
- No custom `HeaderTemplate`/`IconTemplate`/`CloseTemplate` is needed — they only render
  inside the (hidden) strip.
- New types mirror the shell: `AgentDetailDockFactory` (mirrors `WorkspaceDockFactory`)
  and `AgentDetailDocumentGenerator` (mirrors `WorkspaceDocumentGenerator`).

---

## Reference links

- Issue: [#1035 — Sub-agent 'Chat details' panel renders
  blank](https://github.com/JoshuaRowePhantom/Phantom.Workspaces/issues/1035)
- In-repo Dock template: `Phantom.Workspaces/ViewModels/WorkspaceDockFactory.cs:79-141`
- Serialization friction avoided: `Phantom.Workspaces/ViewModels/WorkspaceContentDock.cs:20-54`
