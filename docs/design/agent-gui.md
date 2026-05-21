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

Working name: `ChatAgentOutputControl`.

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

### Input queue control

Working name: `AgentInputQueueControl`.

Responsibilities:

- show pending inputs and their ordering
- allow enqueueing new actions/prompts
- support interrupt / immediate / held queue behavior
- expose queue state clearly enough for debugging and authoring

This control should reflect the queue as a live work list, not just a text box.

### Combined agent workspace control

Working name: `AgentSessionControl`.

Responsibilities:

- compose the output control and input queue control
- bind them to the same `AgentChat`
- keep the user focused on the current session lifecycle
- provide a single workspace for authoring, running, and inspecting an agent

This is the main UI surface for the agent design tool.

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

- left/top: agent output and structured JSON
- right/bottom: input queue and queue controls
- optional inspector panel for templates and selected fragments

The layout should make it obvious that the GUI is both:

- a live agent runner
- an agent authoring tool

## Suggested implementation slices

1. Add the core `AgentChat` session model in `Phantom.Workspaces.Llm.Core`.
2. Add an output control for assistant / JSON rendering.
3. Add an input queue control.
4. Add a composite session control.
5. Add the `Phantom.Workspaces.Agent.Gui` app around those shared controls.
6. Add template extraction plumbing and storage for extracted fragments.
7. Add editing surfaces for MCP servers, trust profiles, extra agents, and skills.
8. Add editing surfaces for tools and AI context providers.

## Open questions

- Whether templates should be stored inline in the agent manifest or as separate entities first.
- How much of the JSON should be editable directly versus via typed sub-editors.
- Whether the output control should default to raw JSON, a tree view, or a split view.
