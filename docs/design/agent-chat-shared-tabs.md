# Shared agent-session tabs

## Goal

Allow the same agent chat session to be opened in multiple tabs, with each tab acting as a view over one shared live `AgentChat`.

## Today

`OpenAgentSessionShortcutHandler` creates a new `AgentChat` for a session open, and `MainWindowViewModel.OpenTabAsync` dedupes tabs by `WorkspaceTabViewModel.Id`. In practice that means an agent-session entity opens as one tab per workspace, not as multiple live views over the same session.

The current persistence path also lives behind per-window context objects, so separate windows can end up with separate caches even when they point at the same persisted `agent-session-id`.

## Desired behavior

1. Opening the same agent session multiple times should create multiple tab views.
2. All of those tabs should bind to the same running `AgentChat` instance for that session.
3. Closing one tab should detach only that view; the shared chat should stay alive until the last lease is released.
4. Shared session state should be process-wide, but not static.

## Core model

Split identity into two parts:

| Concept | Meaning |
|---|---|
| `agent-session-id` | The shared chat/session identity |
| tab instance id | The individual workspace tab/view identity |

`WorkspaceTabViewModel.Id` should identify the tab instance, not the shared session. `AgentSessionWorkspaceTabViewModel` should carry the shared session id separately so the UI can show multiple tabs for one session without deduping them into a single dock item.

## Application services object

Add an application-wide services object in `Phantom.Workspaces.Services` and pass one instance into `MainWindowViewModel` from `App`.

Suggested contents:

- `IRunningAgentChatTable RunningAgentChats`
- `IAgentPersistenceStoreCache` or equivalent shared store cache
- `IUpdateController? UpdateController`

This replaces the current pattern of reaching for app-wide state indirectly and gives tests a single place to inject process-level behavior.

## Running agent chat table

Add a process-wide registry for live chats, keyed by agent session id.

Responsibilities:

1. Create a chat on first open.
2. Return the existing chat on subsequent opens for the same session id.
3. Track reference counts / leases for each open view.
4. Dispose the chat when the last lease is released.
5. Serialize access so two tabs cannot create the same live chat at the same time.

Suggested shape:

```text
RunningAgentChatTable
  AcquireAsync(sessionKey, factory) -> RunningAgentChatLease
  GetOrCreateAsync(sessionKey, factory) -> shared entry
  Release(sessionKey, viewId)
```

Each lease should expose the shared `AgentChat` and a release path for tab disposal.

## Open flow

1. `OpenAgentSessionShortcutHandler` resolves the agent definition and the `agent-session-id`.
2. It asks the application services table for a shared chat entry.
3. The table returns an existing chat or creates one through the existing `AgentChat` creation path.
4. The handler creates a new `AgentSessionWorkspaceTabViewModel` for that shared chat.
5. The tab gets a unique tab id, so `OpenTabAsync` treats it as a new view rather than a duplicate.

## Lifetime / disposal

`AgentSessionWorkspaceTabViewModel.DisposeAsync` should release its lease rather than disposing the shared chat directly.

When the last tab for a session closes:

- the lease table disposes the shared `AgentChat`
- the live entry is removed from the process table
- persisted state remains in the configured store

## Services wiring

`App.OnFrameworkInitializationCompleted` should create the application services instance once and pass it to the main window view model.

`MainWindowViewModel` should accept the application services object and use it for:

- opening shared agent-session tabs
- resolving shared persistence services
- any future process-wide session plumbing

`AgentSessionShortcutContext` should stop owning its own per-window store cache and instead use the shared application services cache.

## Code changes

Likely touched classes / methods:

- `Phantom.Workspaces.App.OnFrameworkInitializationCompleted`
- `Phantom.Workspaces.ViewModels.MainWindowViewModel` constructor and session-open helpers
- `Phantom.Workspaces.ViewModels.OpenAgentSessionShortcutHandler.Handle`
- `Phantom.Workspaces.ViewModels.OpenAgentSessionShortcutHandler.CreateAgentSessionTab`
- `Phantom.Workspaces.ViewModels.AgentSessionWorkspaceTabViewModel.DisposeAsync`
- `Phantom.Workspaces.ViewModels.AgentSessionShortcutContext`
- new `Phantom.Workspaces.Services.ApplicationServices`
- new `Phantom.Workspaces.Services.RunningAgentChatTable`
- new `Phantom.Workspaces.Services.RunningAgentChatLease`

## Tests to add

1. **Running chat reuse**
   - Opening the same session twice returns the same shared `AgentChat`.
   - The chat is not disposed until the last lease is released.

2. **Multi-tab open behavior**
   - Opening the same agent session twice creates two tabs with different tab ids.
   - Both tabs point at the same shared session id / shared `AgentChat`.

3. **Tab lifetime**
   - Closing one tab leaves the shared session alive for the other tab.

4. **Shared persistence cache**
   - The application services cache returns the same persistence store for the same process scope.

5. **Regression coverage for interference**
   - A message sent from one tab is visible in the other tab because both are bound to the same live chat.

## Non-goals

- Do not use static globals for the table or the services object.
- Do not merge unrelated workspace tabs into the shared-session behavior.
- Do not make per-tab UI state global; only the live agent session should be shared.
