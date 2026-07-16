# Dock Tab-Switching Numeric API

A reusable, easily-attachable API that adds **numeric keyboard tab-switching** (e.g. `Alt+1..Alt+0`)
plus **on-tab number badges** to an *arbitrary* [Avalonia.Dock](https://github.com/wieslawsoltes/Dock)
docking area, decoupled from any particular view-model.

Status: Design proposal
Scope: `Dock.Avalonia` (v12.0.0.2) + Avalonia (12.1.0) consumers.

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
- **Per-gesture-set scope, with multiple simultaneous bindings on one `DockControl`.** Scope is a
  property of each gesture set — *not* a single global setting — so a consumer can bind, on the same
  `DockControl`, e.g. `Alt+digit ⇒ AllSwitchable` **and** `Ctrl+Shift+digit ⇒ FocusedDockOnly`
  together. Two scopes are available: (a) *all switchable strips*, with "switchable" defaulting **on**
  at the Dock level (opt-out); (b) *the currently focused dock only*.
- **Replaceable index badge template**, defaulting to the product's existing animated badge.
- **Composable**: badge injection must not clobber the existing header content or other behaviors.
- Single ordering **source of truth** shared by display and activation, derived from
  `IDock.VisibleDockables` in visual order.
- Decoupled from `MainWindowViewModel`; works for a plain consumer's `DockControl`.

### Non-goals
- Redesigning Dock's drag/dock/split mechanics.
- Replacing Dock's built-in Ctrl+Tab *document selector* (`DockControl.KeyDownHandler`,
  `DockControl.axaml.cs:728`); the new API composes **alongside** it.
- Cross-`TopLevel`/floating-window global hotkeys handled by anything *other* than Dock's own factory
  registry (floating windows are covered automatically via `IFactory.DockControls`, see §8.6).
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
- `avalonia\Dock\src\Dock.Avalonia.Themes.Fluent\Controls\DocumentControl.axaml:21-23` —
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
  (`:713-717`). This is the hook to (re)apply badge adornment as containers are realized/recycled when
  the Dock model mutates (tabs added/removed/reordered).

### `DockControl` already installs a **tunnel** `KeyDown` handler
- `avalonia\Dock\src\Dock.Avalonia\Controls\DockControl.axaml.cs:252` —
  `AddHandler(KeyDownEvent, KeyDownHandler, RoutingStrategies.Tunnel);` and `KeyDownHandler` `:728`
  drives Dock's own document *selector* (Ctrl+Tab). This proves the pattern (root-level tunnel key
  handling on the `DockControl`) and gives us a precedent to sit beside.
- `KeyDownEvent` is registered `Tunnel | Bubble`
  (`avalonia\Avalonia\src\Avalonia.Base\Input\InputElement.cs:106-109`).

### Input / focus / attached-property primitives
- `KeyGesture(Key, KeyModifiers)` + `Matches(KeyEventArgs)` —
  `avalonia\Avalonia\src\Avalonia.Base\Input\KeyGesture.cs:20,60,158`.
- `KeyModifiers { None, Alt=1, Control=2, Shift=4, Meta=8 }` —
  `avalonia\Avalonia\src\Avalonia.Base\Input\IKeyboardDevice.cs:11-14`.
- `Key` enum: `D0=34..D9=43` (`avalonia\Avalonia\src\Avalonia.Base\Input\Key.cs:220-265`),
  `F1=90..F12=101` (`Key.cs:500-555`).
- `KeyBinding` (`Gesture` + `Command` + `TryHandle`) —
  `avalonia\Avalonia\src\Avalonia.Base\Input\KeyBinding.cs`.
- `HotKeyManager` — canonical attached-property-drives-behavior pattern; registers a `KeyBinding` on
  `TopLevel.KeyBindings` when the attached `HotKey` changes
  (`avalonia\Avalonia\src\Avalonia.Controls\HotkeyManager.cs:9,12,123-132`).
- **Attached property that inherits a default down the tree**:
  `AvaloniaProperty.RegisterAttached<TOwner,THost,TValue>(name, defaultValue, inherits:true, ...)`
  (`avalonia\Avalonia\src\Avalonia.Base\AvaloniaProperty.cs:355-374`); canonical use:
  `ToolTip.ShowOnDisabledProperty` / `ServiceEnabledProperty` with `inherits:true`
  (`avalonia\Avalonia\src\Avalonia.Controls\ToolTip.cs:76,82`). This is exactly how a Dock-level
  default flows to descendant strips but can be overridden per-strip.
- **Dock's own focus API (the authoritative focus source — preferred over Avalonia's visual
  `FocusManager`).** `IDock.FocusedDockable` (`avalonia\Dock\src\Dock.Model\Core\IDock.cs:31`) records
  the focused dockable; `IFactory.SetActiveDockable(IDockable)` /
  `SetFocusedDockable(IDock, IDockable?)` (`avalonia\Dock\src\Dock.Model\Core\IFactory.cs:303,310`)
  drive selection + focus; the implementation walks `IDockable.Owner`
  (`avalonia\Dock\src\Dock.Model\Core\IDockable.cs:31`) up to the focusable `IRootDock`
  (`IRootDock.IsFocusableRoot`, `avalonia\Dock\src\Dock.Model\Controls\IRootDock.cs:18`) and stores
  `root.FocusedDockable` (`avalonia\Dock\src\Dock.Model\FactoryBase.Init.cs:284,329`). `IDockable.Factory`
  is at `IDockable.cs:41`. Avalonia's visual `FocusManager.GetFocusedElement()`
  (`avalonia\Avalonia\src\Avalonia.Base\Input\FocusManager.cs:65`) exists but is **not** used for scope
  resolution; the scope-aware `GetFocusedElement(IFocusScope)` overload (`FocusManager.cs:133`) is
  `[PrivateApi]` and is deliberately avoided.
- `AdornerLayer.GetAdornerLayer(Visual)` (`:73`) + `SetAdornedElement` (`:68`) / `SetAdorner` (`:118`)
  attached props — `avalonia\Avalonia\src\Avalonia.Controls\Primitives\AdornerLayer.cs`.
- `IDock.VisibleDockables` / `ActiveDockable` / `FocusedDockable` —
  `avalonia\Dock\src\Dock.Model\Core\IDock.cs:16,21,31`.
- **`Avalonia.Xaml.Interactivity` is NOT in this Avalonia source tree and is NOT a Phantom.Workspaces
  dependency** (it ships as a separate NuGet package, `Avalonia.Xaml.Behaviors`, whose latest release is
  `11.1.0.x` — there is no `12.x` release matching Avalonia 12.1.0). To avoid a hard dependency and a
  version mismatch, the design uses **native attached properties + a manager object** (the
  `HotKeyManager` pattern) rather than `Behavior<T>`. See §8 for the full behaviors-vs-attached-property
  decision.

---

## 4. API Design

The API is a static attached-property host class `DockTabSwitch` in a new
`Phantom.Dock.Avalonia.TabSwitching` assembly/namespace, plus a small config object and a default badge
`ControlTheme`. Nothing here references any product view-model.

```
DockTabSwitch (static attached-property host, AvaloniaObject)
 ├─ Enabled            (attached, on DockControl)        → installs DockTabSwitchController
 ├─ Bindings           (attached, DockTabSwitchBindings) → collection of gesture-set + scope entries
 ├─ IsSwitchable       (attached, bool, inherits:true, default true)  → per-strip opt-out
 └─ IndexTheme         (attached, ControlTheme, inherits:true)        → replaceable badge

DockTabSwitchController   – per-DockControl manager (created by Enabled changed-handler)
DockTabSwitchBindings     – ordered collection of DockTabSwitchGestures (each carries its own Scope)
DockTabSwitchGestures     – modifier mask + key ranges + Scope → ordered KeyGesture list, index map
DockIndexBadgeBehavior    – per-DocumentTabStripItem adornment (composable)
```

Each `DockControl` can carry **multiple** gesture sets simultaneously, and **each gesture set owns its
own scope**. This is what lets a single `DockControl` bind, for example, `Alt+digit ⇒ AllSwitchable`
alongside `Ctrl+Shift+digit ⇒ FocusedDockOnly` (see §4.2 and §6.3).

### 4.1 Requirement 1 — Configurable key set

**Config object.** A consumer declares one or more gesture sets declaratively. **Scope is a property
of each gesture set** (not a single global setting), so multiple sets with different scopes can be
bound to the same `DockControl`:

```csharp
public sealed class DockTabSwitchGestures
{
    // Which modifier(s) must be held. Default: Alt.
    public KeyModifiers Modifiers { get; set; } = KeyModifiers.Alt;

    // Which key ranges participate, in the order they map to 1..N.
    // Default: Digits (D1..D9, then D0 == index 10).
    public DockTabSwitchKeys Keys { get; set; } = DockTabSwitchKeys.Digits;

    // The scope THIS gesture set numbers/activates. Default: AllSwitchable.
    public DockTabSwitchScope Scope { get; set; } = DockTabSwitchScope.AllSwitchable;

    // Optional explicit override: an ordered list wins over Modifiers/Keys.
    public IList<KeyGesture>? Gestures { get; set; }
}

// A DockControl carries a collection of gesture sets (one per gesture→scope binding).
public sealed class DockTabSwitchBindings : AvaloniaList<DockTabSwitchGestures> { }

[Flags] public enum DockTabSwitchKeys { Digits = 1, FunctionKeys = 2 }
public enum DockTabSwitchScope { AllSwitchable, FocusedDockOnly }
```

The `DockTabSwitch.Bindings` attached property holds the collection; the controller installs one
gesture→index→activation pipeline per entry, each resolving its ordering root from that entry's own
`Scope` (§4.2). A convenience `DockTabSwitch.Gestures` shorthand accepts a single `DockTabSwitchGestures`
and is sugar for a one-element `Bindings`. Gesture sets on the same `DockControl` are matched
independently, so `Alt+1` and `Ctrl+Shift+1` can coexist and target different scopes.

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
(`HotkeyManager.cs:123-132`); the tunnel handler is preferred because it (a) needs no focused command
target and (b) participates in scope resolution (§4.2).

### 4.2 Requirement 2 — Scope (per-gesture-set, multiple simultaneous bindings)

**Scope is a property of each gesture set, not a single global switch.** This is the key design point:
a `DockControl` may carry several `DockTabSwitchGestures` entries at once (via `DockTabSwitch.Bindings`),
and the controller runs one independent pipeline per entry, each resolving its ordering root from *that
entry's* `Scope`. So `Alt+digit ⇒ AllSwitchable` and `Ctrl+Shift+digit ⇒ FocusedDockOnly` can be active
on the same control simultaneously (exact XAML in §6.3).

The per-strip **opt-out** remains an inherited attached property so a value on the `DockControl` (or any
ancestor) cascades but can be overridden lower down — using the exact `ToolTip` precedent
(`ToolTip.cs:76,82`):

```csharp
// Default TRUE at the Dock scope → opt-out, not opt-in.
public static readonly AttachedProperty<bool> IsSwitchableProperty =
    AvaloniaProperty.RegisterAttached<DockTabSwitch, Control, bool>(
        "IsSwitchable", defaultValue: true, inherits: true);

// The set of gesture→scope bindings for this DockControl.
public static readonly AttachedProperty<DockTabSwitchBindings?> BindingsProperty =
    AvaloniaProperty.RegisterAttached<DockTabSwitch, Control, DockTabSwitchBindings?>("Bindings");

public enum DockTabSwitchScope { AllSwitchable, FocusedDockOnly }
```

Because `Scope` lives on each `DockTabSwitchGestures`, there is no global `Scope` attached property to
reconcile: a single-scope consumer sets `Scope` on one gesture set (or uses the `DockTabSwitch.Gestures`
shorthand); a multi-scope consumer adds several entries to `Bindings`, each with its own `Scope`.

**(a) AllSwitchable (default).** For a gesture set whose `Scope` is `AllSwitchable`, every
`DocumentTabStrip` under the `DockControl` is switchable unless `DockTabSwitch.IsSwitchable="False"` is
set on that strip (or an ancestor). Because the property inherits and defaults `true`, the default
behavior is opt-out. Numbering is computed by walking the Dock model tree from the `DockControl`'s
`Layout` (`IDock`), collecting each switchable strip's `IDock.VisibleDockables` in visual order.

**(b) FocusedDockOnly.** For a gesture set whose `Scope` is `FocusedDockOnly`, only the strip belonging
to the currently focused dock is numbered. **The focused dock is resolved through Dock's own focus
API** — never Avalonia's visual `FocusManager`:
- The controller reads the focusable root's `FocusedDockable` (`IDock.FocusedDockable`, `IDock.cs:31`)
  — the same field Dock's `IFactory.SetFocusedDockable` writes when a document is activated
  (`FactoryBase.Init.cs:329`, walking `IDockable.Owner` up to the `IRootDock` whose `IsFocusableRoot`
  is true, `IRootDock.cs:18`).
- From that `FocusedDockable` it walks the `IDockable.Owner` chain (`IDockable.cs:31`) to the owning
  `IDock` (the `DocumentDock`/tab strip) and numbers that dock's `VisibleDockables`.
- This is fully public Dock API (`IDock`/`IFactory`/`IRootDock`), needs no visual-tree walk, and stays
  correct across floating windows because `SetFocusedDockable` is root-aware. Avalonia's
  `FocusManager.GetFocusedElement(IFocusScope)` (`FocusManager.cs:133`) is `[PrivateApi]` and is not
  used.

**Numbering source of truth (reconciling #1043 / #1067).** Numbering is *always* derived from
`IDock.VisibleDockables` in visual order — never an internal flat list. This directly implements the
#1067 directive and preserves #1043's scoping:
- The "active-workspace content" numbering of #1043 is expressed as **`FocusedDockOnly`** (or, more
  precisely, "the switchable strips within the active content dock"), each strip numbering `1..N`.
- The "workspace-tab host" numbering of #1043/#1067 is expressed as a *second* gesture set (e.g.
  `Alt+Shift`, `Scope=AllSwitchable` restricted to the workspace-tab host strip) added to the same
  `DockControl`'s `DockTabSwitch.Bindings` — two gesture→scope entries over different scoping roots. The
  API can therefore express both the content-tab and pane-tab numberings that the product needs, without
  hardcoding either.

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
controller creates per realized header, `DockTabIndexContext` (a plain `AvaloniaObject`/INPC). Because
a `DockControl` may carry several gesture sets at once (§4.2), a **single tab can be numbered by more
than one binding** — e.g. an `Alt+1` binding *and* a `Ctrl+Shift+F1` binding both target the same tab.
The context therefore exposes a **collection** of per-binding labels (`Labels`) as well as a
single-label convenience (`Label`, the first/primary entry) so simple custom themes stay trivial:

| Member | Type | Meaning |
|---|---|---|
| `Labels` | `IReadOnlyList<DockTabIndexLabel>` | One entry per gesture set that numbers this tab (may be empty, one, or several). Each `DockTabIndexLabel` carries `Text` ("1".."9","0", or "F1"…) and the originating gesture set. |
| `Label` | `string?` | Convenience: `Labels[0].Text` (the primary binding), or `null` ⇒ out of range, hide |
| `Index` | `int` | Zero-based order index of the primary binding |
| `IsVisible` | `bool` | True while the activation modifier is held (badge fade-in trigger) |

**Default theme — hidden by default, overlapping, sized to the widest label.** The default `IndexTheme`
is intentionally minimal in the space it reserves:

- **Hidden by default.** Label text is never shown while no activation modifier is held; the badges only
  fade in while the modifier is down (driven by `IsVisible`, exactly the existing `alt-held` behavior).
- **Overlap, don't lay out side-by-side.** When more than one label applies to a tab (multiple gesture
  sets number it, e.g. both `Alt+1` and `Ctrl+Shift+F1`), the default theme **stacks the labels in the
  same cell** rather than flowing them horizontally, so no extra horizontal space is reserved for the
  second, third, … label. Concretely, the default theme hosts `Labels` in a single-cell `Panel` (a
  `Grid` with one row/column, or a `Panel` whose children all occupy the same position) so every label
  is drawn on top of the same spot. Because only one label is legible at a time (they share a cell), the
  activation-modifier that is currently held determines which label reads on top — the theme raises the
  label whose gesture set matches the held modifier to the front.
- **Reserve space for the largest label.** The single overlapping cell measures to the **widest**
  member of `Labels`, so if a tab carries both `"1"` and `"F1"`, the reserved/measured width is that of
  `"F1"` (the larger), and the `"1"` badge simply centres within that same footprint. This is achieved
  by letting the overlapping `Panel` size to its largest child (a `Panel`/`Grid` naturally measures to
  the union of its children's desired sizes) — no per-label padding is added, so a tab with a single
  short label reserves only that label's width.

**Default template = the product's existing animated badge.** The default `IndexTheme` packages the
existing markup so consumers get the "nicely animated" badge for free:
- Visual: the `Border Classes="alt-index-badge"` + inner `TextBlock` from
  `features\Phantom.Workspaces\Templates\DockDataTemplates.axaml:99-108`.
- Animation: `Border.alt-index-badge` (`Opacity=0`, `DoubleTransition Opacity 0:0:0.1 LinearEasing`)
  and `Border.alt-index-badge.alt-held` (`Opacity=1`) from
  `features\Phantom.Workspaces.Gui.Shared\Styles\SharedStyles.axaml:1235-1250`.

These are lifted verbatim into a `ControlTheme TargetType="ContentPresenter"` (or a small
`DockTabIndexBadge : TemplatedControl`) shipped as
`Phantom.Dock.Avalonia.TabSwitching`'s default resource, rebinding `Text→Label`,
`IsVisible→(Label is not null)`, and the `alt-held` pseudo-class → `IsVisible`. When more than one
label applies (multiple gesture sets, see the data contract above), the default theme wraps the
per-label badges in a single-cell overlapping `Panel` that measures to the widest label, so the
markup above is instanced once per member of `Labels` and the instances are drawn on top of one
another. The default lives in the new assembly's theme dictionary; `IndexThemeProperty`'s effective
value falls back to it when unset (resolved in the changed-handler, since attached-property
`defaultValue` can't reference a resource directly).

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

**Container realization (headers are *not* virtualized).** The `DocumentTabStrip` uses a
non-virtualizing items panel in every orientation, so **all** tab-header containers are realized at all
times — there is no off-screen-badge problem:
- Horizontal strips use a plain `StackPanel`
  (`avalonia\Dock\src\Dock.Avalonia.Themes.Fluent\Accents\Fluent.axaml:177-179`, the
  `DockDocumentTabStripHorizontalItemsPanel` resource referenced from
  `DocumentTabStrip.axaml:246-250`).
- Vertical strips use a `UniformGrid` (`DocumentTabStrip.axaml:228-232`).
- The Avalonia `TabStrip` base default is a `WrapPanel`
  (`avalonia\Avalonia\src\Avalonia.Themes.Fluent\Controls\TabStrip.xaml:27-31`).

None of these virtualizes, so every header — and thus every badge — is materialized whenever the strip
is laid out. The controller still subscribes to `ItemsControl.ContainerPrepared` (`ItemsControl.cs:227`)
and `ContainerClearing` (`:246`) to (re)apply/detach the per-container `DockTabIndexContext` as the Dock
model mutates (tabs added/removed/reordered) and as containers are recycled:
- On `ContainerPrepared`, it attaches/refreshes the `DockTabIndexContext` (Strategy A sets the attached
  `IndexContext`; Strategy B (re)creates the adorner).
- On `ContainerClearing`, it detaches the adorner / clears context so recycled containers don't show a
  stale number.

Labels are assigned to the *ordering model* (per `IDockable` position in `VisibleDockables`), not to
containers, so numbering stays authoritative regardless of container lifecycle. Even if a consumer
swapped in a virtualizing panel, a not-yet-realized tab would by definition be off-screen and its badge
invisible anyway, so this is not a correctness risk (see §8).

Since the badge binds to the container (`DocumentTabStripItem`, an `AvaloniaObject`) and to a
controller-owned context — never to the dockable — it fully respects the non-`AvaloniaObject`
constraint from §3/#1067 and needs no cooperation from any view-model. It works for a plain
consumer's `DockControl`.

### 4.5 Activation path (index → dockable → focus)

`OnKeyDown` tries each gesture set in `DockTabSwitch.Bindings`; when one matches, it gets `index` and
asks the shared `DockTabOrder` service (the *same* ordering used for labels) for the dockable, using
that gesture set's own scope root:

```csharp
var order = _order.Compute(binding.ResolveScopeRoot());  // per-binding scope (§4.2)
if (index < 0 || index >= order.Count) return;    // out of range → not handled
var (strip, dockable) = order[index];
var factory = dockable.Factory;                   // IDockable.Factory (IDockable.cs:41)
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

- **Numbering** flows from `DockTabOrder.Compute(scopeRoot)` over `IDock.VisibleDockables`, one scope
  root per gesture set in `DockTabSwitch.Bindings`.
- **Display** = `DockTabIndexContext.Label`/`IsVisible` on each realized container, refreshed on
  `ContainerPrepared` and on layout/collection changes.
- **Key handling** = one tunnel handler on the `DockControl` (precedent:
  `DockControl.axaml.cs:252`) dispatching to each gesture set, activating via `IFactory` (§4.5).
- **Scoping** = each gesture set's own `Scope` selects its ordering root (whole layout vs the focused
  dock via Dock's focus API), with the inherited `IsSwitchable` opt-out (`ToolTip` precedent)
  filtering strips.

All four requirements meet at the `DockTabSwitchController`, which is the only stateful object and is
created purely from attached properties — no view-model involvement.

---

## 6. Usage examples (XAML)

### 6.1 Defaults (opt-out switchable, Alt+1..0, animated badge)

```xml
<DockControl xmlns:ts="clr-namespace:Phantom.Dock.Avalonia.TabSwitching;assembly=Phantom.Dock.Avalonia.TabSwitching"
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

### 6.3 Override keys, scope, and badge template (single gesture set)

```xml
<DockControl ts:DockTabSwitch.Enabled="True"
             ts:DockTabSwitch.IndexTheme="{StaticResource MyBadgeTheme}">
  <ts:DockTabSwitch.Gestures>
    <ts:DockTabSwitchGestures Modifiers="Control,Shift"
                              Keys="Digits,FunctionKeys"
                              Scope="FocusedDockOnly" />
  </ts:DockTabSwitch.Gestures>
</DockControl>
```

`Ctrl+Shift+1..0` then `Ctrl+Shift+F1..F12`, numbering only the focused dock, with a custom badge.
Scope is set **on the gesture set** (`Scope="FocusedDockOnly"`), not on the `DockControl`.

**Authoring `MyBadgeTheme`.** The badge is a `ControlTheme` whose `TargetType` is the presenter the API
injects (`ContentPresenter`, §4.4), and whose `DataContext` is the controller-owned `DockTabIndexContext`
(§4.3): `Label` (`string?`, the displayed number or `null` when out of range), `Index` (`int`), and
`IsVisible` (`bool`, true while the activation modifier is held). A minimal custom theme:

```xml
<ControlTheme x:Key="MyBadgeTheme" TargetType="ContentPresenter">
  <Setter Property="Content">
    <Template>
      <!-- DataContext is the DockTabIndexContext for this tab -->
      <Border Classes.visible="{Binding IsVisible}"
              Background="{DynamicResource SystemAccentColor}"
              CornerRadius="4" Padding="3,1"
              HorizontalAlignment="Right" VerticalAlignment="Top"
              Opacity="0"
              IsVisible="{Binding Label, Converter={x:Static StringConverters.IsNotNullOrEmpty}}">
        <Border.Transitions>
          <Transitions>
            <DoubleTransition Property="Opacity" Duration="0:0:0.1" Easing="LinearEasing" />
          </Transitions>
        </Border.Transitions>
        <TextBlock Text="{Binding Label}" Classes="caption" HorizontalAlignment="Center" />
      </Border>
    </Template>
  </Setter>
  <!-- fade the badge in only while the activation modifier is held (IsVisible → .visible class) -->
  <Style Selector="^ /template/ Border.visible">
    <Setter Property="Opacity" Value="1" />
  </Style>
</ControlTheme>
```

The custom theme above shows the *single-label* case. The **default** theme differs deliberately: it
keeps the labels hidden until the activation modifier is held, and when a tab is numbered by more than
one gesture set it **overlaps** the labels in a single cell (sized to the widest label) rather than
laying them out side by side — see §4.3 "Default theme — hidden by default, overlapping, sized to the
widest label." A custom theme is free to bind the whole `Labels` collection the same way if it wants
the same overlap behaviour; binding only `Label` (the primary) is sufficient for the common single-set
case.

Here `Label` drives the text and self-hiding, and `IsVisible` toggles the `.visible` class that fades the
badge in — exactly mirroring the default badge's `Border.alt-index-badge` / `.alt-held` opacity
transition (§4.3). `Index` is available for tests/diagnostics but not needed for display.

### 6.4 Two simultaneous gesture→scope bindings on one `DockControl`

Because `Scope` is per-gesture-set, `Alt` can drive **global** switching (all switchable strips) while
`Ctrl+Shift` drives **focused-dock-only** switching at the same time — the exact scenario the maintainer
asked about:

```xml
<DockControl ts:DockTabSwitch.Enabled="True">
  <ts:DockTabSwitch.Bindings>
    <!-- Alt+digit → every switchable strip -->
    <ts:DockTabSwitchGestures Modifiers="Alt" Keys="Digits"
                              Scope="AllSwitchable" />
    <!-- Ctrl+Shift+digit → only the focused dock -->
    <ts:DockTabSwitchGestures Modifiers="Control,Shift" Keys="Digits"
                              Scope="FocusedDockOnly" />
  </ts:DockTabSwitch.Bindings>
</DockControl>
```

Both pipelines run concurrently: `Alt+1` activates the first tab in global order, and `Ctrl+Shift+1`
activates the first tab in the currently focused dock. They never interfere because gesture matching is
modifier-exact (`KeyGesture.Matches`, §4.1).

### 6.5 Two independent numberings (content vs pane host — the product case)

```xml
<!-- content tabs and workspace-tab host, two gesture sets on the root DockControl -->
<DockControl ts:DockTabSwitch.Enabled="True">
  <ts:DockTabSwitch.Bindings>
    <!-- content tabs: Alt+N, focused content dock -->
    <ts:DockTabSwitchGestures Modifiers="Alt" Keys="Digits" Scope="FocusedDockOnly" />
    <!-- workspace-tab host: Alt+Shift+N, all switchable strips (host opted-in) -->
    <ts:DockTabSwitchGestures Modifiers="Alt,Shift" Keys="Digits" Scope="AllSwitchable" />
  </ts:DockTabSwitch.Bindings>
  ...
  <!-- exclude the content docks from the Alt+Shift host numbering via the inherited opt-out -->
  <DocumentDock x:Name="WorkspacesDock" ts:DockTabSwitch.IsSwitchable="True" />
</DockControl>
```

### 6.6 What a consumer must do (integration steps)

Integrating this API into your own `DockControl` is deliberately small, but it is **not** completely
zero-config — the badge visuals rely on theme resources that must be merged. Concretely:

1. **Reference the package** `Phantom.Dock.Avalonia.TabSwitching` and add the XAML namespace
   (`xmlns:ts="clr-namespace:Phantom.Dock.Avalonia.TabSwitching;assembly=Phantom.Dock.Avalonia.TabSwitching"`).

2. **Merge the default theme dictionary** into your `Application.Resources` (or a merged dictionary):

   ```xml
   <Application.Styles>
     <ts:DockTabSwitchTheme />   <!-- ships the default IndexTheme + DocumentTabStripItem ControlTheme -->
   </Application.Styles>
   ```

   This is required for **Strategy A** (§4.4): it supplies the `DocumentTabStripItem` `ControlTheme`
   (`BasedOn` the Fluent theme, `DocumentTabStripItem.axaml:287`) that adds `PART_IndexBadgePresenter`
   into `PART_HeaderHost`, and it registers the default animated badge as the fallback `IndexTheme`. If
   you skip this merge, `DockTabSwitch.Enabled` still wires key handling, but no badge appears.

3. **Set `ts:DockTabSwitch.Enabled="True"`** on your `DockControl` (plus any `Bindings`/`Gestures`/
   `IsSwitchable`/`IndexTheme` overrides). That single property installs the controller; scope,
   numbering, and floating-window propagation (§8.6) are automatic.

4. **Theme variant.** The default `ControlTheme` is `BasedOn` the **Fluent** `DocumentTabStripItem`
   theme. If you use the **Simple** Dock theme (or a custom one), either provide an equivalent
   `BasedOn` theme keyed to your `DocumentTabStripItem`, **or** opt into **Strategy B** (the adorner
   overlay, §4.4) which is theme-agnostic and needs no `ControlTheme` `BasedOn` wiring —
   `ts:DockTabSwitch.Composition="Adorner"`.

5. **Remove any pre-existing key handler** that claims the same chord you configure (e.g. a legacy
   `Alt+digit` `OnPreviewKeyDown`); see §8.3. Nothing else — no view-model members, no per-tab wiring —
   is required, because badges bind only to the controller-owned context on the realized container.

**Do they have to do anything?** Yes: steps 1–3 are mandatory (namespace, merge the theme dictionary,
set `Enabled`); step 4 applies only to non-Fluent themes; step 5 applies only if a conflicting handler
already exists. There is no code-behind or view-model change.

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
  `FocusedDockOnly` follows Dock's own focus API — set `IRootDock.FocusedDockable` via
  `IFactory.SetFocusedDockable` and assert numbering moves to the owning dock (no visual `FocusManager`
  needed). Assert multiple gesture sets on one `DockControl` resolve independent scope roots.
- **Activation.** Fake `IFactory`; assert `SetActiveDockable` + `SetFocusedDockable` called with the
  indexed dockable and its owning strip; out-of-range index is a no-op and leaves `e.Handled=false`.
- **Headless UI (Avalonia.Headless).** Realize a `DocumentTabStrip`, drive `ContainerPrepared`, assert
  the badge presenter/adorner appears and binds `Label`/`IsVisible`. Because the strip's items panel is
  non-virtualizing (§4.4), assert every tab header has a badge. Toggle the modifier and assert badge
  `IsVisible`/opacity animation state.
- **Key routing.** Simulate a tunnel `KeyDown` on the `DockControl` with a focused child text box;
  assert a modified gesture switches tabs and a bare key still reaches the text box.

---

## 8. Resolved design decisions (former open questions)

1. **Virtualization vs badges — not an issue; headers are not virtualized.** The maintainer is correct:
   an unrealized header cannot be visible, so it cannot need a visible badge. Moreover, the
   `DocumentTabStrip` panels are **non-virtualizing** in every orientation — horizontal `StackPanel`
   (`Fluent.axaml:177-179` via `DocumentTabStrip.axaml:246-250`), vertical `UniformGrid`
   (`DocumentTabStrip.axaml:228-232`), and the Avalonia `TabStrip` base default `WrapPanel`
   (`TabStrip.xaml:27-31`). Every header (and badge) is therefore always realized. Numbering is derived
   from the `VisibleDockables` ordering model independently of container lifecycle (§4.4), so there is
   no correctness risk. **This risk is removed.**

2. **Focus scoping uses Dock's own focus API.** `FocusedDockOnly` resolves the focused dock through
   `IDock.FocusedDockable` (`IDock.cs:31`), which `IFactory.SetFocusedDockable` (`IFactory.cs:310`)
   maintains on the focusable `IRootDock` (`FactoryBase.Init.cs:329`; `IRootDock.IsFocusableRoot`,
   `IRootDock.cs:18`), walking the `IDockable.Owner` chain (`IDockable.cs:31`) to the owning dock. This
   is fully public Dock API. Avalonia's visual `FocusManager` — including the `[PrivateApi]`
   `GetFocusedElement(IFocusScope)` (`FocusManager.cs:133`) — is **not** used for scope resolution
   (§4.2). **Resolved: Dock focus API is primary; no `FocusManager` fallback.**

3. **Gesture conflicts — determined against source; no collision with Dock, one collision with the
   product's own legacy handler (which this API replaces).**
   - **Dock consumes only `Tab`-based chords.** Its selector matches `Ctrl+Tab` (documents) and
     `Ctrl+Alt+Tab` (tools), plus their `Shift` reverse variants — `s_documentSelectorKeyGesture` /
     `s_toolSelectorKeyGesture` (`DockSettings.cs:125-126`), matched in
     `MatchesSelectorGesture` (`DockControl.axaml.cs:894-917`) and dispatched from `KeyDownHandler`
     (`:728`, via `TryStartSelector` `:764`). Dock consumes **no** digit or function-key chord. So
     `Alt+digit`, `Ctrl+Shift+digit`, and `F1..F12` do **not** collide with Dock.
   - **The product's own bindings.** The existing bespoke feature already binds `Alt+digit` and
     `Alt+Shift+digit` in `MainWindow.axaml.cs:127-136`, and `F7`/`F8` (and `Ctrl+F7`/`Ctrl+F8`) are
     reserved for notification navigation (`MainWindow.axaml:23-24`, `MainWindow.axaml.cs:87-100`).
     These are not *conflicts* to design around — they are exactly the handlers this API **replaces**:
     adopting `DockTabSwitch` means deleting the `OnPreviewKeyDown` `Alt+N` logic and letting the
     controller own those chords. A consumer choosing default `Alt+digit` must therefore remove any
     prior `Alt+digit` handler; a consumer wanting to keep the legacy handler picks a non-conflicting
     set (e.g. `Ctrl+Shift+digit`, which is free in both Dock and the product).
   - **Coexistence mechanics.** The controller handles in **tunnel** and sets `e.Handled=true` only on a
     matched, in-range gesture, so unmatched keys still reach Dock's bubble-phase selector and app
     `KeyBinding`s. It never matches a bare `Tab`, so it can never swallow Dock's selector chord.
     **Resolved: no Dock collision; product `Alt+digit`/F7/F8 are superseded or avoided by choosing a
     free gesture set.**

4. **Badge labels are ephemeral — nothing is persisted.** The index labels are computed live from each
   dock's `VisibleDockables` ordering (§4.2/§4.5) and held only in the transient, controller-owned
   `DockTabIndexContext` on the realized header container. They are recomputed on every layout/collection
   change and are **never** written to the dockable, its `Context`, or any saved layout. Dockables are
   plain `ReactiveBase` INPC objects (`DockableBase.cs:13`), so there are no attached properties on them
   to persist anyway. **There is no persisted switch state; the numbering is purely derived, live data.**

5. **`Avalonia.Xaml.Interactivity` behaviors vs bespoke attached properties — decision: keep the
   attached-property + controller approach; offer a Behavior adapter only as optional sugar.** The
   `Avalonia.Xaml.Behaviors` source was fetched to `c:\dev\avalonia\Avalonia.Xaml.Behaviors` and its
   model reviewed: `Behavior`/`Behavior<T>` are `AvaloniaObject`s with `OnAttached`/`OnDetaching`
   lifecycle and `OnAttachedToVisualTree`/`OnDetachedFromVisualTree` hooks
   (`src\Avalonia.Xaml.Interactivity\Behavior.cs:11,58,88`, `BehaviorOfT.cs:11`), attached to a control
   through `Interaction.Behaviors` (`Interaction.cs:23,152-168`).

   | Factor | Behavior<T> | Attached-property + controller (chosen) |
   |---|---|---|
   | Already a product dependency? | **No** — not referenced in `Directory.Packages.props` or any `features` csproj | Yes — pure Avalonia, no new package |
   | Version fit with Avalonia 12.1.0 | **Poor** — latest release is `11.1.0.x`, no `12.x` tag | N/A |
   | Attach/detach lifecycle | Good (`OnAttached`/visual-tree hooks) | Equivalent via `Enabled` changed-handler + `ContainerPrepared`/`Clearing` |
   | XAML ergonomics | `Interaction.Behaviors` collection per item | One attached property on the `DockControl`; simpler for the "one line" goal |
   | Composability with other behaviors | Native behavior composition | Composes at the theme/adorner layer (§4.4); orthogonal to behaviors |
   | Trimming | `Behavior<T>.OnAttached` is `[RequiresUnreferencedCode]` (`BehaviorOfT.cs:24`) | No trim warnings |

   **Recommendation:** the bespoke approach is better here — it adds **no** dependency, matches the
   in-box `HotKeyManager` precedent, avoids the 12.x version gap, and keeps the "attach on the
   `DockControl` with one property" ergonomics. Where a consumer *already* uses
   `Avalonia.Xaml.Behaviors`, a thin optional `DockTabSwitchBehavior : Behavior<DockControl>` wrapper can
   forward to the same controller — but it is not the primary API. **Decision recorded: attached-property
   + controller.**

6. **Floating windows are handled through Dock's factory, not per-window manual wiring.** A floated
   document lives in a Dock `HostWindow` (`HostWindow.axaml.cs:25`, a `Window : IHostWindow`) whose
   template hosts its **own** inner `DockControl` bound to the floated layout
   (`Dock.Avalonia.Themes.Fluent\Controls\HostWindow.axaml`). Every `DockControl` — main or floating —
   registers itself in the factory: `layout.Factory.DockControls.Add(this)`
   (`DockControl.axaml.cs:515`), and `IFactory.DockControls` (`IFactory.cs:57`) is the authoritative
   registry of them all. So the controller does **not** need a hand-attached `Enabled` on each window.
   Instead, when `DockTabSwitch.Enabled` installs the controller on the root `DockControl`, the
   controller subscribes to `IFactory.DockControls` (an observable collection) and attaches the same
   gesture/badge pipeline to **every** `DockControl` that appears — including the inner `DockControl` of
   each `HostWindow` as floats are created. Two supporting hooks exist if finer control is needed:
   `IFactory.HostWindowLocator` / `DefaultHostWindowLocator` (`IFactory.cs:107,117`), and
   `DockControl.HostWindowFactory` — a consumer can supply a `HostWindow` subclass whose inner
   `DockControl` already carries `DockTabSwitch.Enabled`. **Resolved: propagate via `IFactory.DockControls`
   (primary), with `HostWindowLocator`/`HostWindowFactory` as override points.**

---

## 9. References

Source worktrees are pinned to the in-use versions: **Avalonia 12.1.0**
(`avalonia\Avalonia`, commit `a21b9f57`) and **Dock.Avalonia 12.0.0.2** (`avalonia\Dock`, commit
`ebbf3d46`). All line numbers below were re-verified against these worktrees.

### Dock.Avalonia (v12.0.0.2) source
- `avalonia\Dock\src\Dock.Avalonia\Controls\DockControl.axaml.cs` — tunnel `KeyDown` handler `:252`,
  `KeyDownHandler` `:728`, `TryStartSelector` `:764`, `MatchesSelectorGesture` `:894-917`, self-registers
  in the factory `DockControls.Add(this)` `:515`.
- `avalonia\Dock\src\Dock.Settings\DockSettings.cs:125-126` — `s_documentSelectorKeyGesture` (`Ctrl+Tab`)
  and `s_toolSelectorKeyGesture` (`Ctrl+Alt+Tab`); the only chords Dock's selector consumes.
- `avalonia\Dock\src\Dock.Avalonia\Controls\DocumentControl.axaml.cs` — hosts strip, `DataContext as IDock`.
- `avalonia\Dock\src\Dock.Avalonia.Themes.Fluent\Controls\DocumentControl.axaml:21-23` —
  `ItemsSource={Binding VisibleDockables}`, `SelectedItem={Binding ActiveDockable}`.
- `avalonia\Dock\src\Dock.Avalonia\Controls\DocumentTabStrip.axaml.cs:27` — `: TabStrip` (ItemsControl).
- `avalonia\Dock\src\Dock.Avalonia.Themes.Fluent\Controls\DocumentTabStrip.axaml:228-232` (vertical
  `UniformGrid`), `:246-250` (horizontal panel setter) and
  `avalonia\Dock\src\Dock.Avalonia.Themes.Fluent\Accents\Fluent.axaml:177-179`
  (`DockDocumentTabStripHorizontalItemsPanel` = plain `StackPanel`) — **non-virtualizing** items panels.
- `avalonia\Dock\src\Dock.Avalonia\Controls\DocumentTabStripItem.axaml.cs:24` — `: TabStripItem`
  (AvaloniaObject).
- `avalonia\Dock\src\Dock.Avalonia.Themes.Fluent\Controls\DocumentTabStripItem.axaml:287,355,362,370,373,378`
  — container `ControlTheme` and header `ContentPresenter` parts.
- `avalonia\Dock\src\Dock.Model\Core\IDock.cs:16,21,31` — `VisibleDockables`, `ActiveDockable`,
  `FocusedDockable`.
- `avalonia\Dock\src\Dock.Model\Core\IFactory.cs:57,107,117,303,310` — `DockControls` registry,
  `DefaultHostWindowLocator`/`HostWindowLocator`, `SetActiveDockable`, `SetFocusedDockable`.
- `avalonia\Dock\src\Dock.Model\FactoryBase.Init.cs:284,329` — `SetFocusedDockable` sets
  `root.FocusedDockable`; `avalonia\Dock\src\Dock.Model\Controls\IRootDock.cs:18` — `IsFocusableRoot`.
- `avalonia\Dock\src\Dock.Model\Core\IDockable.cs:31,41` — `Owner`, `Factory`.
- `avalonia\Dock\src\Dock.Avalonia\Controls\HostWindow.axaml.cs:25` — `HostWindow : Window, IHostWindow`;
  `avalonia\Dock\src\Dock.Avalonia.Themes.Fluent\Controls\HostWindow.axaml` — template hosts an inner
  `DockControl`.
- `avalonia\Dock\src\Dock.Model.Inpc\Core\DockableBase.cs:13`,
  `avalonia\Dock\src\Dock.Model.Inpc\Controls\Document.cs:14` — dockables are `ReactiveBase` (INPC), not
  `AvaloniaObject`.
- `avalonia\Dock\src\Dock.Settings\DockProperties.cs` — attached-property precedent
  (`IsDockTarget`, `IsDragArea`, `DockGroup` inherits, …).

### Avalonia (12.1.0) source
- `Avalonia.Base\Input\KeyGesture.cs:20,60,158`; `Input\IKeyboardDevice.cs:11-14` (`KeyModifiers`);
  `Input\Key.cs:220-265` (`D0..D9`), `Input\Key.cs:500-555` (`F1..F12`); `Input\KeyBinding.cs`.
- `Avalonia.Controls\HotkeyManager.cs:9,12,123-132` — attached-property → `KeyBinding` pattern.
- `Avalonia.Base\Input\InputElement.cs:106` — `KeyDownEvent` `Tunnel|Bubble`;
  `Interactivity\RoutedEvent.cs` — routing strategies / `AddClassHandler`.
- `Avalonia.Base\Input\FocusManager.cs:65` (`GetFocusedElement`), `:133` (`GetFocusedElement(IFocusScope)`,
  `[PrivateApi]`, deliberately unused).
- `Avalonia.Base\AvaloniaProperty.cs:355-374` — `RegisterAttached(..., inherits)`;
  `Avalonia.Controls\ToolTip.cs:76,82` — `inherits:true` precedent.
- `Avalonia.Controls\Primitives\AdornerLayer.cs:68` (`SetAdornedElement`), `:73` (`GetAdornerLayer`),
  `:118` (`SetAdorner`) — adorner overlay.
- `Avalonia.Controls\ItemsControl.cs:218,227,246,713-717` — `PreparingContainer`/`ContainerPrepared`/
  `ContainerClearing`; `Presenters\ContentPresenter.cs`, `Styling\ControlTheme.cs`.
- `Avalonia.Themes.Fluent\Controls\TabStrip.xaml:27-31` — `TabStrip` default `WrapPanel` items panel
  (non-virtualizing).

### Avalonia.Xaml.Behaviors source (fetched for the behaviors-vs-attached-property decision, §8.5)
- Cloned to `c:\dev\avalonia\Avalonia.Xaml.Behaviors` (outside this repo). Latest release tag
  `11.1.0.x` — **no `12.x` release** matching Avalonia 12.1.0, and **not** referenced by
  Phantom.Workspaces.
- `src\Avalonia.Xaml.Interactivity\Behavior.cs:11,58,88` (`Behavior`, `OnAttached`,
  `OnAttachedToVisualTree`), `BehaviorOfT.cs:11,24` (`Behavior<T>`; `OnAttached` is
  `[RequiresUnreferencedCode]`), `Interaction.cs:23,152-168` (`Interaction.Behaviors` attached property +
  visual-tree attach/detach).

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

---

## 10. Packaging / Repository structure

All of this API's code lives in a **new, standalone git repository** published as the
`Phantom.Dock.Avalonia.TabSwitching` submodule. The repository is added as a **git submodule of
Phantom.Workspaces** and its project is included in the Phantom.Workspaces solution so it **compiles as
part of the Phantom.Workspaces build**. This keeps day-to-day development in-tree (edit, build, test the
submodule alongside the app) while preserving a clean split for eventual release.

### Independence for separate release

The submodule is intended for eventual **separate NuGet release**, so it must be self-contained:

- It depends **only** on Avalonia (12.1.0) and Dock.Avalonia (12.0.0.2) — never on any
  Phantom.Workspaces-specific type, view-model, or assembly.
- Everything reusable and generic — `DockTabSwitch` attached properties, `DockTabSwitchController`,
  `DockTabSwitchGestures`/`DockTabSwitchBindings`, `DockTabOrder`, `DockTabIndexContext`, the default
  badge `ControlTheme`/theme dictionary, and the `DocumentTabStripItem` composition theme — moves **into
  the submodule**.
- Phantom.Workspaces-specific wiring (replacing the current bespoke `Alt+N`/badge path, choosing gesture
  sets and scopes, opting specific strips out) **stays in the Phantom.Workspaces app** and merely
  **consumes** the submodule's public API. No product types leak into the package.

### Two-step commit workflow

Because the code lives in a submodule, any change spans two commits, in this order:

1. **Commit inside the submodule first.** Make and commit all `Phantom.Dock.Avalonia.TabSwitching`
   changes in the submodule's own repository, on its own branch/history.
2. **Bump the submodule reference in Phantom.Workspaces.** Then add a commit in the Phantom.Workspaces
   superproject that advances the recorded submodule pointer to that latest submodule commit
   (`git add <submodule-path>` in the superproject, then commit). Phantom.Workspaces always builds
   against a pinned submodule revision, so this second commit is what makes the new submodule code take
   effect in the app.

This ordering matters: the superproject reference must point at a commit that already exists in the
submodule, so the submodule commit must land before the superproject bump. The implementation plan's
first commit therefore scaffolds the submodule and wires it into the solution before any feature code is
written.
