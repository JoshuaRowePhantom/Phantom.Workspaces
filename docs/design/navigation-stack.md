# Navigation Stack

## Purpose

Maintain a navigable history of tabs and views the user has visited so that
**Ctrl+−** (go back) and **Ctrl+Shift+−** (go forward) move through the history the same
way a browser's back/forward buttons do. Any navigation that activates a tab or top-level
view adds an entry to the stack.

## Keyboard shortcuts

| Gesture | Action |
|---|---|
| `Ctrl+−` (Ctrl and the minus/hyphen key) | Navigate backward in history |
| `Ctrl+Shift+−` (Ctrl+Shift+Minus, i.e. Ctrl+_) | Navigate forward in history |

These are analogous to **Alt+Left / Alt+Right** in web browsers and
**Ctrl+− / Ctrl+Shift+−** in Visual Studio.

## Navigation entry

```csharp
public sealed record NavigationEntry
{
    /// <summary>Identifies the workspace that owns the tab, or null for top-level views.</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>
    /// Stable tab identifier matching WorkspaceTabViewModel.Id.
    /// Null for top-level view entries.
    /// </summary>
    public string? TabId { get; init; }

    /// <summary>
    /// For top-level view navigation (entity browser left-pane selections).
    /// Null for tab entries.
    /// </summary>
    public string? TopLevelViewId { get; init; }

    /// <summary>
    /// Descriptor used to reopen a closed tab. Serialisable so it survives tab disposal.
    /// </summary>
    public TabDescriptor? TabDescriptor { get; init; }
}
```

`TabDescriptor` (shared with the Notifications design) carries enough information to
reconstruct the tab: entity name/id for entity tabs, agent-session entity reference for
agent tabs, URL for browser tabs, etc. Each `WorkspaceTabViewModel` subclass is responsible
for producing its own `TabDescriptor`.

## Stack semantics

The history is a **linear list with a current-position pointer** — identical to browser
history:

```
[A] [B] [C*] [D] [E]
             ↑ current
Ctrl+−  → go to B → [A] [B*] [C] [D] [E]
Ctrl+−  → go to A → [A*] [B] [C] [D] [E]
Ctrl+Shift+− → go to B → [A] [B*] [C] [D] [E]
Navigate to F → truncate forward, push F → [A] [B] [F*]
```

Rules:

1. **Push on activation**: any activation of a tab or top-level view (by the user or by
   code) appends a new entry after the current position and truncates any forward history,
   **unless** the activation was itself triggered by a back/forward navigation.
2. **Deduplication**: if the entry immediately before the current position is identical (same
   workspace + tab), the push is a no-op to avoid double-entries from programmatic
   activate-then-focus sequences.
3. **Max depth**: the stack is capped at **200 entries** (oldest entries dropped when
   exceeded).
4. **Closed tabs**: if the tab pointed to by an entry no longer exists, navigating to it
   reopens it using `TabDescriptor` and the disposition from the original open (see
   §Reopening closed tabs).

## `INavigationHistoryService`

```csharp
public interface INavigationHistoryService
{
    /// <summary>Record that the user/code navigated to this entry (user-initiated push).</summary>
    void Push(NavigationEntry entry);

    /// <summary>Navigate backward. Returns false if already at the beginning.</summary>
    bool GoBack(out NavigationEntry? entry);

    /// <summary>Navigate forward. Returns false if already at the end.</summary>
    bool GoForward(out NavigationEntry? entry);

    bool CanGoBack { get; }
    bool CanGoForward { get; }

    event EventHandler CanNavigateChanged;
}
```

`NavigationHistoryService` is a singleton registered at app startup and injected into
`MainWindowViewModel`. It has no persistence — the stack is lost on app restart.

## Sources of push

Every code path that activates a tab or top-level view must call `Push`:

| Source | When to push |
|---|---|
| `MainWindowViewModel.OpenTabAsync` | After `SetActiveDockable` |
| `MainWindowViewModel.SetSelectedTopLevelView` | When the left-pane view changes |
| Notification click navigation | After activating the tab |
| F7 / F8 notification key navigation | After activating the tab |
| AI tools that open entities (see §AI tool integration) | After the tool activates the tab |

Back/forward navigation (`GoBack` / `GoForward`) must **not** push during the activation
they trigger, or every back-navigation would erase the forward history. A boolean
`_navigatingViaHistory` guard in `MainWindowViewModel` suppresses the push while history
navigation is in progress.

## Reopening closed tabs

When `GoBack` or `GoForward` resolves an entry whose tab no longer exists:

1. Call `TabDescriptor.OpenAsync(mainWindowViewModel)` — a virtual method on `TabDescriptor`
   that reproduces the original open sequence (e.g., `OpenEntityTabAsync`, `OpenAgentTab`).
2. The new tab is activated but a push is **not** emitted (the history position itself is
   the navigation target).
3. If reopening fails (e.g., the entity was deleted), the entry is skipped and the next
   valid entry in the same direction is tried.

Each `TabDescriptor` subclass:

| Tab kind | Descriptor fields | Reopen action |
|---|---|---|
| Entity tab | `EntityId`, `EntityName[]` | `OpenEntityTabAsync` |
| Agent-session tab | `AgentSessionEntityId` | `OpenAgentSessionTab` |
| Browser tab | `Url` | `OpenBrowserTab` |
| Entity-browser tab | `ViewId` | Activate the named top-level view |

## AI tool integration

AI tools that navigate the workspace (e.g., `entity_invoke_shortcut` opening an entity)
must call `INavigationHistoryService.Push` after the tab is activated. This is tracked as a
separate enhancement (see related issues) so it can be addressed once the tool-invocation
pipeline has a clean injection point for the service.

## `MainWindowViewModel` changes

```csharp
// New commands
public ICommand NavigateBackCommand { get; }   // Ctrl+−
public ICommand NavigateForwardCommand { get; } // Ctrl+Shift+−

// New properties (for optional UI affordance)
public bool CanNavigateBack  => this.historyService.CanGoBack;
public bool CanNavigateForward => this.historyService.CanGoForward;
```

`NavigateBackCommand` calls `this.historyService.GoBack(out var entry)`, then activates the
entry's workspace + tab (or top-level view) with `_navigatingViaHistory = true` to suppress
the push.

## `MainWindow.axaml` changes

```xml
<KeyBinding Gesture="Ctrl+OemMinus" Command="{Binding NavigateBackCommand}" />
<KeyBinding Gesture="Ctrl+Shift+OemMinus" Command="{Binding NavigateForwardCommand}" />
```

(`OemMinus` is Avalonia's key name for the `-`/`_` key.)

## Source layout

In `Phantom.Workspaces`:

- `Services/Navigation/INavigationHistoryService.cs` (new)
- `Services/Navigation/NavigationHistoryService.cs` (new)
- `Services/Navigation/NavigationEntry.cs` (new)
- `Services/Navigation/TabDescriptor.cs` (new, shared with notifications) — abstract base; subclasses per tab kind
- `Services/Navigation/EntityTabDescriptor.cs` (new)
- `Services/Navigation/AgentSessionTabDescriptor.cs` (new)
- `Services/Navigation/BrowserTabDescriptor.cs` (new)
- `ViewModels/MainWindowViewModel.cs` — inject `INavigationHistoryService`; add `Push` calls; add back/forward commands; add `_navigatingViaHistory` guard
- `MainWindow.axaml` — add Ctrl+− / Ctrl+Shift+− key bindings

## Non-goals

1. Persisting the navigation stack across app restarts.
2. Branching history (the stack is always linear).
3. Visual back/forward buttons in the UI chrome (keyboard shortcuts only in this iteration).
4. AI tool push integration in this feature — tracked separately.
