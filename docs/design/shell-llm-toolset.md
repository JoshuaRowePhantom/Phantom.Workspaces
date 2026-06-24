# Shell LLM toolset

> Designs the agent-facing toolset for controlling shells (arbitrary processes) on one or more
> hosts. Builds on the shell stream transport, PTY/pipe modes, and terminal control in
> `docs/design/shell-pty-terminal.md`. LLM-facing tools are `AIFunction`s exposed through an
> `AIContextProvider` toolset created by an `IToolsetFactory`, like the workspace-entity and
> session-context toolsets.

## Purpose

Let an agent **start, write to, read from, and stop** shells, and crucially **choose the host**
per shell — so a single agent can drive shells on several Phantom.Workspaces hosts at once (the
local machine, forward-connected instances, reverse-tunnel-connected instances). The same live
shells are viewable by a human in the agent editor (`shell-pty-terminal.md`, "agent editor
shells").

## Background: toolset wiring

- A tool is an `AIFunction` (snake_case `Name`, `Description`, `JsonSchema`, `InvokeCoreAsync`).
- Tools are grouped in an `AIContextProvider` with a unique per-instance `StateKeys`.
- An `IToolsetFactory` registered for a **kind** (`ToolsetFactory.CreateNamedToolsetFactory`)
  builds the provider; an agent-definition opts in by listing that tool resource. The factory is
  created by the **host**, closed over the services it needs (as in `session-context-tools.md`).

## Tools

All tools are namespaced `shell_*` and operate over the generic stream transport
(`ITrustedExecutor.OpenStreamAsync`, `StreamKind == "shell"`).

### `shell_start`

Start a process on a host and return a session id.

- **Arguments:** `host` (the `targetClientInstance`: `"."`, a forward id, or a reverse id),
  `mode` (`pty`|`pipe`, default `pipe` for agents), `command`, `command_arguments` (array),
  `working_directory` (optional), `environment` (optional object), `columns`/`rows` (optional,
  pty only).
- **Behavior:** resolves the executor via `ITrustedExecutorSelector` for `host`, checks the
  trust profile permits `shell` streams on that host, calls `OpenStreamAsync`, registers the
  live session, and returns `{ "shell_id": "<id>", "host": "<host>" }`.
- **Default mode `pipe`:** agents usually want captured output, not a live terminal; `pty` is
  available for interactive programs.

### `shell_write`

Send input to a running shell.

- **Arguments:** `shell_id`, `data` (string; sent as `input` bytes). Optional `append_newline`
  (default true) so `command` lines execute.
- **Behavior:** writes `data`'s bytes to the session's terminal `Stream`.

### `shell_read`

Read accumulated output since the last read.

- **Arguments:** `shell_id`, optional `max_bytes`.
- **Behavior:** returns `{ "data": "<text>", "exited": <bool>, "exit_code": <int|null>,
  "truncated": <bool> }`. The toolset reads the session's terminal `Stream` into a per-session
  output buffer; `shell_read` drains the buffer (up to `max_bytes`). Because LLM tools are
  request/response, reading is **pull** over the buffered live stream (the terminal control reads
  the same `Stream`/buffer for the human to watch in real time). In `pipe` mode the data is raw
  text; in `pty` mode it is the VT byte stream (the tool may optionally strip control sequences —
  see open questions).

### `shell_stop`

Stop a shell.

- **Arguments:** `shell_id`, optional `signal` (default terminate).
- **Behavior:** calls `ITerminalSession.SignalAsync` (or disposes the session), removes the
  session from the registry, returns the final exit code if known.

### `shell_list`

List the agent's running shells (so the agent can recover ids).

- **Arguments:** none.
- **Behavior:** returns `[{ shell_id, host, command, mode, exited }]` from the registry.

## Choosing the execution host

`host` on `shell_start` is the `targetClientInstance`. The toolset resolves it exactly as agent
execution does:

- `"."` → local (`LocalTrustedExecutor`).
- a forward-remote id → `RemoteTrustedExecutor` (WebSocket `/stream/connect` on that instance).
- a reverse-connected id → `ReverseTrustedExecutor` (reverse channel).

Each `shell_start` independently selects an executor, so **one agent can hold shells on multiple
hosts simultaneously**, each gated by that host's resolved `TrustProfile` (`shell ∈
AllowedStreamKinds`). A host the trust profile disallows yields a tool error, not a thrown
exception.

## Toolset, factory, and the shell registry

- **`ShellToolsetContextProvider : AIContextProvider`** — unique
  `StateKeys = $"shell-toolset:{Guid.NewGuid():n}"`; `ProvideAIContextAsync` returns the five
  `shell_*` `AIFunction`s.
- **`ShellSessionRegistry`** — owns the agent's live shell sessions (id → session = the
  `ITerminalSession` (`Stream` + resize/exit) + an output buffer). Created per agent. It is the
  **same** object the agent editor observes as `IAgentShellRegistry`, so tools and the GUI share
  one source of truth (the GUI binds a terminal control to each session's `Stream`).
- **`ToolsetFactory.CreateShellToolsetFactory(executorSelector, trustProfileProvider, shellRegistry, …)`**
  — registers kind **`shell`**, closed over the trusted-executor selector, the trust-profile
  provider, and the per-agent `ShellSessionRegistry`. The **host** builds this when starting an
  agent and `Combine`s it into the toolset-factory chain (the host knows the agent's trust
  profile and owns the registry it also hands to the agent editor).
- An agent-definition opts in by listing a `shell` tool resource.

Because the registry is created by the host and shared with the agent editor, a human sees and
can interact with exactly the shells the agent started (`shell-pty-terminal.md`).

## Source layout

In `Phantom.Workspaces.Llm.Core` (or a `Shell/` subfolder):

- `ShellToolsetContextProvider.cs` — the provider + the five `shell_*` `AIFunction`s.
- `ShellSessionRegistry.cs` / `IAgentShellRegistry.cs` — the shared registry (interface consumed
  by the agent GUI).
- `ShellSession.cs` — id, host, command, mode, the `ITerminalSession` (`Stream` + resize/exit),
  and the output buffer.
- `ToolsetFactory.cs` — add `CreateShellToolsetFactory`.

The host (`Phantom.Workspaces` / agent startup) builds the registry, creates the factory, and
combines it in; the agent editor (`Phantom.Workspaces.Agent.Gui`) consumes the registry.

## Tests

Unit (in-memory transport + fake `IPseudoTerminal`, deterministic — no network/timing):

- **`shell_start`** opens a stream for the requested `host`/`mode`/`command`, registers a session,
  and returns a `shell_id`; a disallowed host (trust profile) returns a tool error, not an
  exception.
- **`shell_write`** frames input; with a fake echo terminal, the bytes appear in the session
  buffer.
- **`shell_read`** drains buffered output, honors `max_bytes`/`truncated`, and reports
  `exited`/`exit_code` after the process exits.
- **`shell_stop`** signals/disposes the stream, removes the session, returns the exit code.
- **`shell_list`** reflects registered sessions and their `exited` state.
- **Multi-host:** two `shell_start`s with different `host` values select different executors and
  produce independent sessions (assert via fake executors per host).
- **Registry sharing:** a started shell appears in the `IAgentShellRegistry` the agent editor
  observes (one source of truth).
- **Toolset/factory:** `CreateShellToolsetFactory` returns a `ShellToolsetContextProvider` for
  kind `shell`; the provider exposes exactly the five tools with a unique `StateKeys`.
- **Tool metadata:** each tool's `Name`/`JsonSchema` matches the documented surface.

Determinism: fakes for executor/terminal/registry; event-driven synchronization; no
timing-based waits.

## Open questions

1. **PTY output to the agent.** Should `shell_read` strip VT control sequences in `pty` mode
   (cleaner text for the model) or return them raw? Proposed: default `pipe` mode for agents
   (raw text, no control sequences); for `pty`, offer a `strip_control_sequences` flag on
   `shell_read`.
2. **Buffer bounds.** Cap per-session output buffer size (ring buffer) to avoid unbounded memory
   for chatty processes? Proposed: a bounded ring buffer with `truncated` signaling.
3. **Approval.** Should `shell_start` route through the trust approval flow (like MCP tool-call
   authorization) for interactive confirmation, beyond the static `AllowedStreamKinds` gate?
   Proposed: static gate for v1; optional approval hook later.
