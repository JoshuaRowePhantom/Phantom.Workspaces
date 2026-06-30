# Notifications

## Purpose

Provide a lightweight notification system that lets any running code (scheduled tools, agent
sessions, background services) surface a tab-linked reason for the user's attention. The UI
presents notifications as an overlay that fades automatically, remaining interactive if the
user hovers or explicitly opens the notification panel.

## Core data model

### `NotificationEntry`

```csharp
public sealed record NotificationEntry
{
    /// <summary>Unique per tab: workspaceId + tabId combined key.</summary>
    public required string TabKey { get; init; }

    /// <summary>
    /// Entity name or id path used to reopen the tab if it is closed.
    /// E.g. entity-name array for entity tabs, or a typed descriptor for other tab kinds.
    /// </summary>
    public required TabDescriptor TabDescriptor { get; init; }

    /// <summary>The notification text. Null means "clear this tab's notification".</summary>
    public required string? Reason { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>False until the user navigates to the tab or explicitly dismisses.</summary>
    public required bool IsRead { get; init; }

    /// <summary>
    /// When true, this tab's notifications are silently auto-read and never trigger the
    /// auto-show overlay. Snooze state is in-memory only and resets on restart.
    /// </summary>
    public required bool IsSnoozed { get; init; }
}
```

`TabDescriptor` carries enough information to navigate to (and if necessary reopen) the tab
— see the [Navigation Stack](navigation-stack.md) design for the reopening mechanism.

### `INotificationService`

```csharp
public interface INotificationService
{
    /// <summary>
    /// Post a notification for the specified tab. If <paramref name="reason"/> is null,
    /// the existing notification for that tab is cleared.
    /// A new notification for a tab that already has one replaces it and marks it unread,
    /// unless the tab is snoozed (in which case it is immediately marked read).
    /// </summary>
    void Notify(TabDescriptor tab, string? reason);

    IReadOnlyList<NotificationEntry> Notifications { get; }

    event EventHandler NotificationsChanged;
}
```

`INotificationService` is registered as a singleton and injected wherever running code needs
to post notifications (scheduled tools, agent sessions, shortcut handlers, etc.).

## Overlay behavior

### Auto-show on new notification

When a non-snoozed notification arrives:

1. The notification overlay panel becomes visible at full opacity.
2. After **1.5 seconds** (if the mouse has not entered the overlay), it begins a
   **1-second fade** to fully transparent.
3. While the mouse is over the overlay, the fade is paused and the panel stays opaque.
4. When the mouse leaves, the 1-second fade restarts from the current opacity.
5. Only the **most recent unread notification** is shown in the auto-show state. The
   overlay does not scroll through history during auto-show.

### Toggle button (explicit open)

A notification bell icon button is added to the window header (rightmost column, before the
settings gear). It shows a badge with the count of unread notifications when > 0.

Clicking the toggle button:
- Opens the overlay at full opacity indefinitely (does not auto-fade while open).
- A second click (or pressing Escape with the overlay focused) closes it.
- When open, all notifications are listed, newest first, with unread ones at the top.

### Overlay position and appearance

- The overlay is a `Popup` or absolutely positioned `Panel` anchored to the top-right of
  the main content area (below the header, aligned with the toggle button).
- Width: ~360 px; max-height: ~480 px with an internal `ScrollViewer`.
- Background: semi-transparent (e.g., 90% opaque dark surface) when fully "shown"; fades to
  0% opacity when auto-hiding.
- `IsHitTestVisible = false` when fully transparent so the underlying UI is not blocked.

## Notification list item layout

Each row:

```
┌────────────────────────────────────────────────────────┐
│  [tab icon]  Tab title · Reason text             [zzz] │
│              2 minutes ago                             │
└────────────────────────────────────────────────────────┘
```

- **Tab icon + title**: indicates which tab posted the notification.
- **Reason text**: the notification message.
- **Timestamp**: relative ("just now", "2 minutes ago").
- **Unread indicator**: a colored dot or bold styling on the tab title for unread entries.
- **zzz button**: clicking it snoozes the tab (see §Snooze below).
- Clicking anywhere on the row (except zzz) navigates to the tab and marks the notification
  read.

## Navigation via notifications

### Click to navigate

Clicking a notification row:

1. If the tab is open: activates its workspace and tab via `MainWindowViewModel.OpenTabAsync`.
2. If the tab is closed: reopens it using the same disposition mechanism as the navigation
   stack (see [navigation-stack.md](navigation-stack.md)).
3. Marks the notification `IsRead = true`.
4. Closes the overlay (returns to hidden state).

### F7 / F8 keyboard navigation

These shortcuts cycle through unread notifications without opening the overlay panel:

| Key | Action |
|---|---|
| `F8` | Navigate to the next (newer) unread notification |
| `F7` | Navigate to the previous (older) unread notification |

Both keys navigate to the associated tab (reopening if necessary) and mark the notification
read. If all notifications are read, F7/F8 cycle through all notifications (read or not) in
timestamp order.

`KeyBinding` entries for F7 and F8 are added to `MainWindow.axaml`; the corresponding
`RelayCommand`s (`NavigateNextNotificationCommand`, `NavigatePreviousNotificationCommand`)
are added to `MainWindowViewModel`.

## Snooze

Clicking the **zzz** icon on a notification entry:

1. Sets `IsSnoozed = true` for all future notifications from that tab.
2. Marks the current notification `IsRead = true`.
3. Future `Notify(tab, ...)` calls for that tab are silently accepted (stored in the list)
   but immediately marked read and never trigger the auto-show overlay.
4. A snoozed tab displays a small "zzz" badge on its notification list entry; clicking zzz
   again un-snoozes.

## Read / unread rules

| Event | Result |
|---|---|
| New `Notify(tab, reason)` — tab is **currently active** | Stored; `IsRead = true`; no auto-show; yellow border not shown |
| New `Notify(tab, reason)` — tab is **inactive**, unsnoozed | Previous entry replaced; `IsRead = false`; auto-show triggered; yellow border shown |
| New `Notify(tab, reason)` for snoozed tab | Previous entry replaced; `IsRead = true`; no auto-show |
| `Notify(tab, null)` | Entry for tab removed entirely; yellow border removed |
| Tab becomes active (user switches to it, F7/F8, notification click, back/forward nav) | Entry for that tab marked `IsRead = true`; yellow border removed |
| User clicks notification row | Entry marked `IsRead = true`; tab activated |
| User presses F7 / F8 | Cycled entry marked `IsRead = true`; tab activated |

Only the most recent notification per tab is retained. When a new notification arrives for a
tab that already has an entry, the old entry is replaced.

### `IActiveTabProvider`

`NotificationService` queries the currently-active tab to apply the focused-tab suppression
rule. It receives an `IActiveTabProvider` at construction:

```csharp
public interface IActiveTabProvider
{
    string? ActiveTabId { get; }
}
```

`MainWindowViewModel` implements this by reading `documentDock.ActiveDockable?.Id`.

### `INotificationService.MarkRead`

```csharp
void MarkRead(string tabId);
```

Called from every code path that activates a tab:
`MainWindowViewModel.OpenTabAsync`, `NavigateBackCommand`, `NavigateForwardCommand`, F7/F8
navigation. Marks the notification for that tab read and updates `HasUnreadNotification` on
the corresponding `WorkspaceDocument`.

## Notification indicator in tab strip

Tabs with an **unread** notification display a **yellow (`#FFD700`) border** around the
tab title text in the Dock tab item. The border disappears immediately when the
notification is marked read.

### `WorkspaceDocument.HasUnreadNotification`

`WorkspaceDocument` gains a new observable property:

```csharp
public bool HasUnreadNotification
{
    get => this.hasUnreadNotification;
    set => this.SetProperty(ref this.hasUnreadNotification, value);
}
```

`MainWindowViewModel` (or a `NotificationTabBridge` helper) subscribes to
`INotificationService.NotificationsChanged` and keeps `HasUnreadNotification` in sync for
each live `WorkspaceDocument`.

### Tab item template

The Dock tab item template wraps the title in a `Border` that is always present (no layout
shift); only its `BorderBrush` changes:

```xml
<Border BorderThickness="2"
        CornerRadius="3"
        Padding="2,0"
        BorderBrush="{Binding TabViewModel.HasUnreadNotification,
            Converter={StaticResource UnreadNotificationBorderConverter}}">
    <TextBlock Text="{Binding Title}" />
</Border>
```

`UnreadNotificationBorderConverter` returns `Gold` when `true`, `Transparent` when `false`.

## Source layout

In `Phantom.Workspaces`:

- `Services/Notifications/INotificationService.cs` (new) — includes `Notify`, `MarkRead`, `Notifications`, `NotificationsChanged`
- `Services/Notifications/IActiveTabProvider.cs` (new)
- `Services/Notifications/NotificationService.cs` (new) — in-memory, publishes `NotificationsChanged`; queries `IActiveTabProvider` for focused-tab suppression
- `Services/Notifications/NotificationEntry.cs` (new)
- `Services/Notifications/TabDescriptor.cs` (new)
- `ViewModels/NotificationsViewModel.cs` (new) — list of `NotificationRowViewModel`, toggle state, fade logic
- `ViewModels/NotificationRowViewModel.cs` (new) — per-entry row: title, reason, timestamp, isRead, isSnoozed, navigate command, snooze command
- `ViewModels/WorkspaceDocument.cs` — add `HasUnreadNotification` observable property
- `Controls/NotificationsOverlayControl.axaml` (new) — overlay panel with opacity animation
- `MainWindow.axaml` — add bell toggle button (Grid.Column 5) and `NotificationsOverlayControl`; add F7/F8 key bindings; update Dock tab item template for yellow border
- `ViewModels/MainWindowViewModel.cs` — implement `IActiveTabProvider`; add `NotificationsViewModel`, `NavigateNextNotificationCommand`, `NavigatePreviousNotificationCommand`; call `MarkRead` on every tab activation

## Non-goals

1. Persisting notification history across app restarts (notifications and snooze state are
   in-memory only and reset on restart).
2. System tray / OS-level notifications.
3. Sound or vibration feedback.
4. Notifications from sources other than in-process code (no push/webhook model in scope).
