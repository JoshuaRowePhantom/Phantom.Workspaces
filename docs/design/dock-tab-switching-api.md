# Dock Tab-Switching Numeric API

A reusable, easily-attachable API that adds **numeric keyboard tab-switching** (e.g. `Alt+1..Alt+0`)
plus **on-tab number badges** to an *arbitrary* [Avalonia.Dock](https://github.com/wieslawsoltes/Dock)
docking area, decoupled from any particular view-model.

Status: Design proposal
Scope: `Dock.Avalonia` (v12.0.0.2) + Avalonia (12.0.4) consumers.

---

## 1. Context & Motivation

Phantom.Workspaces already ships an Alt+N tab-switching feature with a "nicely animated" numeric
badge drawn on each tab header. That implementation is **entirely bespoke and welded to
`MainWindowViewModel`**:

- Key handling lives in `MainWindow.axaml.cs` (`OnPreviewKeyDown` / `OnPreviewKeyUp`,
  `features\Phantom.Workspaces\MainWindow.axaml.cs:49-155,224-243`), which tracks `IsAltHeld` /
  `IsShiftHeld` and calls `GoToTabAtIndexCommand` / `GoToWorkspacePaneAtIndexCommand`.
- Ordering, label assignment, and badge visibility live in `MainWindowViewModel`
  (`ComputeGlobalTabOrder` `:2754`, `AltShortcutLabelForIndex` `:2649`,
  `RefreshTabAltShortcutLabels`/`RefreshActiveWorkspaceAltShortcutLabels` `:2663/:2691`,
  `PropagateBadgeVisibility` `:2791`).
- The badge visual is a bespoke `DataTemplate` for `TabHeaderViewModel`
  (`features\Phantom.Workspaces\Templates\DockDataTemplates.axaml:86-110`) styled by
  `Border.alt-index-badge` in `features\Phantom.Workspaces.Gui.Shared\Styles\SharedStyles.axaml:1235-1250`.

Three problems motivate generalizing this:

1. **Wrong source of truth (#1067).** Indexing/badges are computed from the internal flat
   `WorkspacePaneViewModel.Tabs` projection instead of the Dock's `IDock.VisibleDockables`, so they
   diverge under splits and the #1065 insert-to-the-right reorder. The reusable API must read the
   **Dock structure** directly.
2. **Not reusable.** A plain consumer with their own `DockControl` cannot get this behavior without
   copying VM code, key handlers, templates and styles.
3. **Not composable.** The badge is baked into one header `DataTemplate`; it cannot coexist cleanly
   with other header-augmenting behaviors.

This document designs a self-contained, attachable API — call it **`DockTabSwitch`** — that a
consumer wires onto their `DockControl` with a single attached property and sensible defaults, while
remaining fully configurable (keys, scope, badge template).

---

## 2. Goals / Non-goals

### Goals
- Attach numeric tab-switching + number badges to *any* `DockControl` with one line of XAML.
- **Configurable key set**: modifier mask (`Alt`/`Ctrl`/`Shift`) × (`1`-`0` and/or `F1`-`F12`).
- **Two scopes**: (a) *all switchable strips*, with "switchable" defaulting **on** at the Dock level
  (opt-out); (b) *the currently focused dock only*.
- **Replaceable index badge template**, defaulting to the product's existing animated badge.
- **Composable**: badge injection must not clobber the existing header content or other behaviors.
- Single ordering **source of truth** shared by display and activation, derived from
  `IDock.VisibleDockables` in visual order.
- Decoupled from `MainWindowViewModel`; works for a plain consumer's `DockControl`.

### Non-goals
- Redesigning Dock's drag/dock/split mechanics.
- Replacing Dock's built-in Ctrl+Tab *document selector* (`DockControl.KeyDownHandler`,
  `DockControl.axaml.cs:728`); the new API composes **alongside** it.
- Cross-`TopLevel`/floating-window global hotkeys (addressed as an open question).
- Persisting a user-remappable keymap (the API accepts a gesture config; persistence is the
  consumer's concern).

---

## 3. Grounding facts (Dock / Avalonia source)

These facts anchor every design decision below.

### Dock model objects are **plain CLR objects, not `AvaloniaObject`**
`Document`/`DockableBase` derive from `ReactiveBase` (INPC), **not** `AvaloniaObject`:
- `avalonia\Dock\src\Dock.Model.Inpc\Core\DockableBase.cs:13` —
  `public abstract class DockableBase : ReactiveBase, IDockable, ...`
- `avalonia\Dock\src\Dock.Model.Inpc\Controls\Document.cs:14` —
  `public class Document : DockableBase, ...`

**Consequence:** You *cannot* set an `AvaloniaProperty` attached property (or add an `AdornerLayer`
adorner) on a dockable. Anything visual must target the realized **header container**, which *is* an
`AvaloniaObject`. This confirms the maintainer note in **#1067**.

### The realized header container *is* an `AvaloniaObject`
- `avalonia\Dock\src\Dock.Avalonia\Controls\DocumentTabStripItem.axaml.cs:24` —
  `public class DocumentTabStripItem : TabStripItem` (→ `ContentControl` → `Control` → `AvaloniaObject`).
- `avalonia\Dock\src\Dock.Avalonia\Controls\DocumentTabStrip.axaml.cs:27` —
  `public class DocumentTabStrip : TabStrip, IExternalDockSurface` (→ `ItemsControl`).

`DocumentTabStrip.ItemsSource` is bound to `IDock.VisibleDockables` and `SelectedItem` to
`ActiveDockable`:
- `avalonia\Dock\src\Dock.Avalonia.Themes.Fluent\Controls\DocumentControl.axaml:22-23` —
  `<DocumentTabStrip ItemsSource="{Binding VisibleDockables}" SelectedItem="{Binding ActiveDockable, Mode=TwoWay}" />`.
- Each realized item container is a `DocumentTabStripItem`; its `DataContext` is the `IDockable`.

### Tab header template = a `StackPanel` of `ContentPresenter`s (composition point)
`DocumentTabStripItem`'s Fluent `ControlTheme`
(`avalonia\Dock\src\Dock.Avalonia.Themes.Fluent\Controls\DocumentTabStripItem.axaml:287` →
template) contains a header host with named presenters:
- `PART_HeaderHost` `StackPanel` `:355`
- `PART_IconPresenter` `:362`, `PART_HeaderPresenter` `:370` (the actual header content),
  `PART_ModifiedPresenter` `:373`, `PART_ClosePresenter` `:378`.

An **extra** presenter for the index badge can be composed into this host (or overlaid) *without*
touching `PART_HeaderPresenter`.

### `DocumentTabStrip` is an `ItemsControl` with container lifecycle events
- `avalonia\Avalonia\src\Avalonia.Controls\ItemsControl.cs:227` — `event ... ContainerPrepared;`
  (`:218` `PreparingContainer`, `:246` `ContainerClearing`), raised from `ItemContainerPrepared`
  (`:713-717`). This is the hook to (re)apply badge adornment as containers are realized/recycled,
  which matters under virtualization.

### `DockControl` already installs a **tunnel** `KeyDown` handler
- `avalonia\Dock\src\Dock.Avalonia\Controls\DockControl.axaml.cs:252` —
  `AddHandler(KeyDownEvent, KeyDownHandler, RoutingStrategies.Tunnel);` and `KeyDownHandler` `:728`
  drives Dock's own document *selector* (Ctrl+Tab). This proves the pattern (root-level tunnel key
  handling on the `DockControl`) and gives us a precedent to sit beside.
- `KeyDownEvent` is registered `Tunnel | Bubble`
  (`avalonia\Avalonia\src\Avalonia.Base\Input\InputElement.cs:106-109`).

### Input / focus / attached-property primitives
- `KeyGesture(Key, KeyModifiers)` + `Matches(KeyEventArgs)` —
  `avalonia\Avalonia\src\Avalonia.Base\Input\KeyGesture.cs:13,20,158`.
- `KeyModifiers { None, Alt=1, Control=2, Shift=4, Meta=8 }` —
  `avalonia\Avalonia\src\Avalonia.Base\Input\IKeyboardDevice.cs:7-15`.
- `Key` enum: `D0=34..D9`, `F1=90..F12=101` —
  `avalonia\Avalonia\src\Avalonia.Base\Input\Key.cs:90-101,220-225`.
- `KeyBinding` (`Gesture` + `Command` + `TryHandle`) —
  `avalonia\Avalonia\src\Avalonia.Base\Input\KeyBinding.cs`.
- `HotKeyManager` — canonical attached-property-drives-behavior pattern; registers a `KeyBinding` on
  `TopLevel.KeyBindings` when the attached `HotKey` changes
  (`avalonia\Avalonia\src\Avalonia.Controls\HotkeyManager.cs:11,127-134,146`).
- **Attached property that inherits a default down the tree**:
  `AvaloniaProperty.RegisterAttached<TOwner,THost,TValue>(name, defaultValue, inherits:true, ...)`
  (`avalonia\Avalonia\src\Avalonia.Base\AvaloniaProperty.cs:355-376`); canonical use:
  `ToolTip.ShowOnDisabledProperty` / `ServiceEnabledProperty` with `inherits:true`
  (`avalonia\Avalonia\src\Avalonia.Controls\ToolTip.cs:75-82`). This is exactly how a Dock-level
  default flows to descendant strips but can be overridden per-strip.
- `FocusManager.GetFocusedElement()` and scope-aware `GetFocusedElement(IFocusScope)` —
  `avalonia\Avalonia\src\Avalonia.Base\Input\FocusManager.cs:65,133-136`.
- `AdornerLayer.GetAdornerLayer(Visual)` + `SetAdornedElement`/`SetAdorner` attached props —
  `avalonia\Avalonia\src\Avalonia.Controls\Primitives\AdornerLayer.cs:15,20-33,63-101`.
- `IFactory.SetActiveDockable(IDockable)` / `SetFocusedDockable(IDock, IDockable?)` —
  `avalonia\Dock\src\Dock.Model\Core\IFactory.cs:303,310`; `IDock.VisibleDockables` / `ActiveDockable`
  / `FocusedDockable` — `avalonia\Dock\src\Dock.Model\Core\IDock.cs:16,21,31`.
- **`Avalonia.Xaml.Interactivity` is NOT in this Avalonia source tree** (it ships as a separate NuGet
  package). To avoid a hard dependency, the design uses **native attached properties + a manager
  object** (the `HotKeyManager` pattern) rather than `Behavior<T>`. An optional
  `Behavior<DocumentTabStripItem>` adapter can be offered where the package is already referenced.

---

## 4. API Design

The API is a static attached-property host class `DockTabSwitch` in a new
`Dock.Avalonia.TabSwitching` assembly/namespace, plus a small config object and a default badge
`ControlTheme`. Nothing here references any product view-model.

```
DockTabSwitch (static attached-property host, AvaloniaObject)
 ├─ Enabled            (attached, on DockControl)        → installs DockTabSwitchController
 ├─ Gestures           (attached, DockTabSwitchGestures) → key set config
 ├─ Scope              (attached, DockTabSwitchScope)     → AllSwitchable | FocusedDockOnly
 ├─ IsSwitchable       (attached, bool, inherits:true, default true)  → per-strip opt-out
 └─ IndexTheme         (attached, ControlTheme, inherits:true)        → replaceable badge

DockTabSwitchController   – per-DockControl manager (created by Enabled changed-handler)
DockTabSwitchGestures     – modifier mask + key ranges → ordered KeyGesture list, index map
DockIndexBadgeBehavior    – per-DocumentTabStripItem adornment (composable)
```

### 4.1 Requirement 1 — Configurable key set

**Config object.** A consumer declares the gesture set declaratively:

```csharp
public sealed class DockTabSwitchGestures
{
    // Which modifier(s) must be held. Default: Alt.
    public KeyModifiers Modifiers { get; set; } = KeyModifiers.Alt;

    // Which key ranges participate, in the order they map to 1..N.
    // Default: Digits (D1..D9, then D0 == index 10).
    public DockTabSwitchKeys Keys { get; set; } = DockTabSwitchKeys.Digits;

    // Optional explicit override: an ordered list wins over Modifiers/Keys.
    public IList<KeyGesture>? Gestures { get; set; }
}

[Flags] public enum DockTabSwitchKeys { Digits = 1, FunctionKeys = 2 }
```

**Gesture → index mapping.** `DockTabSwitchGestures.BuildMap()` produces an ordered
`IReadOnlyList<KeyGesture>` and an index lookup, grounded in the `Key` enum
(`Key.cs:90-101,220-225`):

- `Digits`: `D1→0, D2→1, … D9→8, D0→9` (so "0" is the 10th tab — matching the existing
  `GetDigitIndex`/`AltShortcutLabelForIndex` behavior, `MainWindow.axaml.cs:139-155`,
  `MainWindowViewModel.cs:2649`).
- `FunctionKeys`: `F1→0 … F12→11`.
- If both flags are set, digits occupy `1..10`, function keys continue `11..N` (deterministic order).
- `Gestures` (explicit list) overrides everything; index = position in the list.

Each gesture is a `KeyGesture(key, Modifiers)`; matching uses `KeyGesture.Matches(KeyEventArgs)`
(`KeyGesture.cs:158`) so `KeyModifiers` equality is exact (Alt-only doesn't fire on Alt+Shift).

**Where the handler attaches / tunnel vs bubble.** The controller adds a **tunnel** `KeyDown`
handler on the `DockControl` root, mirroring Dock's own selector:

```csharp
dockControl.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
```

Rationale (source-grounded):
- `KeyDownEvent` is `Tunnel | Bubble` (`InputElement.cs:106-109`); tunnel fires root→leaf, so the
  `DockControl` sees the gesture *before* a focused editor/child can swallow it. This is exactly what
  Dock does for its selector (`DockControl.axaml.cs:252`).
- The controller sets `e.Handled = true` only when a gesture matches an in-range index, so
  non-matching keys still reach children (typing "1" in a text box is unaffected because it carries no
  Alt modifier).
- To toggle badge **visibility** while a bare modifier is held (e.g. Alt down → show badges), the
  controller also watches `KeyDown`/`KeyUp` for the modifier keys (`Key.LeftAlt`/`RightAlt`, etc.),
  the same technique as `MainWindow.axaml.cs:62-67,224-243`, and flips a controller-owned
  `AreBadgesVisible` flag consumed by the badge template.

Optionally, for a *pure MVVM* consumer, the same map can be materialized as `KeyBinding`s registered
on the enclosing `TopLevel.KeyBindings` via the `HotKeyManager` pattern
(`HotkeyManager.cs:127-134`); the tunnel handler is preferred because it (a) needs no focused command
target and (b) participates in scope resolution (§4.2).

### 4.2 Requirement 2 — Scope

Two attached properties, both `inherits:true` so a value on the `DockControl` (or any ancestor)
cascades but can be overridden lower down — using the exact `ToolTip` precedent (`ToolTip.cs:75-82`).

```csharp
// Default TRUE at the Dock scope → opt-out, not opt-in.
public static readonly AttachedProperty<bool> IsSwitchableProperty =
    AvaloniaProperty.RegisterAttached<DockTabSwitch, Control, bool>(
        "IsSwitchable", defaultValue: true, inherits: true);

public static readonly AttachedProperty<DockTabSwitchScope> ScopeProperty =
    AvaloniaProperty.RegisterAttached<DockTabSwitch, Control, DockTabSwitchScope>(
        "Scope", defaultValue: DockTabSwitchScope.AllSwitchable, inherits: true);

public enum DockTabSwitchScope { AllSwitchable, FocusedDockOnly }
```

**(a) AllSwitchable (default).** Every `DocumentTabStrip` under the `DockControl` is switchable
unless `DockTabSwitch.IsSwitchable="False"` is set on that strip (or an ancestor). Because the
property inherits and defaults `true`, the default behavior is opt-out. Numbering is computed by
walking the Dock model tree from the `DockControl`'s `Layout` (`IDock`), collecting each switchable
strip's `IDock.VisibleDockables` in visual order.

**(b) FocusedDockOnly.** Only the strip belonging to the currently focused dock is numbered. The
controller resolves the active `DocumentControl`/`IDock` two ways and prefers whichever is available:
- Dock model: `dockControl.Layout.FocusedDockable`'s owning `IDock` (`IDock.FocusedDockable`
  `IDock.cs:31`), i.e. the dock whose `ActiveDockable` chain is focused.
- Visual focus fallback: `FocusManager.GetFocusedElement()` (`FocusManager.cs:65`), walk visual
  ancestors to the enclosing `DocumentControl`, read its `DataContext as IDock`. Scope-restricted
  focus is available via `GetFocusedElement(IFocusScope)` (`FocusManager.cs:133-136`) if the consumer
  treats each `DocumentControl` as a focus scope.

**Numbering source of truth (reconciling #1043 / #1067).** Numbering is *always* derived from
`IDock.VisibleDockables` in visual order — never an internal flat list. This directly implements the
#1067 directive and preserves #1043's scoping:
- The "active-workspace content" numbering of #1043 is expressed as **`FocusedDockOnly`** (or, more
  precisely, "the switchable strips within the active content dock"), each strip numbering `1..N`.
- The "workspace-tab host" numbering of #1043/#1067 is expressed by marking the workspace-tab host
  strip switchable under a *different* gesture set (e.g. `Alt+Shift`) — two independent
  `DockTabSwitch` configurations over two different scoping roots. The API can therefore express both
  the content-tab and pane-tab numberings that the product needs, without hardcoding either.

A single `DockTabOrder` service computes `IReadOnlyList<(IDock Strip, IDockable Dockable)>` in visual
order and is shared by **both** label assignment and activation (§4.5), so display and activation can
never diverge (the exact failure mode called out in #1067).

### 4.3 Requirement 3 — Replaceable index template

The badge is a `ControlTheme`-typed attached property, inheriting so a Dock-level default flows to all
strips but any strip can override it:

```csharp
public static readonly AttachedProperty<ControlTheme?> IndexThemeProperty =
    AvaloniaProperty.RegisterAttached<DockTabSwitch, Control, ControlTheme?>(
        "IndexTheme", inherits: true);
```

**Template data contract.** The badge template targets a tiny, dependency-free view object the
controller creates per realized header, `DockTabIndexContext` (a plain `AvaloniaObject`/INPC):

| Member | Type | Meaning |
|---|---|---|
| `Label` | `string?` | The displayed number ("1".."9","0", or "F1"…) — `null` ⇒ out of range, hide |
| `Index` | `int` | Zero-based order index |
| `IsVisible` | `bool` | True while the activation modifier is held (badge fade-in trigger) |

**Default template = the product's existing animated badge.** The default `IndexTheme` packages the
existing markup so consumers get the "nicely animated" badge for free:
- Visual: the `Border Classes="alt-index-badge"` + inner `TextBlock` from
  `features\Phantom.Workspaces\Templates\DockDataTemplates.axaml:99-108`.
- Animation: `Border.alt-index-badge` (`Opacity=0`, `DoubleTransition Opacity 0:0:0.1 LinearEasing`)
  and `Border.alt-index-badge.alt-held` (`Opacity=1`) from
  `features\Phantom.Workspaces.Gui.Shared\Styles\SharedStyles.axaml:1235-1250`.

These are lifted verbatim into a `ControlTheme TargetType="ContentPresenter"` (or a small
`DockTabIndexBadge : TemplatedControl`) shipped as
`Dock.Avalonia.TabSwitching`'s default resource, rebinding `Text→Label`,
`IsVisible→(Label is not null)`, and the `alt-held` pseudo-class → `IsVisible`. The default lives in
the new assembly's theme dictionary; `IndexThemeProperty`'s effective value falls back to it when
unset (resolved in the changed-handler, since attached-property `defaultValue` can't reference a
resource directly).

### 4.4 Requirement 4 — Automatic, composable injection into the tab header

The badge must appear on every tab header **without** replacing `PART_HeaderPresenter` or other
behaviors. Two composition strategies; the design ships **Strategy A** as default and documents B.

**Strategy A — extra `ContentPresenter` composed into the container theme (preferred).**
Ship a `ControlTheme` for `DocumentTabStripItem` (`BasedOn` the Fluent one at
`DocumentTabStripItem.axaml:287`) that adds one presenter into `PART_HeaderHost` *after*
`PART_HeaderPresenter` (`:370`):

```xml
<ContentPresenter x:Name="PART_IndexBadgePresenter"
                  Theme="{TemplateBinding (DockTabSwitch.IndexTheme)}"
                  Content="{Binding #PART_Root.(DockTabSwitch.IndexContext)}" />
```

Because it is a *sibling* presenter inside the existing `StackPanel` host, the header content and the
icon/modified/close presenters are untouched — this is composition, not replacement. Other behaviors
that add their own siblings coexist for the same reason. Grounded in the template structure at
`DocumentTabStripItem.axaml:355-378`.

**Strategy B — `AdornerLayer` overlay (no theme override).**
For consumers who cannot or will not swap the container `ControlTheme`, the controller instead
overlays the badge via the adorner layer:
`AdornerLayer.GetAdornerLayer(container)` then `SetAdornedElement`/`SetAdorner`
(`AdornerLayer.cs:73-101,63-71`). The adorner renders the same default badge control positioned over
the header container. This keeps the header content fully intact and is 100% additive, at the cost of
adorner-layer positioning bookkeeping.

**Container realization / virtualization.** The controller subscribes to the `DocumentTabStrip`'s
`ItemsControl.ContainerPrepared` (`ItemsControl.cs:227`) and `ContainerClearing` (`:246`) events:
- On `ContainerPrepared`, it attaches/refreshes the per-container `DockTabIndexContext` (Strategy A
  needs only to set the attached `IndexContext`; Strategy B (re)creates the adorner).
- On `ContainerClearing`, it detaches the adorner / clears context so recycled containers don't show a
  stale number.
- **Off-screen tabs still need a number**: labels are assigned to the *ordering model* (per
  `IDockable` position in `VisibleDockables`), not to containers. When a virtualized container is
  later realized, `ContainerPrepared` re-binds its context to the already-computed label. Numbering
  is thus independent of realization; only the *visual* badge is lazy.

Since the badge binds to the container (`DocumentTabStripItem`, an `AvaloniaObject`) and to a
controller-owned context — never to the dockable — it fully respects the non-`AvaloniaObject`
constraint from §3/#1067 and needs no cooperation from any view-model. It works for a plain
consumer's `DockControl`.

### 4.5 Activation path (index → dockable → focus)

`OnKeyDown` matches a gesture, gets `index`, and asks the shared `DockTabOrder` service (the *same*
ordering used for labels) for the dockable:

```csharp
var order = _order.Compute(_scopeRoot);          // IReadOnlyList<(IDock Strip, IDockable Dockable)>
if (index < 0 || index >= order.Count) return;    // out of range → not handled
var (strip, dockable) = order[index];
var factory = dockable.Factory;                   // IDockable.Factory
factory?.SetActiveDockable(dockable);             // IFactory.cs:303
factory?.SetFocusedDockable(strip, dockable);     // IFactory.cs:310
e.Handled = true;
```

Display (label assignment) and activation both consume `order`, guaranteeing one source of truth
(the explicit fix demanded by #1067). `SetActiveDockable` selects the tab within its strip;
`SetFocusedDockable` moves focus so subsequent keyboard input targets the newly active document.

---

## 5. Architecture — how the pieces compose

```
        ┌──────────────────────────────────────────────────────────────┐
        │ DockControl (root, tunnel KeyDown)                            │
        │   DockTabSwitch.Enabled = True                               │
        │   ├─ DockTabSwitchController (per-DockControl manager)        │
        │   │    • tunnel KeyDown handler  → gesture match → activate   │
        │   │    • modifier down/up        → AreBadgesVisible flag      │
        │   │    • subscribes ContainerPrepared/Clearing per strip      │
        │   │    • owns DockTabOrder (VisibleDockables, visual order)   │
        │   │    • owns default IndexTheme fallback                     │
        │   └─ Layout : IDock                                           │
        │        └─ DocumentDock ... DocumentControl                    │
        │             └─ DocumentTabStrip  (ItemsSource=VisibleDockables)│
        │                  └─ DocumentTabStripItem  (AvaloniaObject)     │
        │                       PART_HeaderHost                          │
        │                         ├ PART_HeaderPresenter (untouched)     │
        │                         └ PART_IndexBadgePresenter (injected)  │
        │                              Theme = DockTabSwitch.IndexTheme  │
        │                              Content = DockTabIndexContext     │
        └──────────────────────────────────────────────────────────────┘
```

- **Numbering** flows from `DockTabOrder.Compute(scopeRoot)` over `IDock.VisibleDockables`.
- **Display** = `DockTabIndexContext.Label`/`IsVisible` on each realized container, refreshed on
  `ContainerPrepared` and on layout/collection changes.
- **Key handling** = one tunnel handler on the `DockControl` (precedent:
  `DockControl.axaml.cs:252`), scoped by §4.2, activating via `IFactory` (§4.5).
- **Scoping** = inherited attached properties (`ToolTip` precedent) selecting the ordering root
  (whole layout vs focused dock).

All four requirements meet at the `DockTabSwitchController`, which is the only stateful object and is
created purely from attached properties — no view-model involvement.

---

## 6. Usage examples (XAML)

### 6.1 Defaults (opt-out switchable, Alt+1..0, animated badge)

```xml
<DockControl xmlns:ts="clr-namespace:Dock.Avalonia.TabSwitching;assembly=Dock.Avalonia.TabSwitching"
             ts:DockTabSwitch.Enabled="True"
             Layout="{Binding Layout}" />
```

Every document tab strip under the control is switchable; `Alt+1`…`Alt+9`, `Alt+0` activate tabs
1–10; holding `Alt` fades in the default animated number badges.

### 6.2 Opt a specific strip out

```xml
<DocumentDock ts:DockTabSwitch.IsSwitchable="False">
  <!-- these tabs are excluded from numbering/badges -->
</DocumentDock>
```

### 6.3 Override keys, scope, and badge template

```xml
<DockControl ts:DockTabSwitch.Enabled="True"
             ts:DockTabSwitch.Scope="FocusedDockOnly"
             ts:DockTabSwitch.IndexTheme="{StaticResource MyBadgeTheme}">
  <ts:DockTabSwitch.Gestures>
    <ts:DockTabSwitchGestures Modifiers="Control,Shift"
                              Keys="Digits,FunctionKeys" />
  </ts:DockTabSwitch.Gestures>
</DockControl>
```

`Ctrl+Shift+1..0` then `Ctrl+Shift+F1..F12`, numbering only the focused dock, with a custom badge.

### 6.4 Two independent numberings (content vs pane host — the product case)

```xml
<!-- content tabs: Alt+N, focused content dock -->
<DockControl ts:DockTabSwitch.Enabled="True"
             ts:DockTabSwitch.Scope="FocusedDockOnly">
  ...
  <!-- workspace-tab host strip: Alt+Shift+N, its own config -->
  <DocumentDock x:Name="WorkspacesDock"
                ts:DockTabSwitch.Enabled="True">
    <ts:DockTabSwitch.Gestures>
      <ts:DockTabSwitchGestures Modifiers="Alt,Shift" Keys="Digits" />
    </ts:DockTabSwitch.Gestures>
  </DocumentDock>
</DockControl>
```

---

## 7. Testing strategy

- **Gesture mapping (pure unit).** `DockTabSwitchGestures.BuildMap()` — digits→index, function
  keys→index, both-combined ordering, explicit `Gestures` override, and modifier exactness (Alt vs
  Alt+Shift). No UI needed.
- **Ordering source of truth.** Build an `IDock` tree with `VisibleDockables` (including split strips)
  and assert `DockTabOrder.Compute` yields visual order; assert labels and activation resolve to the
  *same* dockable for a given index (regression guard for #1067 divergence). Reorder/close and
  re-assert without relying on any flat projection.
- **Scope resolution.** `AllSwitchable` includes/excludes strips per inherited `IsSwitchable`;
  `FocusedDockOnly` follows `IDock.FocusedDockable` / `FocusManager` focus changes.
- **Activation.** Fake `IFactory`; assert `SetActiveDockable` + `SetFocusedDockable` called with the
  indexed dockable and its owning strip; out-of-range index is a no-op and leaves `e.Handled=false`.
- **Headless UI (Avalonia.Headless).** Realize a `DocumentTabStrip`, drive `ContainerPrepared`, assert
  the badge presenter/adorner appears and binds `Label`/`IsVisible`; scroll to force virtualization and
  assert recycled containers show the correct (not stale) number. Toggle the modifier and assert
  badge `IsVisible`/opacity animation state.
- **Key routing.** Simulate a tunnel `KeyDown` on the `DockControl` with a focused child text box;
  assert a modified gesture switches tabs and a bare key still reaches the text box.

---

## 8. Open questions / risks

1. **Virtualization vs badges.** If a `DocumentTabStrip` uses a virtualizing panel, only realized
   headers show badges; numbers ≥ the realized window are computed but invisible until scrolled into
   view. Acceptable, but if consumers expect *all* badges visible when the modifier is held, we may
   need to disable virtualization on switchable strips or pre-realize. Needs confirmation of the
   default `DocumentTabStrip` panel.
2. **Focus scoping precision.** `FocusManager.GetFocusedElement(IFocusScope)` is `[PrivateApi]`
   (`FocusManager.cs:133`); relying on it is risky. The Dock-model route
   (`IDock.FocusedDockable`) is public and preferred; the visual fallback should use the public
   `GetFocusedElement()` + ancestor walk only.
3. **Gesture conflicts.** Dock's own selector (`DockControl.KeyDownHandler`, `:728`) and app-level
   `KeyBinding`s / `HotKeyManager` hotkeys may claim the same chord. Because we handle in **tunnel**,
   we can pre-empt bubbling `KeyBinding`s, but must *not* swallow Dock's selector chord; the controller
   should ignore gestures whose modifier set equals Dock's `SelectorGesture` when the selector is
   enabled, and expose a `HandledEventsToo=false` policy so unmatched keys pass through.
4. **Non-`AvaloniaObject` dockables.** Confirmed constraint (`DockableBase.cs:13`): all visual state
   lives on the container + controller context, never on the dockable. Any future need to persist
   per-tab switch state must live in the dockable's `Context`/INPC, not attached properties.
5. **`Avalonia.Xaml.Interactivity` availability.** Not in the core tree; the default design avoids it.
   If we later expose a `Behavior<DocumentTabStripItem>`, it becomes an optional package dependency.
6. **Theme coupling.** Strategy A's default `ControlTheme` is `BasedOn` the *Fluent*
   `DocumentTabStripItem` theme (`:287`); consumers on a different Dock theme (Simple) need an
   equivalent `BasedOn`, or must use Strategy B (adorner) which is theme-agnostic.
7. **Multiple `TopLevel`s / floating windows.** A `DockControl`'s floated documents live in separate
   windows; the tunnel handler on the main `DockControl` won't see their key events. Each floating
   host would need its own controller (attach `Enabled` there too, or have the factory propagate it).

---

## 9. References

### Dock.Avalonia (v12.0.0.2) source
- `avalonia\Dock\src\Dock.Avalonia\Controls\DockControl.axaml.cs` — tunnel `KeyDown` handler `:252`,
  `KeyDownHandler` `:728`, pointer handlers `:245-251`.
- `avalonia\Dock\src\Dock.Avalonia\Controls\DocumentControl.axaml.cs` — hosts strip, `DataContext as IDock`.
- `avalonia\Dock\src\Dock.Avalonia.Themes.Fluent\Controls\DocumentControl.axaml:22-23` —
  `ItemsSource={Binding VisibleDockables}`, `SelectedItem={Binding ActiveDockable}`.
- `avalonia\Dock\src\Dock.Avalonia\Controls\DocumentTabStrip.axaml.cs:27` — `: TabStrip` (ItemsControl).
- `avalonia\Dock\src\Dock.Avalonia\Controls\DocumentTabStripItem.axaml.cs:24` — `: TabStripItem`
  (AvaloniaObject).
- `avalonia\Dock\src\Dock.Avalonia.Themes.Fluent\Controls\DocumentTabStripItem.axaml:287,355,362,370,373,378`
  — container `ControlTheme` and header `ContentPresenter` parts.
- `avalonia\Dock\src\Dock.Model\Core\IDock.cs:16,21,31` — `VisibleDockables`, `ActiveDockable`,
  `FocusedDockable`.
- `avalonia\Dock\src\Dock.Model\Core\IFactory.cs:303,310` — `SetActiveDockable`, `SetFocusedDockable`.
- `avalonia\Dock\src\Dock.Model.Inpc\Core\DockableBase.cs:13`,
  `avalonia\Dock\src\Dock.Model.Inpc\Controls\Document.cs:14` — dockables are `ReactiveBase` (INPC), not
  `AvaloniaObject`.
- `avalonia\Dock\src\Dock.Settings\DockProperties.cs` — attached-property precedent
  (`IsDockTarget`, `IsDragArea`, `DockGroup` inherits, …).

### Avalonia (12.0.4) source
- `Avalonia.Base\Input\KeyGesture.cs:13,20,158`; `Input\IKeyboardDevice.cs:7-15` (`KeyModifiers`);
  `Input\Key.cs:90-101,220-225` (`D0..D9`, `F1..F12`); `Input\KeyBinding.cs`.
- `Avalonia.Controls\HotkeyManager.cs:11,127-134,146` — attached-property → `KeyBinding` pattern.
- `Avalonia.Base\Input\InputElement.cs:106-109` — `KeyDownEvent` `Tunnel|Bubble`;
  `Interactivity\RoutedEvent.cs:6-12,121-135` — routing strategies / `AddClassHandler`.
- `Avalonia.Base\Input\FocusManager.cs:65,133-136` — `GetFocusedElement`.
- `Avalonia.Base\AvaloniaProperty.cs:355-376` — `RegisterAttached(..., inherits)`;
  `Avalonia.Controls\ToolTip.cs:75-82` — `inherits:true` precedent.
- `Avalonia.Controls\Primitives\AdornerLayer.cs:15,20-33,63-101` — adorner overlay.
- `Avalonia.Controls\ItemsControl.cs:218,227,246,713-717` — `PreparingContainer`/`ContainerPrepared`/
  `ContainerClearing`; `Presenters\ContentPresenter.cs`, `Styling\ControlTheme.cs:11-63`.
- Note: `Avalonia.Xaml.Interactivity` (`Behavior<T>`) is **not** in this source tree (separate NuGet).

### Product reference implementation (to be generalized / reused as defaults)
- `features\Phantom.Workspaces\MainWindow.axaml.cs:49-155,224-243` — Alt/Shift tracking, `Alt+N`
  activation, digit→index map.
- `features\Phantom.Workspaces\ViewModels\MainWindowViewModel.cs:2649,2663,2691,2754,2791` —
  `AltShortcutLabelForIndex`, label refresh, `ComputeGlobalTabOrder`, `PropagateBadgeVisibility`.
- `features\Phantom.Workspaces\ViewModels\WorkspaceDockFactory.cs:79-141`,
  `WorkspaceContentDock.cs`, `WorkspaceDocument.cs` (`TabViewModel` `:165`, `EffectiveTabHeader` `:109`),
  `TabHeaderViewModel.cs:80-124` (`AltShortcutLabel`, `IsShortcutBadgeVisible`).
- **Default badge template/animation to reuse:**
  `features\Phantom.Workspaces\Templates\DockDataTemplates.axaml:86-110` (badge markup) and
  `features\Phantom.Workspaces.Gui.Shared\Styles\SharedStyles.axaml:1235-1250`
  (`Border.alt-index-badge` opacity `DoubleTransition` 100 ms `LinearEasing`, `.alt-held` → opacity 1).

### Related issues (JoshuaRowePhantom/Phantom.Workspaces)
- **#1067** — Alt+N indexing & badges must be computed from `IDock.VisibleDockables`, not the internal
  flat `Tabs` list; dockables are plain CLR objects → attach visuals to the realized
  `DocumentTabStripItem` container. (Primary constraint driving §3–§4.)
- **#1065** — New tabs insert one position right within the source strip's `DocumentDock` /
  `VisibleDockables` (Dock as ordering authority).
- **#1043** — Alt+N numbering scoped to the active workspace's content dock (`1..N` per scope),
  Alt+Shift+N as an independent numbering over the workspace-tab host — expressible here via two
  scoped configs.
