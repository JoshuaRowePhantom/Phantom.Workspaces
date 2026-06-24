# Shell entity, PTY transport, and terminal GUI

> **Status: design for review.** Proposes the streamed-process ("shell") feature: a generic
> bidirectional **stream** capability on the trusted executor (WebSockets for web/dev-tunnel
> hosts), **PTY and pipe** run modes, an Avalonia terminal control added to the renamed
> `Phantom.Workspaces.Gui.Styles` → `Phantom.Workspaces.Gui.Shared` assembly, a workspace
> **shell tab**, and shells shown in the **agent editor**. LLM control of shells is designed in
> `docs/design/shell-llm-toolset.md`.

## Problem & scenario

A user (or an agent) wants to run an arbitrary program **on a specific host** — the local
machine, a forward-connected Phantom.Workspaces instance, or one connected over a reverse
tunnel — and drive it from an Avalonia terminal control, under the same trust-execution scheme
that governs agents and tools. "Shell" is shorthand for **any executable** the user decides to
run. Shells are usually **ephemeral**, so they are **not** backed by an entity by default.

This builds on the trust-execution seam (`docs/design/trust-models.md`) and the reverse-tunnel
transport (`docs/design/reverse-tunnel-trust-execution.md`), which already dispatch execution to
a target client instance (local, forward-remote, reverse-remote). The shell adds a
**byte-streaming, bidirectional, long-lived** channel rather than a request/response agent turn.

## Terminology

- **Host:** the machine the process runs on, resolved to a `targetClientInstance` exactly as
  agent execution is (`"."` local, a forward-remote id, or a reverse-connected id).
- **PTY:** a pseudo-terminal — ConPTY on Windows, `openpty`/`forkpty` on Unix — that gives the
  child process a real terminal so interactive programs (REPLs, editors, pagers), colors,
  control characters, and mouse tracking behave correctly.
- **Terminal control:** the Avalonia control that renders the VT/xterm output stream and
  forwards keystrokes, mouse, and resize events.

## Generic stream capability on the trusted executor

Today `ITrustedExecutor` (`Phantom.Workspaces.Llm.Core/Trust/ITrustedExecutor.cs`) only creates
agent chats:

```csharp
bool CanExecute(string targetClientInstance);
Task<AgentChat> CreateAgentChatAsync(TrustedExecutionRequest request, CancellationToken ct);
```

It gains a generic **stream** method so the same trust-scoped, host-resolving dispatch can open
an arbitrary long-lived bidirectional **`Stream`**; the shell is the first stream **kind**:

```csharp
// new on ITrustedExecutor — opens a streaming session and returns its duplex byte stream
Task<Stream> OpenStreamAsync(TrustedStreamRequest request, CancellationToken ct);

public sealed record TrustedStreamRequest
{
    public required string TargetClientInstance { get; init; } // "." | forward id | reverse id
    public required string StreamKind { get; init; }           // "shell"
    public required JsonElement OpenPayload { get; init; }      // kind-specific (shell start args)
}
```

`OpenStreamAsync` returns a plain `System.IO.Stream` — a duplex byte channel (read = host→client,
write = client→host). There is **no** public `ITrustedStream`/`StreamFrame` surface; framing is an
**implementation detail** of the executor (see *Wire encoding*). `OpenStreamAsync` is implemented
on all three executors (`LocalTrustedExecutor`, `ReverseTrustedExecutor`, `RemoteTrustedExecutor`);
selection is unchanged (`ITrustedExecutorSelector.SelectExecutor(TrustProfile, targetClientInstance)`).

### Wire encoding — internal, binary (resolves open question 2)

How the returned `Stream`'s bytes (and shell control like resize/exit) cross the network is
**private to `ITrustedExecutor`'s implementations**. A binary `StreamFrame` (1-byte kind prefix,
then raw bytes or a small JSON control body) is used — binary over JSON+base64 for terminal
throughput. The reverse channel (currently JSON-only `ReverseFrame`) gains a **binary frame
variant** so reverse-remote bytes are not base64-in-JSON. `StreamFrame`, the
`IStreamMessageChannel`, its `InMemoryStreamMessageChannelPair` (tests), and `WebSocketTrustedStream`
all live in `Phantom.Workspaces.Llm.Core/Shell/` as **internal plumbing**; consumers only ever see
the `Stream`.

### Per-target carriers

Each executor adapts its carrier into the duplex `Stream` returned by `OpenStreamAsync`:

| Target | Carrier | Notes |
| --- | --- | --- |
| Local (`"."`) | in-process | `LocalTrustedExecutor.OpenStreamAsync` dispatches to a registered local handler for the kind (`LocalShellStreamHandler` spawns the PTY/pipe) and returns an in-memory duplex `Stream` wired to it. |
| Forward-remote (C → S) | `GET /stream/connect?kind=shell` WebSocket on S | Same `X-Tunnel-Authorization: tunnel <token>` auth as `/agent/respond` and `/reverse/connect`; the socket is adapted to a `Stream`; S runs the local handler. |
| Reverse-remote (S → C) | reverse channel | A new `ReverseFrame` type `OpenStream` carries a stream-session id + kind + payload; subsequent bytes ride the binary reverse-frame variant, adapted to a `Stream`. Reuses `ReverseExecutionRegistry`/`ReverseChannelConnection`. |

### Server endpoint

`MapStreamEndpoints` adds `GET /stream/connect` (WebSocket upgrade) in
`Phantom.Workspaces.Web.Server`, adapting the socket to a `Stream`, resolving the trust profile,
and handing it to the registered handler for `kind`. Wired in `Program.cs` next to
`MapReverseEndpoints`, under the existing `app.UseWebSockets()`.

### Trust gating

`TrustProfileDefinition`/`TrustProfile` gain an **`AllowedStreamKinds`** set (effective, composed
by `TrustProfileComposer`, parsed by `TrustProfileEntityReader`). Opening a `shell` stream
requires `shell ∈ AllowedStreamKinds` for the resolved target, checked in `OpenStreamAsync`
(parallel to `TrustToolCallAuthorizer` for tool calls). Defaults:

- `workspace-read-only`, `no-tool` → deny (`shell` not in the set).
- `all-tools`, `current-machine`, `all-machines` → grant.

## Shell session over the `Stream` (`StreamKind == "shell"`)

For a shell, `OpenStreamAsync` returns the duplex `Stream` whose **bytes are the terminal data**
(read = PTY/process output, write = stdin/keyboard input). The start parameters are passed once in
`OpenPayload`:

```json
{
  "mode": "pty" | "pipe",
  "command": "pwsh",
  "command-arguments": ["-NoLogo"],
  "working-directory": "C:\\src",
  "environment": { "FOO": "bar" },
  "columns": 120, "rows": 30
}
```

### Data is the `Stream`; control is a method

The terminal byte data flows through the `Stream` — that is all the terminal control needs.
**Window-size and signals are not data**, so they are not written into the byte stream; they are
exposed as **methods** on a thin shell-session wrapper that the shell layer builds around the
`Stream`:

```csharp
public interface ITerminalSession : IAsyncDisposable
{
    Stream Stream { get; }                                  // duplex terminal bytes (the Stream)
    ValueTask ResizeAsync(int columns, int rows, CancellationToken ct); // pty mode only
    ValueTask SignalAsync(string signal, CancellationToken ct);
    Task<int> WaitForExitAsync();                           // completes with the exit code
}
```

- The **terminal control** uses only `ITerminalSession.Stream` (read output / write input) plus
  the `ResizeAsync` delegate wired to its bounds observer — it never sees frames.
- `ResizeAsync`/`SignalAsync`/exit are carried over the **same transport** as out-of-band control
  (the internal `StreamFrame` plumbing of `ITrustedExecutor`); the shell layer multiplexes them
  with the byte data and exposes the demultiplexed terminal bytes as `Stream`.
- The owner (`ShellTabViewModel` / the toolset) holds the `ITerminalSession` and forwards the
  terminal control's resize events to `ResizeAsync`.

### PTY vs. pipe mode

- **pty mode** (`mode: "pty"`): the host spawns the process attached to a pseudo-terminal
  (ConPTY/forkpty). Interactive programs, colors, control characters, and **mouse tracking** work.
  `Stream` carries the VT/xterm bytes; the initiating process renders them on its terminal control
  and calls `ResizeAsync` on window changes — *the captured PTY output is displayed on a PTY on the
  initiating process*.
- **pipe mode** (`mode: "pipe"`): no PTY; stdout/stderr/stdin are plain pipes. `Stream` is the raw
  captured bytes (no terminal emulation); `ResizeAsync` is a no-op. Use for non-interactive
  capture (build output, scripts).

### PTY implementation (host side, resolves open question 1)

`IPseudoTerminal` in `Phantom.Workspaces.Llm.Core/Shell/` (host/executor side, **not** the GUI):

- **Windows:** ConPTY (`CreatePseudoConsole`/`ResizePseudoConsole`/`ClosePseudoConsole`) via
  P/Invoke; child launched with `STARTUPINFOEX` + `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`.
- **Unix:** `openpty`/`forkpty` P/Invoke.

**NuGet verification (net10.0):**

- **`Pty.Net`** — only **pre-release** (latest `0.1.16-pre`), targets `.NETFramework4.6` /
  `netstandard2.0`, unmaintained. Consumable from net10.0 (via netstandard2.0) but too stale to
  take a hard dependency on → **do not depend on it**; implement `IPseudoTerminal` with our own
  ConPTY/forkpty P/Invoke (small, well-documented surface).
- A fake `IPseudoTerminal` (echoes input→output, exits on a sentinel) backs deterministic tests.

`LocalShellStreamHandler` bridges the transport `Stream` (and its control multiplexing) ↔
`IPseudoTerminal` (pty mode) or `Process` std pipes (pipe mode).

## Terminal display control

### Is there an existing Avalonia terminal control?

There is **no mature, standard Avalonia terminal control** to depend on. The viable libraries are
VT **emulator cores** (parser + screen-buffer) without an Avalonia renderer, so we build our own
control around one of them:

- **`VtNetCore`** — **stable** (`1.0.30`), `netstandard2.0` → consumable on net10.0; a pure
  VT100/xterm parser + screen buffer with no UI dependency. **Chosen** as the VT core.
- **`XtermSharp`** — only `1.0.0-alpha.10` (alpha), netstandard2.0; not chosen.

### `TerminalControl` (in `Phantom.Workspaces.Gui.Shared`)

- **VT core:** feed the `Stream`'s output bytes into `VtNetCore`, which maintains the screen
  buffer (cells, colors, attributes, cursor, scrollback) and **terminal modes** (alt-screen,
  bracketed paste, and **mouse tracking** — X10/normal/button/any with SGR encoding).
- **Render:** draw the cell grid with Avalonia (monospace glyph runs onto a `DrawingContext`);
  colors/fonts from centralized shared styles (`Gui.Shared`), not inline.
- **Input:** translate key/text events to terminal input sequences and **write them to the
  `Stream`**; translate Avalonia pointer events to xterm mouse sequences when mouse tracking is
  enabled; paste → bytes (bracketed when enabled). A bounds observer maps pixel size →
  columns/rows and calls the **debounced** resize delegate.
- **Binding:** binds to a `TerminalSessionViewModel` that holds a `Stream` (the terminal bytes)
  and a resize delegate (`(columns, rows) => session.ResizeAsync(...)`), exposes connection/exit
  state and the screen buffer, and disposes on close. The control depends **only** on
  `System.IO.Stream` + the resize delegate — **not** on `Llm.Core`, `StreamFrame`, or any trust
  type — so `Gui.Shared` needs no `Llm.Core` dependency. It serves local and remote shells, and
  both the workspace shell tab and the agent editor, identically.
- Scrollbars use `ScrollViewer.AllowAutoHide="False"` per the GUI scrollbar convention.

## Assemblies and styling

### Rename `Gui.Styles` → `Gui.Shared` and add the terminal control

`Phantom.Workspaces.Gui.Styles` (net10.0, references `Avalonia`; holds `SharedStyles.axaml` and
`Controls/Sticky*`) is renamed to `Phantom.Workspaces.Gui.Shared` and repurposed as the shared
GUI primitives library — **including the terminal control** — so both the main app and the agent
GUI can host live PTYs from one place (no separate controls assembly):

- Rename the project/folder/assembly and the root namespace.
- Update the three referencing projects — `Phantom.Workspaces`, `Phantom.Workspaces.Agent.Gui`,
  and the styles test project — `ProjectReference`s.
- Update `StyleInclude` URIs in both `App.axaml`s:
  `avares://Phantom.Workspaces.Gui.Styles/Styles/SharedStyles.axaml` →
  `avares://Phantom.Workspaces.Gui.Shared/Styles/SharedStyles.axaml`.
- Update `using:Phantom.Workspaces.Gui.Styles.Controls` XML namespaces.
- Rename `Phantom.Workspaces.Gui.Styles.Tests` → `…Gui.Shared.Tests`.

**Terminal control added to `Gui.Shared`:** `TerminalControl.axaml`/`.cs`, the `VtNetCore`
integration, `TerminalSessionViewModel`, and terminal color resources/keys. The control consumes a
`System.IO.Stream` + a resize delegate, so `Gui.Shared` gains only a `VtNetCore` package reference
and **no dependency on `Llm.Core` / `StreamFrame` / trust types**. Both `Phantom.Workspaces` and
`Phantom.Workspaces.Agent.Gui` already reference `Gui.Shared`, so no new project references are
needed for the terminal control.

## GUI surface 1: workspace shell tab

`ShellTabViewModel : WorkspaceTabViewModel` (in `Phantom.Workspaces/ViewModels`):

- Holds a `TerminalSessionViewModel` over the `ITerminalSession` (its `Stream` + resize);
  `Title` = command + host.
- A `DataTemplate` in `Phantom.Workspaces/Templates/WorkspaceDataTemplates.axaml` maps
  `ShellTabViewModel` → a view hosting `TerminalControl` (from `Phantom.Workspaces.Gui.Shared`)
  bound to `TerminalSession`.
- Opened via `MainWindowViewModel.OpenTabAsync(...)`, reused by `Id`.

### Correction: the start-shell shortcut must not create an entity

The current `StartShellOnProfileShortcutHandler` creates a `shell` entity plus an `owned-by`
relationship and then opens the entity — wrong for ephemeral shells. It is rewritten to:

1. Resolve the host from the `user-computer-profile` (its client-instance id / `"."`).
2. `OpenStreamAsync` a `shell` session to that host (default command = host login shell,
   `mode: "pty"`), gated by the trust profile, and wrap the returned `Stream` in an
   `ITerminalSession`.
3. Open a `ShellTabViewModel` bound to that session.

No entity or relationship is created for an ephemeral shell.

## The `shell` entity type (saved configurations only)

A `shell` entity type is retained, but **only** for **saved** shell configurations/templates
(not for ephemeral runs). Schema at `Phantom.Workspaces.Data.Core/JsonSchemas/shell.json` +
`JsonEntities/schema-definitions/shell-entity-type.json` (matching the existing pattern; every
property documented per the schema-documentation convention).

| Property | Type | Description |
| --- | --- | --- |
| `mode` | string (`pty`/`pipe`) | Run mode for the saved configuration. |
| `command` | string | Executable to run (e.g. `pwsh`, `bash`). |
| `command-arguments` | string[] | Arguments passed to `command`. |
| `working-directory` | string | Absolute path on the host where the shell starts. |
| `environment` | object (optional) | Extra environment variables layered over host defaults. |

Opening a saved `shell` entity pre-fills the start payload and launches it on the chosen host;
it does not imply that ephemeral shells become entities. The host is not a property of the
shell — a saved shell is launched against a host chosen at open time (or, if contained by a
`user-computer-profile`, that profile's host), via an entity-name reference, never a slash-joined
string.

## GUI surface 2: agent editor shells

Agent-started shells (via the toolset in `shell-llm-toolset.md`) are visible in the agent editor
(`Phantom.Workspaces.Agent.Gui/ViewModels/AgentViewModel.cs`):

- The shell toolset publishes its live `TerminalSessionViewModel`s into an
  **`IAgentShellRegistry`** (an observable collection of the agent's running shells), supplied to
  the toolset when the host builds it (parallel to the host-created toolset factory in
  `session-context-tools.md`).
- `AgentViewModel` observes the registry and adds a **"Shells"** node to `EditorItems` (next to
  the "Tools" node built by `BuildEditorTree()`), one child per running shell. Selecting a shell
  sets `SelectedEditorDetailContent` to a detail hosting `TerminalControl` bound to that shell's
  session — so the human watches/types into the same live PTY the agent drives. A new
  shell-detail `DataTemplate` is added in `AgentChatEditorControl.axaml`.

## Classes, AXAML, and files to implement

**`Phantom.Workspaces.Llm.Core` (`/Trust`, `/Shell`):**
- `ITrustedExecutor.OpenStreamAsync` (returns `Stream`) + `TrustedStreamRequest`; implement in
  `LocalTrustedExecutor`, `ReverseTrustedExecutor`, `RemoteTrustedExecutor`.
- **Internal** wire plumbing: `StreamFrame` (+ reader/writer), `IStreamMessageChannel`,
  `InMemoryStreamMessageChannelPair`, `WebSocketTrustedStream`, and `Stream` adapters over the
  carriers — none public.
- `ITerminalSession` (`Stream` + `ResizeAsync`/`SignalAsync`/exit) + a `ShellSession`
  implementation that wraps the transport `Stream` and multiplexes control.
- `IStreamHandler` registry + `LocalShellStreamHandler`.
- `IPseudoTerminal` (ConPTY/forkpty P/Invoke) + a fake.
- Reverse: `ReverseFrame` `OpenStream` type + binary reverse-frame variant.
- Trust: `AllowedStreamKinds` on `TrustProfileDefinition`/`TrustProfile`, composer/reader support,
  and updated default-profile entities.

**`Phantom.Workspaces.Web.Server`:**
- `StreamEndpointRouteBuilderExtensions.MapStreamEndpoints` (`GET /stream/connect`) + `Program.cs`
  wiring.

**`Phantom.Workspaces.Gui.Shared` (renamed from `Gui.Styles`):**
- Project/namespace rename; `TerminalControl.axaml`/`.cs` + `VtNetCore` integration +
  `TerminalSessionViewModel` (consumes a `Stream` + resize delegate); terminal (and status-badge)
  color resources in `SharedStyles.axaml`. Adds only a `VtNetCore` package reference.

**`Phantom.Workspaces` (main app):**
- `ShellTabViewModel`; `WorkspaceDataTemplates.axaml` template; rewrite
  `StartShellOnProfileShortcutHandler` (wraps the `Stream` in an `ITerminalSession`).

**`Phantom.Workspaces.Agent.Gui`:**
- `IAgentShellRegistry`; `AgentViewModel` "Shells" node + shell detail; shell-detail
  `DataTemplate` in `AgentChatEditorControl.axaml`.

**`Phantom.Workspaces.Data.Core`:**
- `shell` entity type (saved configs): `shell.json` + entity-type entity + documentation note.

## Test tasks

- **Stream transport (deterministic, in-memory):** `OpenStreamAsync` returns a duplex `Stream`
  over `InMemoryStreamMessageChannelPair` + a fake `IPseudoTerminal`; writing input bytes to the
  `Stream` round-trips as output bytes; `ResizeAsync`/exit are delivered. No `Task.Delay`;
  synchronize via channel completion / `TaskCompletionSource`.
- **Internal frame serialization:** the (internal) binary `StreamFrame` round-trips data + control,
  including the binary reverse-frame variant.
- **Modes:** pty mode delivers a VT byte stream and honors `ResizeAsync`; pipe mode delivers raw
  bytes and `ResizeAsync` is a no-op.
- **Executor dispatch:** Local/Reverse/Remote `OpenStreamAsync` select per `targetClientInstance`;
  reverse path carries a shell `Stream` over the in-memory reverse pair (extends the reverse e2e).
- **Endpoint:** `/stream/connect` is mapped (matching `ReverseEndpointRouteBuilderExtensionsTests`).
- **Trust gating:** `workspace-read-only`/`no-tool` deny `shell`; `all-tools`/machine profiles
  permit (assert `AllowedStreamKinds`).
- **Terminal view model:** `TerminalSessionViewModel` updates the screen buffer from the `Stream`'s
  output bytes, writes input bytes to the `Stream`, debounces and calls the resize delegate, and
  flips state on exit (synchronous dispatch over an in-memory `Stream`).
- **VT core:** control characters, SGR color, alt-screen, and SGR-mouse sequences produce the
  expected screen-buffer / mouse-mode state.
- **Shell tab / correction:** `StartShellOnProfileShortcutHandler` opens a `ShellTabViewModel`
  and creates **no** entity or relationship.
- **Agent editor shells:** publishing into `IAgentShellRegistry` adds a "Shells" child; selecting
  it yields a terminal detail bound to that session.
- **Shell entity type:** `SchemaPopulatorTests` covers the `shell` entity-type + schema validity;
  a saved `shell` entity round-trips.
- **Rename safety:** build/tests green after `Gui.Styles`→`Gui.Shared`; a smoke test that
  `SharedStyles.axaml` loads from the new `avares://` URI.

## Implementation steps (after approval)

1. Generic stream seam: `ITrustedExecutor.OpenStreamAsync` (returns `Stream`) +
   `TrustedStreamRequest` + internal `StreamFrame`/in-memory channel/pair (+ serialization tests).
2. `IPseudoTerminal` (ConPTY/forkpty) + fake; `LocalShellStreamHandler`; `ITerminalSession`/
   `ShellSession`; `LocalTrustedExecutor` path + deterministic e2e.
3. `WebSocketTrustedStream` (internal) + `MapStreamEndpoints('/stream/connect')` + `Program.cs`.
4. Trust: `AllowedStreamKinds` + default-profile coverage.
5. Reverse: `OpenStream` frame + binary reverse-frame variant; reverse-remote shell e2e.
6. `Gui.Styles`→`Gui.Shared` rename; `TerminalControl` + `VtNetCore` + `TerminalSessionViewModel`
   (over a `Stream` + resize delegate).
7. `ShellTabViewModel` + template; rewrite `StartShellOnProfileShortcutHandler` (no entity).
8. `shell` entity type for saved configurations.
9. Agent-editor shells: `IAgentShellRegistry` + `AgentViewModel` "Shells" node + detail template.
10. The LLM shell toolset (`docs/design/shell-llm-toolset.md`).

## Resolved decisions (formerly open questions)

1. **VT emulator / PTY libraries — verified.** Use **`VtNetCore` 1.0.30** (stable, netstandard2.0,
   consumable on net10.0) as the VT core; **`XtermSharp`** is alpha-only and not used. **`Pty.Net`**
   is pre-release/unmaintained — do **not** depend on it; implement `IPseudoTerminal` with direct
   ConPTY/forkpty P/Invoke.
2. **Binary vs JSON frames — binary (internal).** The internal `StreamFrame` wire encoding is
   binary; the reverse channel gains a **binary frame variant** so reverse-remote bytes are not
   base64-in-JSON. This framing is private to `ITrustedExecutor`; consumers use a `Stream`.
3. **Session persistence — none.** A shell terminates on stream/socket close; no reattach in v1.
4. **Terminal control dependency — resolved by the `Stream` interface.** Because
   `OpenStreamAsync` returns a `Stream` and the terminal control consumes only `Stream` + a resize
   delegate (`ITerminalSession`), `Gui.Shared` needs **no** dependency on `Llm.Core`/`StreamFrame`/
   trust types. No separate contracts assembly is required.
