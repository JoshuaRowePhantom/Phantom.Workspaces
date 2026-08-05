# Agent GUI design

## Goal

Build an interactive GUI for designing and running agents from a bare-bones default configuration. The GUI should treat the agent definition as JSON-first output and expose the major agent layers directly:

- MCP servers
- trust profiles
- extra agents
- skills

The GUI should also support extracting reusable templates from that JSON output.
The first concrete app will be `Phantom.Workspaces.Agent.Gui`, a single-agent GUI user that accepts either an agent JSON document or an agent template JSON document and runs that agent.

## Design principles

1. **JSON is the source of truth**
   - The GUI edits and displays a normalized JSON agent description.
   - Typed views are projections of that JSON, not separate state.

2. **Start minimal**
   - The default configuration should be the smallest valid agent.
   - Users add capability deliberately: servers, trust, tools, helper agents, skills.

3. **Separate execution from presentation**
   - LLM execution lives in `Phantom.Workspaces.Llm.Core`.
   - Avalonia controls only render state and collect user actions.

4. **Template extraction is a first-class workflow**
   - Any stable JSON subtree should be promotable into a template.
   - Templates should be reusable across agent configs and sessions.

## Runtime model

### Agent definition

Use one canonical JSON document to represent the agent configuration and generated structure. The document should carry:

- basic agent identity and metadata
- MCP server declarations
- editable tool definitions
- editable AI context provider definitions
- trust profile references or inline trust data
- extra agent definitions
- skill definitions
- template metadata for reusable fragments

This lets the GUI show both the current concrete agent and any reusable pieces derived from it.

### Llm.Core runtime model

Add a new `Llm.Core` model for a single conversation session.

Working name: `AgentChat`.

Responsibilities:

- model the ordered, numbered history set as `History`
- model the current running item set, including the active chat stream, as `RunningItems`
- model the items waiting for approval as `PendingApprovalItems`
- model the queue set as a `ChatQueue` property
- model editable tool definitions
- model editable AI context provider definitions
- own or reference the active `AgentSession`
- own or reference the `AgentInputQueue`
- publish queued user actions into the session
- expose enough history to hydrate prior turns and conversation context
- manage interruptions, turn boundaries, and session lifetime

This object should sit above the raw queue and below the Avalonia view layer.

`AgentInputQueue` should raise a change event so the UI can bind to queue updates without polling.

Public collection-like properties on `AgentChat` should use concrete, bindable types rather than interface-only abstractions.
Each of `ChatQueue`, `History`, `RunningItems`, and `PendingApprovalItems` should be a concrete model or observable collection type that the UI can subscribe to.

Recommended property names and concrete types:

- `ChatQueue` : `AgentChatQueue`
- `History` : `AgentChatHistory` (virtualization-aware)
- `RunningItems` : `AgentChatRunningItems`
- `PendingApprovalItems` : `AgentChatPendingApprovalItems`

`AgentChatHistory` should support virtualization so the UI can render large histories without materializing every item at once.

Editable tool and AI context provider definitions should be represented as concrete model collections under `AgentChat` so they can be edited, validated, and persisted alongside the rest of the agent configuration.

## Avalonia controls

### Chat agent output control

Working name: `AgentChatOutputControl`.

Responsibilities:

- render the streaming conversation/output area as event history
- show assistant text, tool calls, tool results, and status
- render structured JSON facets when available
- surface template extraction affordances for selected output regions
- edit tool definitions
- edit AI context provider definitions

The control should support two views of the same data:

- a human-readable conversational stream
- a structured JSON/tree view of the current agent state

It should render only the events that have occurred, including the currently active turn while it is still streaming, and be able to rehydrate prior turns from stored history.

#### Two-zone layout

The control is split into two vertical zones stacked from top to bottom:

1. **History zone** — completed turns, virtualized, scroll-anchored at the bottom.
2. **Active items zone** — currently running items, non-virtualized, always visible below the history zone.

The active items zone is never virtualized because there are at most a handful of items running at any time.

#### Rendering primitive: browser-hosted HTML output

Both zones are rendered inside a single browser-hosted surface — a native WebView (`ControllableWebViewControl` in `Phantom.Workspaces.Gui.Styles`) that loads a static HTML shell (`chat-output-shell.html`) and is driven through a bidirectional JavaScript bridge. This replaced the earlier `FlowDocument`/`SelectableTextBlock` Avalonia renderers, which did not scale to long conversations. The browser surface gives:

- mouse-drag text selection and keyboard copy, including cross-message selection (a native limitation of the previous per-item `SelectableTextBlock` model)
- rich inline formatting and layout via ordinary HTML/CSS, themed through CSS custom properties injected by the host
- streaming incremental updates by mutating only the changed DOM elements rather than rebuilding the view

The host never manipulates the DOM directly. A testable, push-based model (`ChatOutputHtmlModel`) computes the minimal set of operations — replace, insert before/after, append, remove — and emits them through `IChatOutputHtmlSink`. The renderer control (`AgentChatOutputControl`) serializes each operation with `ChatOutputBrowserCommands` (JSON) and posts it across the bridge, where the shell's `applyCommand` handler applies it. Element ids are assigned by `ChatOutputHtmlRenderer` so updates target stable nodes.

The shell exposes three persistent regions that map onto the zones: `#chat-history-container` (completed turns), `#running-items-container` (active items), and `#subagent-panel-sentinel` (a permanent wrapper whose only child, `#subagent-panel-inner`, is rebuilt whenever running sub-agent state changes). The persistent regions are never replaced or removed — history chunks are `prepend`-ed into the history container, live messages `append`/`after` relative to existing top-level elements, and the sub-agent panel is updated by removing `#subagent-panel-inner` and appending a fresh inner node into the sentinel. There are no first-item anchor elements; an empty container is the base case. The page posts `ready`/`scrollState` messages back to the host so it can flush queued commands once the shell has loaded and own the auto-scroll policy.

> **Testing note:** the model and command layers are covered by ordinary headless unit tests; real-browser behavior is covered by the `Phantom.Workspaces.Agent.Gui.WebViewTests` project (Trait `Category=WebView`), which hosts a real native WebView on an STA thread and is excluded from default runs (use `.\scripts\run-tests.ps1 -IncludeWebView`).

#### Multi-level sticky headers

The shell includes a `StickyScrollEngine` that mirrors the Avalonia `StickyScroll`/`StickyItem`/`StickyLayoutSelector` system for the browser surface. The following data attributes drive it:

| HTML attribute | Meaning |
|---|---|
| `data-sticky-level="N"` | This element is sticky at declared level N. Its effective level is N plus the sum of all ancestor `data-sticky-base-level` values up to the scroll root. |
| `data-sticky-base-level="N"` | This container adds N to the effective level of all sticky descendants. |

Conventions used by `ChatOutputHtmlRenderer`:

- **Message headers** (`.chat-header`): `data-sticky-level="0"`. The containing `.chat-message` div carries `data-sticky-base-level="0"` as a neutral scroll-root boundary marker.
- **Tool-call/result collapsibles** (`<details>`): `data-sticky-base-level="1"` on the `<details>` element; `data-sticky-level="0"` on the `<summary>` element. This makes the summary pin at the top while the user scrolls through an expanded body.

The engine attaches to `document.body` as the scroll root and calls `update()` on `scroll` (passive listener), `ResizeObserver`, and `MutationObserver` (covers `<details>` open/close and streamed DOM mutations). The core pin algorithm (`ComputeAxisPins`) matches `StickyLayoutSelector.ComputeAxisPins`: items are processed in ascending effective-level order; each pinned item advances the accumulated top offset so higher-level items push lower-level ones upward.

#### History zone rendering

- Rendered into the `#chat-history-container` region as completed turns are appended.
- Scroll is anchored to the bottom; new entries cause the view to scroll down unless the user has scrolled up (tracked via the `scrollState` messages the page posts to the host).

#### Active items zone rendering

Each active item renders two things side by side:

1. **Animation indicator** — an in-progress indicator (CSS-driven) shown while the item is running. It changes state when the item completes or errors.
2. **State text** showing the item's current state:
   - For a streaming chat or thinking response: text is appended incrementally as it arrives from the model, producing a live word-by-word display.
   - For a tool call: shows "calling *tool name*…", then the result or error when finished.
   - For a sub-agent invocation: shows the sub-agent name and its current status.

When an active item completes, it transitions from the `#running-items-container` region into `#chat-history-container` (moved to the bottom of the history list and the active item row is removed).

### Input queue control

Working name: `AgentChatInputQueueControl`.

Responsibilities:

- provide a text input box for composing messages
- enqueue composed text into the appropriate queue based on the keyboard gesture used
- display all active queues in their injection order
- allow per-queue management: hold, clear items, remove queue, reprioritize

This control should reflect the queue as a live work list, not just a text box.

#### Text input and keyboard gestures

A single text box sits at the bottom of the control. The text box operates in one of two modes.

**Normal mode** (default):

| Gesture | Action |
|---|---|
| **Enter** | Append the composed text to the **default input queue** (the immediate/first queue). |
| **Ctrl+Enter** | Append the composed text to the **default input queue** (same as Enter). |
| **Ctrl+Q** | Append the composed text to the **most recently created queue**. |
| **Ctrl+Shift+Q** | **Create a new queue in the Held (paused) state** and append the composed text to it. The staged message is not dispatched until the queue is released, so the user can configure or reorder it first. The new queue becomes the most recently created queue for subsequent Ctrl+Q presses. |
| **Shift+Enter** | Switch to **formatted mode** without enqueuing. The text box expands to show multiple lines. |

After any enqueue gesture the text box is cleared and focus returns to it.

**Formatted mode** (entered via Shift+Enter):

In formatted mode the text box accepts multi-line input. The enqueue gestures change:

| Gesture | Action |
|---|---|
| **Enter** | Insert a newline at the cursor. |
| **Ctrl+Enter** | Enqueue the composed text into the **default input queue** and return to normal mode. |
| **Ctrl+Q** | Enqueue the composed text into the **most recently created queue** and return to normal mode. |
| **Ctrl+Shift+Q** | **Create a new queue in the Held (paused) state**, enqueue the composed text into it, and return to normal mode. The staged message is not dispatched until the queue is released. |
| **Esc** | Return to normal mode without enqueuing. The composed text is preserved in the text box. |

The text box should show a visible indicator (e.g., a label or border change) when in formatted mode so the user knows Enter will not immediately submit.

#### Queue list layout

All active queues are shown stacked in the order their items will be injected into the agent session (highest priority at the top). Each queue row contains:

1. **Queue label** — a name or auto-generated index identifying the queue.
2. **Items list** — the pending messages in that queue, shown in submission order.
3. **Per-item controls** — next to each item: a remove (×) button to delete that individual item.
4. **Queue action buttons** — on each queue row header:
   - **Hold** — toggles the held state; a held queue's items are not sent until released.
   - **Clear** — removes all items from the queue without deleting the queue itself.
   - **Remove queue** — deletes the queue and all its items entirely (not available on the default queue).
   - **↑ Top** — reprioritizes this queue above all others.
   - **↓ Bottom** — reprioritizes this queue below all others (but above the default queue).

The default queue cannot be removed or repositioned below the user-created queues; it is always the lowest-priority named queue.

### Combined agent workspace control

Working name: `AgentSessionControl`.

Responsibilities:

- compose the output control and input queue control
- bind them to the same `AgentChat`
- keep the user focused on the current session lifecycle
- provide a single workspace for authoring, running, and inspecting an agent

This is the main UI surface for the agent design tool.

### Agent definition editor control

Working name: `AgentDefinitionEditorControl`.

Responsibilities:

- display and edit an `AgentDefinition` as JSON text
- expose a `DefinitionChanged` event so host controls can react to edits
- validate JSON on change and surface parse errors inline

The editor is JSON-first: typed sub-editors for specific fields (model options, tools, instructions) are projections of the same underlying JSON and will be added incrementally.

### Agent control

Working name: `AgentControl`.

Responsibilities:

- accept an `AgentDefinition` as its primary input
- embed an `AgentDefinitionEditorControl` showing the current definition
- create and start an `AgentChat` session from the definition on load
- for prompt and chat agents: compose the output control, input queue control, and active items view
- subscribe to `AgentDefinitionEditorControl.DefinitionChanged`: stop the current session, recreate it from the updated definition, and restart

When the GUI is opened with an agent definition document:

1. The `AgentControl` receives the definition.
2. It populates the embedded `AgentDefinitionEditorControl` with that definition.
3. It starts the `AgentChat` session immediately.
4. Edits made in the `AgentDefinitionEditorControl` propagate back to the `AgentControl`, which restarts the session with the new definition.

### Runtime agent browser control

Working name: `AgentRuntimeBrowserControl`.

Responsibilities:

- display the live runtime entities created when an agent runs as a navigable tree
- show the agent itself as the top-level node
- expand into sub-levels for runtime-realized entities such as:
  - tools registered and available to the agent
  - sub-agents spawned or referenced at runtime
  - MCP servers that are currently connected
  - active sessions and their state
- reflect the live state of the runtime: nodes appear and disappear as entities are created and destroyed
- allow selection of a node to inspect its details in a companion panel

The browser is a read-only runtime view, not an editor. It is the complement to `AgentDefinitionEditorControl`: the editor shows what was configured, the browser shows what actually exists at runtime.

The runtime model backing this control should be a bindable tree rooted at an `AgentRuntimeNode`. Each node carries:

- a display name and kind label (agent, tool, sub-agent, server, session, …)
- a reference to the underlying runtime object where available
- an observable children collection so the tree refreshes without full rebuilds

## GUI project structure

### `Phantom.Workspaces.Agent.Gui`

This is the first dedicated GUI app for agent authoring and execution.

Responsibilities:

- load a single agent JSON document or agent template JSON document
- run that agent interactively
- host the agent output control, input queue control, and combined session control
- expose template extraction and reuse actions

### `Phantom.Workspaces.Agent.Gui.Test`

This is the dedicated test project for the agent GUI app and its reusable controls.

Responsibilities:

- test control behavior and composition
- test session binding integration
- test JSON/template loading behavior
- keep GUI-specific coverage separate from the core LLM and existing desktop app tests

### `Phantom.Workspaces`

The existing desktop app will later reference the same agent GUI controls instead of reimplementing them.

That implies the agent GUI controls should be organized as reusable UI building blocks, not app-specific one-offs.

## Template extraction

Template extraction should operate on normalized JSON output.

### What can become a template

- a single MCP server definition
- a trust profile fragment
- an extra agent definition
- a skill definition
- a repeated subtree inside the full agent manifest

Example template composition:

- one template for working on a `.git` repository with docker-isolated file operations and build tools
- one separate docker-isolated toolset template for git operations
- one rights template for searching specific websites

### Extraction flow

1. User selects a subtree in the output view.
2. The GUI normalizes the selection into a template candidate.
3. The system computes:
   - stable identity
   - parameter placeholders
   - dependencies on other agent fragments
4. The extracted template is stored as its own reusable artifact.
5. The original agent JSON can reference that template instead of inlining it.

### Template goals

- reduce repetition
- keep the main agent definition readable
- make repeated server / trust / skill patterns easy to reuse
- preserve a path back to the concrete JSON that produced the template

## UI layout

The first version should stay simple:

- left panel: `AgentRuntimeBrowserControl` (runtime tree)
- center: `AgentControl` (definition editor + output + input queue)
- optional right/bottom inspector panel for templates and selected fragments

The layout should make it obvious that the GUI is both:

- a live agent runner
- an agent authoring tool

## Suggested implementation slices

1. Add the core `AgentChat` session model in `Phantom.Workspaces.Llm.Core`.
2. Add the `AgentRuntimeNode` bindable tree model in `Phantom.Workspaces.Llm.Core`.
3. Add `AgentDefinitionEditorControl` (JSON text editor + `DefinitionChanged` event).
4. Add an output control for assistant / JSON rendering.
5. Add an input queue control.
6. Add `AgentRuntimeBrowserControl` (tree view of live runtime entities).
7. Add `AgentControl` composing the definition editor, output, input queue, and runtime browser.
8. Add the `Phantom.Workspaces.Agent.Gui` app around those shared controls.
9. Add template extraction plumbing and storage for extracted fragments.
10. Add editing surfaces for MCP servers, trust profiles, extra agents, and skills.
11. Add editing surfaces for tools and AI context providers.

## Open questions

- Whether templates should be stored inline in the agent manifest or as separate entities first.
- How much of the JSON should be editable directly versus via typed sub-editors.
- Whether the output control should default to raw JSON, a tree view, or a split view.
- How `AgentRuntimeNode` gets notified when the agent framework creates or destroys runtime entities (polling vs. events vs. framework hooks).
- Whether restarting the session on definition change should preserve history or start fresh.
