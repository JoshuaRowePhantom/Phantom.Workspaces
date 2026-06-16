# Shell entity, PTY transport, and terminal GUI

> **Status: draft — design for review.** Proposes the `shell` entity type, the trust-scoped
> pseudo-terminal (PTY) transport over WebSockets, and an Avalonia terminal control. No
> implementation should land until this is approved. Tracks todos `shell-entity-type`,
> `shell-pty-transport-gui-design`, and `ucp-start-shell-shortcut`.

## Problem & scenario

A user wants to open an interactive shell **on a specific host** — the local machine, a
connected-to Phantom.Workspaces instance, or an instance connected in over a reverse tunnel — and
drive it from an Avalonia terminal control, with the process running under the same trust-execution
scheme that governs agents and tools. The shell is a first-class workspace entity (so it can be
saved, named, related, and reopened), and is launched from the containing `user-computer-profile`
that identifies the host it runs on.

This builds directly on the trust-execution seam (`docs/design/trust-models.md`) and the
reverse-tunnel transport (`docs/design/reverse-tunnel-trust-execution.md`): both already establish
how execution is dispatched to a target client instance (local, forward-remote, or reverse-remote).
The shell adds a **byte-streaming, bidirectional, long-lived** channel rather than a request/response
agent turn.

## Terminology

- **Host:** the machine the shell process runs on, identified by the `user-computer-profile`/client
  instance id that contains the shell. Resolved to a `targetClientInstance` exactly as agent
  execution is (`"."` local, a forward-remote id, or a reverse-connected id).
- **PTY:** a pseudo-terminal — ConPTY on Windows, `forkpty`/`openpty` on Unix — that gives the child
  process a real terminal so interactive programs (REPLs, editors, pagers) behave correctly.
- **Terminal control:** the Avalonia control that renders the VT/xterm output stream and forwards
  keystrokes and resize events.

## The `shell` entity type (todo `shell-entity-type`)

A new entity type `shell` with a JSON schema at
`Phantom.Workspaces.Data.Core/JsonSchemas/shell.json` and an entity-type definition entity under
`JsonEntities/schema-definitions/shell-entity-type.json` (matching the existing pattern: `content`
points at a `/JsonEntities/documentation/shell-schema.md`, `schema` `$ref`s the flat
`/JsonSchemas/shell.json`). Per the schema-documentation convention, every property carries a
`description`.

Properties (all documented in the schema):

| Property | Type | Description |
| --- | --- | --- |
| `working-directory` | string | Absolute path on the host where the shell starts. |
| `command` | string | The executable to run (e.g. `pwsh`, `bash`). Defaults to the host's login shell when omitted. |
| `command-arguments` | string[] | Arguments passed to `command`. |
| `environment` | object (optional) | Extra environment variables (name → value) layered over the host defaults. |

The host is **not** a property of the shell: a shell is *contained by* a `user-computer-profile`
(via its hierarchical entity name, e.g. `[${USER}, computers, <machine>, shells, <name>]`). Opening a
shell resolves its containing `user-computer-profile` to determine the target host, mirroring how
`ucp-start-agent-session-shortcut` opens a session on the profile.

> **Convention note:** the host reference is derived from the containing profile (an entity name
> array), never a slash-joined string, consistent with the entity-reference convention.

## Transport: PTY over WebSocket frames

The shell session is a duplex byte stream multiplexed with small control frames. Like the reverse
channel, the **same framed protocol** is used regardless of which executor serves the host; only the
underlying carrier differs.

### Frame protocol (`ShellFrame`)

Binary WebSocket messages with a 1-byte type prefix (binary chosen over JSON+base64 for throughput;
control frames that need structure use a JSON body after the prefix):

| Direction | Type | Body | Meaning |
| --- | --- | --- | --- |
| client → host | `start` | JSON `{ workingDirectory, command, commandArguments, environment, columns, rows }` | Spawn the PTY. Sent once, first. |
| client → host | `input` | raw bytes | Keyboard/stdin bytes to the PTY. |
| client → host | `resize` | JSON `{ columns, rows }` | Window resize. |
| client → host | `signal` | JSON `{ signal }` | Interrupt/terminate (e.g. Ctrl+C is normally just `input`; `signal` is for explicit kill). |
| host → client | `output` | raw bytes | PTY stdout/stderr bytes. |
| host → client | `exit` | JSON `{ exitCode }` | Process exited; channel closes. |
| host → client | `error` | JSON `{ code, message }` | Spawn or I/O failure. |

A `ShellFrame` reader/writer pair lives in `Phantom.Workspaces.Llm.Core/Shell/` alongside an
`IShellMessageChannel` (mirroring `IReverseMessageChannel`) with an in-memory pair for deterministic
tests and a `WebSocketShellMessageChannel` for production.

### Executor dispatch (trust-scoped)

The host is resolved to a `targetClientInstance` and an executor is selected with the **existing**
`ITrustedExecutorSelector` flow, extended with a parallel `IShellExecutor` seam so trust profiles can
permit/deny shell execution independently of agent execution:

| Target | Carrier | Notes |
| --- | --- | --- |
| Local (`"."`) | in-process PTY | `LocalShellExecutor` spawns ConPTY/forkpty directly; client and host frames cross an in-memory channel. |
| Forward-remote (C → S) | `GET /shell/connect` WebSocket on S | The connecting instance opens a WebSocket (same `X-Tunnel-Authorization` auth as `/agent/respond` and `/reverse/connect`); S runs `LocalShellExecutor`. |
| Reverse-remote (S → C) | reverse channel | A new reverse frame type carries a shell-session id; S asks C to open a shell and relays `ShellFrame`s. Reuses the registry/connection from the reverse-tunnel design. |

Trust enforcement: a `shell` execution is gated by the resolved `TrustProfile` for the target host —
a new effective permission (e.g. `allow-shell`) so the default profiles (`current-machine`,
`all-machines`, `workspace-read-only`, `no-tool`, `all-tools`) can grant or withhold shell access.
`workspace-read-only` and `no-tool` deny it; `all-tools` and the machine profiles grant it.

### Server endpoint

`MapShellEndpoints` adds `GET /shell/connect` (WebSocket upgrade) in
`Phantom.Workspaces.Web.Server`, accepting the socket, wrapping it in a
`WebSocketShellMessageChannel`, resolving the trust profile, and handing it to `LocalShellExecutor`.
Wired in `Program.cs` next to `MapReverseEndpoints`, guarded by the same `UseWebSockets()`.

## PTY implementation

A small `IPseudoTerminal` abstraction in `Phantom.Workspaces.Llm.Core/Shell/`:

- **Windows:** ConPTY (`CreatePseudoConsole`/`ResizePseudoConsole`) via P/Invoke, with the child
  process launched through `STARTUPINFOEX` + `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`.
- **Unix:** `openpty`/`forkpty` (or `posix_openpt`) P/Invoke.

To avoid hand-rolling both, evaluate the `Pty.Net` package (cross-platform PTY for .NET) as the
backing implementation behind `IPseudoTerminal`; if it is unsuitable for net10.0, fall back to the
direct P/Invoke layer above. The decision is recorded here once the NuGet research
(`scripts/research-nuget-apis.ps1`) is regenerated to cover PTY libraries.

## Terminal GUI

A reusable `TerminalControl` (Avalonia) in `Phantom.Workspaces` renders the VT/xterm output stream:

- **VT parsing:** feed `output` bytes into a VT/xterm emulator core. Evaluate `VtNetCore` or
  `XtermSharp` for the parser/screen-buffer; render the resulting cell grid with Avalonia drawing
  (monospace `FormattedText` / `RenderTargetBitmap`), rather than adopting a heavyweight prebuilt
  control. Per the styling convention, colors/fonts come from centralized shared styles, not inline
  control properties.
- **Input:** key and text events are translated to terminal input sequences and sent as `input`
  frames; paste sends bytes; a resize observer on the control's bounds sends `resize` frames
  (debounced).
- **Lifecycle:** the control binds to a `ShellSessionViewModel` that owns the `IShellMessageChannel`,
  exposes connection/exit state, and disposes the channel on close.

`ScrollViewer.AllowAutoHide="False"` where a scrollbar reserves space, consistent with the GUI
scrollbar convention.

## `user-computer-profile` "start shell" shortcut (todo `ucp-start-shell-shortcut`)

A "start shell" shortcut on the `user-computer-profile`, analogous to `ucp-start-agent-session-shortcut`:
it opens a view to configure/select the shell (working directory, command, arguments — pre-filled
from an existing `shell` entity when one is opened), then starts the session on the profile's host
and replaces the configuration view in place with the live `TerminalControl`. Opening an existing
`shell` entity skips straight to launching it on its containing profile's host.

## Test tasks

- **Shell entity type:** `SchemaPopulatorTests` covers the new `shell` entity-type + schema validity
  (populate is all-or-nothing). A round-trip test that a `shell` entity with
  `working-directory`/`command`/`command-arguments` validates via
  `EntityRepository.CreateAsync(new UnknownRepositorySource())`.
- **Frame protocol:** `ShellFrame` serialization/round-trip tests (binary prefix + JSON bodies),
  including `start`/`input`/`resize`/`output`/`exit`/`error`.
- **Channel + executor (deterministic, in-memory):** end-to-end test using
  `InMemoryShellMessageChannelPair` + a fake `IPseudoTerminal` that echoes input to output and exits
  on a sentinel: assert `start` spawns, `input` is echoed as `output`, `resize` is delivered, and
  `exit` propagates. No `Task.Delay`; synchronize via `TaskCompletionSource`/channel completion, per
  the deterministic-tests rule.
- **Endpoint:** route-mapping test that `/shell/connect` is mapped (matching
  `ReverseEndpointRouteBuilderExtensionsTests`).
- **Trust gating:** tests that `workspace-read-only`/`no-tool` deny shell execution and
  `all-tools`/machine profiles permit it.
- **GUI view-model:** `ShellSessionViewModel` tests (synchronous `dispatch`) that output updates the
  buffer, input is framed, resize is framed/debounced, and exit flips connection state.
- **Reverse-remote path:** extend the reverse-tunnel in-memory e2e to carry a shell session and
  assert byte frames round-trip C↔S.

## Implementation steps (after approval)

1. `shell` entity type: schema + entity-type definition + documentation note + `SchemaPopulatorTests`.
2. `ShellFrame` + `IShellMessageChannel` + in-memory pair (+ serialization tests).
3. `IPseudoTerminal` abstraction + a fake for tests; pick ConPTY/forkpty vs `Pty.Net`.
4. `LocalShellExecutor` + deterministic in-memory e2e.
5. `WebSocketShellMessageChannel` + `MapShellEndpoints('/shell/connect')` + `Program.cs` wiring.
6. Trust: `allow-shell` effective permission + default-profile coverage.
7. Reverse-remote shell frames over the existing reverse channel.
8. `TerminalControl` + VT parser selection + `ShellSessionViewModel`.
9. `user-computer-profile` "start shell" shortcut + open-existing-shell flow.

## Open questions

1. **VT emulator / PTY libraries:** confirm `VtNetCore`/`XtermSharp` and `Pty.Net` viability on
   net10.0, or commit to the direct P/Invoke + custom renderer path.
2. **Binary vs JSON frames:** binary is proposed for `input`/`output`; confirm the reverse channel
   (currently JSON-only) should gain a binary frame variant for the reverse-remote shell path, or
   whether shell bytes ride base64 inside the existing JSON reverse frames.
3. **Session persistence:** should a disconnected shell session survive on the host for reattach, or
   terminate on socket close? (Proposed: terminate on close for v1; reattach is a later enhancement.)
