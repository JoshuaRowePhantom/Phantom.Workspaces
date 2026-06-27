# Entity click shortcut handler

> **Status: implemented.** Adds a click-activated shortcut handler that opens certain entity types
> when their card is clicked, without showing a shortcut button. Tracks todo
> `entity-click-shortcut-handler`.

## Problem & scenario

Entities are rendered as cards in the GUI with a row of **shortcut buttons** (Open `↗`, Json `{}`,
Delete `🗑`). Some entity types should also open on a plain **click** of the card — for example,
clicking a `workspace` card should open that workspace — but we do **not** want this click behavior to
add yet another visible button, and we want it scoped to specific entity types.

## Existing shortcut architecture

- `ShortcutHandler` (abstract, `Phantom.Workspaces/ViewModels/ShortcutHandler.cs`) has
  `ShouldApplyTo(mvm, shortcut, entity)` and `Handle(mvm, shortcut, entity)`.
- `ShortcutManager` holds the registered handlers. `GetShortcutsFor(...)` returns, from the fixed set
  `[Open, Json, Delete]`, the shortcuts for which **some registered handler** applies — this is what
  drives **which buttons are shown**. `HandleShortcutAsync(mvm, shortcut, entity)` runs the first
  registered handler that applies.
- `OpenEntityShortcutHandler` applies to `Shortcut.Open` and, in `Handle`, opens a `workspace` via
  `MainWindowViewModel.OpenWorkspaceAsync` (or any other entity via `OpenEntityTabAsync`).
- `EntityShortcutViewModel.HandleAsync` calls `ShortcutManager.HandleShortcutAsync`; buttons are bound
  to `MainWindowViewModel.ActivateShortcutCommand` (`entityCardNode.SetShortcuts(...)`).

**Key insight:** a button only appears for a handler if that handler is **registered in the
`ShortcutManager`** and applies to one of the buttoned shortcuts. A handler that is *not* registered
in the manager produces **no button**, yet can still be invoked directly.

## Design

Add `EntityClickShortcutHandler : ShortcutHandler` in `Phantom.Workspaces/ViewModels/`:

- It is constructed with the **set of entity types** that should open on click (initially
  `["workspace"]`) and a reference to the `ShortcutManager` (so it can delegate to the real Open
  handler).
- `ShouldApplyTo(mvm, shortcut, entity)` returns `true` only when the entity is one of the configured
  types (it ignores `shortcut`, since it is invoked directly on click, not via the button set).
- `Handle(mvm, shortcut, entity)`: for a configured entity type, invokes the **Open** shortcut through
  the shortcut-handling command — `mvm`/`ShortcutManager.HandleShortcutAsync(mvm, Shortcut.Open,
  entity)` — so the existing `OpenEntityShortcutHandler` performs the actual open (no duplicated open
  logic). Returns `true` when it handled the click; `false` (or no-op) for non-configured types.

**It is deliberately NOT added to the `ShortcutManager`.** Because `GetShortcutsFor` only consults
registered handlers, leaving this handler unregistered guarantees it contributes **no button**. It is
instead held directly by the view layer and invoked from the click wiring.

### GUI wiring

The entity card view binds a **`Tapped`** event on the card `Border` to `OnEntityCardTapped` in
`EntityCardControl`, which calls `ActivateEntityClickCommand`. The existing shortcut buttons remain
unchanged and continue to route through `ActivateShortcutCommand`.

**No double-trigger guarantee:** every interactive child control (all `Button` elements and the raw
JSON `TextBox`) inside the card AXAML carries a `Tapped="OnInteractiveChildTapped"` handler that
immediately sets `e.Handled = true`. Because `OnEntityCardTapped` on the `Border` is registered with
the Avalonia default `handledEventsToo: false`, it is silently skipped whenever a child has already
claimed the event. An explicit `if (e.Handled) return;` guard at the top of `OnEntityCardTapped`
provides defence-in-depth for any call path that bypasses the routing (e.g. direct invocation in
tests). This replaces the former fragile visual-tree walk over a hard-coded type list
(`IsInteractiveSource`) — see issue #85. Styling stays in centralized shared styles per the styling
convention.

### Why delegate to Open rather than open directly

Delegating to `Shortcut.Open` via the manager keeps a single source of truth for "what opening an
entity means" (`OpenEntityShortcutHandler`), so future open behavior (e.g. new openable types) is
inherited by click automatically. The click handler only decides **which types are click-openable**.

## Test tasks

- **Click opens a workspace:** invoking `EntityClickShortcutHandler.Handle` for a `workspace` entity
  calls through to the Open behavior (`OpenWorkspaceAsync`), via a `ShortcutManager` containing the
  real `OpenEntityShortcutHandler`. Deterministic, synchronous `dispatch`, no `Task.Delay`.
- **Non-configured type is ignored:** invoking it for a non-`workspace` entity does nothing and
  returns `false`.
- **No button is produced:** with the click handler unregistered, `ShortcutManager.GetShortcutsFor`
  for a `workspace` returns the same buttons as before (no extra/duplicate Open from the click
  handler), confirming the click handler contributes no button.
- **No double-trigger:** tapping a shortcut button sets `e.Handled = true` via `OnInteractiveChildTapped`
  before the event reaches the `Border`'s card handler; the card handler is therefore silently skipped.
  Verified by `EntityCardControl_WhenTappedEventAlreadyHandled_DoesNotOpenEntity`.

## Implementation (completed)

1. `EntityClickShortcutHandler` (configured types + delegates to `Shortcut.Open` via the manager).
2. `MainWindowViewModel.ActivateEntityClickCommand` invoking the handler for the clicked entity.
3. `Tapped="OnEntityCardTapped"` on the card `Border`; interactive child controls carry
   `Tapped="OnInteractiveChildTapped"` to mark the event handled before it can reach the card handler
   (issue #85 — replaces the former `IsInteractiveSource` visual-tree walk).
4. Tests above.

## Open questions

1. **Configured types:** start with `workspace` only; should other types (e.g. `agent-session`,
   `view`) be click-openable too, or is that per-type opt-in added later?
2. **Single vs double click:** single click to open (proposed) vs double click, given selection
   semantics in the list.
