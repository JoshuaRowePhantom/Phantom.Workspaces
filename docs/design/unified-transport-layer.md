# Design: Unified Transport Layer

> **Status:** Phase 5 — Implementation plan complete; ready for commit and bug filing

---

## Requirements

### Core abstractions

1. A **`IMessageChannel`** abstraction provides a bidirectional, async, `JsonElement`-typed communication channel with a `ChannelWriter<JsonElement>` and a `ChannelReader<JsonElement>`. It is `IAsyncDisposable`.

2. A **`ITransport`** abstraction can open named/requested message channels and raw byte streams from a client. It is `IAsyncDisposable` and is kept alive by the client explicitly or implicitly through any live `IMessageChannel` or `Stream` instances it has vended.

3. A **`ITransportFactory`** abstraction creates `ITransport` instances from a `JsonElement` connection descriptor. It is `IAsyncDisposable`.

4. When a `ITransport` is disposed on the client side, a disposal message is sent to the server, and all server-side objects created through that transport are disposed.

5. When an individual `IMessageChannel` or `Stream` returned by a transport is disposed, only its corresponding server-side object is disposed — the transport remains open.

6. Each `ITransport` embodies a **leasing** mechanism: if no activity is received over the connection for a configurable period, the transport is closed and all attached server-side objects are disposed.

### Concrete transport implementations

7. **`LocalTransport`** (`LocalTransportFactory`) — in-process transport that routes to locally registered listeners without any network or serialization overhead. All the same primitives (`IMessageChannel`, `Stream`) are supported, enabling the same RPC abstractions to work identically locally and remotely.

8. **`HttpTransport`** (`HttpClientTransportFactory`) — forward-direction HTTP transport to a listening Phantom.Workspaces instance, directly or via Microsoft Dev Tunnel (authenticated via `X-Tunnel-Authorization`).

9. **`ReverseHttpTransport`** (`ReverseHttpClientTransportFactory` + `ReverseHttpServerTransportFactory`) — reverse-direction transport built on top of `HttpTransport`'s own `ConnectToMessageChannel` / `ConnectToStream`. The reverse server listener answers incoming `ConnectToMessageChannel` / `ConnectToStream` requests from its remote peer and routes them to locally registered listeners. When a remote Phantom.Workspaces instance connects via dev tunnel or HTTP, it **proactively** registers itself with the `ReverseHttpClientTransportRegistry` so that the server side can initiate connections back to it.

10. (Future) **`ContainerTransport`** (`ContainerTransportFactory`) — transport into an isolated containerized process; only accessible via a local or reverse transport.

### Connection descriptor JSON schema

11. All transport factories and listeners inspect a `JsonElement` connection descriptor (schema `$connection`, an `anyOf`) to decide whether they handle a given request:

    | Schema `$id` | Shape | Meaning |
    |---|---|---|
    | `$local` | `{ "type": "local" }` | Local in-process transport |
    | `$http` | `{ "type": "http", "url": "https://...", "target": $connection }` | Forward HTTP to a remote instance |
    | `$reverse-http` | `{ "type": "reverse-http", "entity-id": $entityId, "target": $connection }` | Reverse HTTP (C→S direction inverted) |
    | `$user-computer-profile` | `{ "type": "user-computer-profile", "entity-id": $entityId, "target": $connection }` | Look up the profile entity, rewrite to `$http` or `$reverse-http` wrapper (or `$local` if the current machine is the profile) |
    | (future) `$container` | `{ "type": "container", ... }` | Isolated container process |

> **`$user-computer-profile` — `target` field.** The `target` field is a **chained connection descriptor** — a descriptor for a service or listener reachable *within* or *via* the remote machine identified by `entity-id`. `UserComputerProfileTransportFactory` first routes to the remote machine (using its stored `connection-descriptor`), then on the remote machine, dispatches the `target` descriptor through the remote machine's `ITransportFactoryRegistry`. This allows reaching, for example, a local MCP server on a remote machine:
>
> ```json
> {
>   "type": "user-computer-profile",
>   "entity-id": "<Machine-B-guid>",
>   "target": { "type": "local", "name": "workspace-tools-mcp" }
> }
> ```
>
> When `target` is absent, `UserComputerProfileTransportFactory` connects to the machine's default transport (the stored `connection-descriptor` resolved directly).

12. **`UserComputerProfileTransportFactory`** resolves the `entity-id` in a `$user-computer-profile` descriptor, determines whether the profile refers to the current machine (→ `$local`) or a remote machine (→ `$http` or `$reverse-http` wrapping the embedded `target`), and re-dispatches through `ITransportFactoryRegistry`.

### Server-side registries

13. **`ITransportRegistry`** holds a list of `ITransport` server-side listener instances. When an incoming `ConnectToMessageChannel` or `ConnectToStream` request arrives, it iterates listeners in registration order; the first to return a non-null object wins and that object is associated with the incoming transport for leasing and disposal.

14. **`ITransportFactoryRegistry`** holds a list of `ITransportFactory` instances and iterates them in the same way when `ConnectTo` is called with a connection descriptor.

15. **`HttpServerTransportFactory`** is the HTTP entry-point: it listens for inbound HTTP requests, routes them to a local `ITransportFactoryRegistry` that in turn can route to registered `ReverseHttpClientTransportRegistry` instances and to the `LocalTransportFactory`. It implements the server-side leasing logic.

### Transport listeners (server-side object factories)

16. **`McpTransportListener`** handles `{ "type": "mcp", "connection": $mcp-connection }` requests (where `$mcp-connection` comes from the `mcp-server.json` entity schema, extended with a new `$id`). Returns an `IAsyncDisposable` that represents an MCP server session, communicating over the provided `IMessageChannel`.

17. **`ChatClientTransportListener`** handles `{ "type": "chat-client", "definition": ... }` requests and returns an `IAsyncDisposable` representing an agent chat session, communicating over an `IMessageChannel`.

18. **`ShellTransportListener`** handles `{ "type": "shell", ... }` requests (parameters extracted from a new standalone `shell-parameters.json` JSON schema, referenced via `allOf` from the existing shell entity schema). Returns an `IAsyncDisposable` representing a PTY/shell session, communicating over a raw `Stream`.

### Client-side adapters

19. **`McpClientOverTransport`** implements `IMcpClient` by opening an `IMessageChannel` via `ITransport.ConnectToMessageChannel` with an MCP connection descriptor, then forwarding MCP protocol messages over it.

20. **`ChatClientOverTransport`** (replacement for `WebRemoteChatClient` and `ReverseRemoteChatClient`) implements `IChatClient` by opening an `IMessageChannel` via `ITransport.ConnectToMessageChannel` with a chat-client connection descriptor.

21. **`ShellOverTransport`** (replacement for existing shell stream code) implements the PTY session by opening a `Stream` via `ITransport.ConnectToStream` with a shell connection descriptor.

### Executor context for MCP and tools

22. MCP servers and tools configured in an agent definition use the **same executor** as the agent chat session itself (e.g., if the agent runs on a remote machine, its MCP servers run there too).

23. Certain tools that are inherently GUI-local (workspace GUI tools, entity DAL tools) always run **relative to the GUI**, regardless of the agent's executor.

24. The executor choice is encoded as the `$connection` descriptor embedded in the agent definition. `UserComputerProfileTransportFactory` handles the common case of "run on the machine associated with this profile entity."

### Migration / removal

25. The existing `ReverseRemoteChatClient`, `WebRemoteChatClient`, `RemoteAgentChatClient`, `ReverseTrustedExecutor`, `RemoteTrustedExecutor`, `ReverseFrame`, `IReverseMessageChannel`, `ReverseChannelConnection`, `ReverseConnectionAcceptor`, and `ReverseExecutionRegistry` are **replaced** by the new transport layer. The trusted executor abstraction (`ITrustedExecutor`) is rebuilt on top of `ITransport`.

26. The local `ITransport` makes it possible to use the same RPC and listener abstractions for both in-process and remote scenarios, removing any need for separate code paths.

27. **Multi-hop relay** — When two Phantom.Workspaces instances (B and C) are each registered with the same hub (A) via reverse devtunnel but have no direct network path to each other, Machine B must be able to open a transport connection to Machine C by routing through hub A. The transport abstractions on B and C must be identical to the direct-connection case — only the physical path changes.

---

## Open Questions — All Answered

| # | Question | Answer |
|---|---|---|
| Q1 | Lease reset trigger | Any message, including keepalives. Keepalive interval: **30 s**. |
| Q2 | `ReverseHttpTransport` multiplexing | **Multiplexed** over a single persistent connection, via the same `channel-id`/`stream-id` correlation pattern as the existing `ReverseFrame`+`CorrelationId` design. The existing single-connection protocol is rewritten into the new generic `TransportFrame` protocol. |
| Q3 | Back-pressure | **Unbounded** channels (`Channel<JsonElement>.CreateUnbounded()`). |
| Q4 | `HttpTransport` auth | **Dev Tunnel only** (`X-Tunnel-Authorization: tunnel <token>`). |
| Q5 | MCP connection schema `$id` | Schema already exists: `https://phantom-workspaces/schemas/mcp-server.json` in `Phantom.Workspaces.Llm.Core/JsonSchemas/mcp-server.json`. No new standalone file needed; the `McpTransportListener` request embeds a `"connection"` property referencing this schema's connection sub-type. |
| Q6 | `ChatClientTransportListener` wire protocol | See §Chat client wire protocol below. |
| Q7 | Tool call round-trips | Each tool specifies or is constructed with an executor context. GUI / entity DAL tools always route to the client GUI side. MCP tools use the agent's executor. Workspace-backend scheduled tools use the hosting Phantom.Workspaces instance. Tool calls are expressed as `FunctionCallContent` / `FunctionResultContent` within `ChatMessage.Contents` — no special tool-call frames traverse the chat channel. |
| Q8 | `LocalTransport` threading | **Background thread.** Individual tools that need the UI thread marshal themselves (e.g., via `ForegroundScheduler`). |
| Q9 | Versioning/negotiation | **None.** Same-version assumption across client and server. |
| Q10 | `ContainerTransportFactory` scope | **Deferred.** Not designed here, but all abstractions must not preclude it. |

---

## Phase 2 — Options

### O1: Extend the existing `ReverseFrame` protocol

Add `IMessageChannel` support by adding a new `channel-message` `ReverseFrame` type alongside the existing `execute`, `update`, `complete`, `open-stream`, `stream-data`, `stream-close`, `run-tool`, `run-tool-complete`, `register`, and `cancel` types.

**Pros:**
- Smaller diff; `ReverseChannelConnection`, `ReverseConnectionAcceptor`, and `ReverseExecutionRegistry` are preserved.
- Existing tests continue to pass.

**Cons:**
- `ReverseFrame` is typed to specific high-level operations (`RemoteAgentRequest`, `ChatResponseUpdate`, `TrustedToolRequest`). Generalizing to generic `JsonElement` payloads would require nullable union fields or a `JsonElement payload` catch-all, making the type messy.
- The `LocalTransport` scenario cannot share this protocol (no serialization, no HTTP, different dispatch path).
- Does not naturally support the `$local` / `$http` / `$reverse-http` connection-descriptor routing required by `ITransportFactoryRegistry`.
- The `ITrustedExecutor` / `TrustedExecutorSelector` layering cannot be cleanly removed; both the old and new abstractions would co-exist.

### O2: New generic `TransportFrame` protocol (same single-connection architecture)

Replace `ReverseFrame` with a new `TransportFrame` type carrying a `channel-id` or `stream-id` (GUID), a `type` discriminator, and a generic `JsonElement payload` or base64 binary `data` field. The `LocalTransport` does not serialise frames but uses the same logical interface.

**Pros:**
- Clean separation of transport-layer framing from application-layer protocol (MCP, chat client, shell).
- All three connection types (`local`, `http`, `reverse-http`) share the same `ITransport` / `IMessageChannel` / `Stream` interfaces.
- `ITrustedExecutor` and all old reverse-channel code can be fully removed once migration is complete.
- Enables future `ContainerTransportFactory` without any transport-layer changes.
- MCP, chat client, and shell all become ordinary listeners registered in `ITransportRegistry`, with no special cases in the server HTTP layer.

**Cons:**
- Larger rewrite surface; old and new code must coexist during migration.

**→ Chosen: O2.**

---

## Phase 3 — Chosen Design

### Core architectural decision

All transport scenarios — local in-process, forward HTTP to a remote instance, and reverse HTTP from a connecting instance — share the same `ITransport` / `IMessageChannel` / `Stream` interfaces. The physical wire format is handled per transport implementation; the application layer never sees it.

Multiple logical channels and streams are **multiplexed** over a single physical connection using a per-channel/stream GUID (`channel-id` / `stream-id`). This directly mirrors the existing `CorrelationId` pattern in `ReverseChannelConnection`.

### Connection lifetime and leasing

- Each `ITransport` on the server side maintains a **lease timer** that resets on every received frame (including `keepalive` frames).
- The client sends a `keepalive` frame every **30 seconds** while the transport is alive.
- If the server's lease timer fires (default: 90 s, i.e., three missed keepalives), all channels and streams vended by the transport are disposed, and the physical connection is closed.
- When the client explicitly disposes an `ITransport`, it sends a `transport-close` frame before closing the connection; the server disposes all associated channels/streams immediately.

### Wire protocol (`TransportFrame`)

All control and message frames are JSON text frames over WebSocket. Stream binary data uses the 5-byte binary framing `[kind: 1 byte][stream-id: 16 bytes LE][payload-length: 4 bytes BE][payload bytes]` over WebSocket binary frames.

> **Ordering and delivery guarantees.** The transport provides ordered, at-most-once delivery per channel. WebSocket guarantees in-order delivery of frames over a single connection. The relay pump on Machine A preserves frame order by reading and writing sequentially (no concurrent reads from the same channel). Applications may rely on these guarantees; no sequence numbers or deduplication are needed.

| Frame type | Direction | Fields | Meaning |
|---|---|---|---|
| `channel-open` | C→S | `channel-id`, `request: JsonElement` | Open a new message channel |
| `channel-message` | bidirectional | `channel-id`, `payload: JsonElement` | Message on an open channel |
| `channel-close` | bidirectional | `channel-id` | Close a channel (either side). The receiver drains any already-buffered `channel-message` frames before completing the reader; frames arriving after `channel-close` are discarded. |
| `channel-open-error` | S→C | `channel-id`, `error-code: string` (e.g. `"not-found"`, `"not-registered"`, `"unauthorized"`), `message: string` | Sent by the server when a `channel-open` request cannot be fulfilled. The client should treat the channel as never opened. |
| `stream-open` | C→S | `stream-id`, `request: JsonElement` | Open a new raw stream |
| `stream-data` | bidirectional | `stream-id`, binary frame (WS) | Binary chunk |
| `stream-close` | bidirectional | `stream-id` | Close a stream (either side) |
| `keepalive` | either | — | Resets server lease timer |
| `transport-close` | C→S | — | Client disposing; server tears down |

Reverse transport (B→A registration) uses the same frame set. When A sends `channel-open` back through the registration channel, the direction labels above are from B's (registering client's) perspective.

### Chat client wire protocol (Q6)

The `ChatClientTransportListener` handles `{ "type": "chat-client", "definition": ... }` requests and communicates over the `IMessageChannel`.

**Client → Server frames:**

| Type | Fields | Meaning |
|---|---|---|
| `process-streaming` | `content: ChatMessage` | Send user message; begin streaming response |
| `steering` | `content: ChatMessage` | Inject a steering message mid-turn |
| `interrupt` | — | Interrupt the in-progress streaming response |

**Server → Client frames:**

| Type | Fields | Meaning |
|---|---|---|
| `streaming-update` | `content: ChatResponseUpdate` | One update in the streaming response |
| `streaming-update-complete` | — | Response turn finished |
| `streaming-error` | `error: string` | Unrecoverable error during the turn |

> **Note:** The chat channel carries serialized JSON of `ChatMessage` (for complete turns) and `ChatResponseUpdate` (for streaming deltas). Tool calls and results are expressed as `FunctionCallContent` and `FunctionResultContent` within the `Contents` array — no special tool-call frames exist on the wire.

### Tool executor context

Tool execution context is determined at construction time, not at call time. Each tool is annotated with one of three execution targets:

| Target | Meaning | Examples |
|---|---|---|
| `agent-executor` | Same transport/executor as the agent chat | MCP servers, most LLM-facing tools |
| `gui-local` | Always executed in the GUI process | Workspace GUI tools, entity DAL tools |
| `hosting-instance` | Always executed in the Phantom.Workspaces server that hosts the workspace | Workspace-backend scheduled tools |

The `ChatClientTransportListener` message loop handles `process-streaming`, `steering`, and `interrupt` frames. Tool-call routing is handled at the AgentFramework level via `FunctionCallContent` / `FunctionResultContent` deserialization within `ChatMessage.Contents` — no special tool-call frames exist in the chat channel protocol.

---

## Phase 4 — Detailed Design

### New project: `Phantom.Workspaces.Transport`

All new transport-layer types live in a new `Phantom.Workspaces.Transport` project (or namespace). This isolates the wire protocol from Llm.Core and the GUI layer.

### Core interfaces

```csharp
// The unit of bidirectional JSON communication
public interface IMessageChannel : IAsyncDisposable
{
    ChannelWriter<JsonElement> Writer { get; }
    ChannelReader<JsonElement> Reader { get; }
}

// A connected transport: vends channels and streams
public interface ITransport : IAsyncDisposable
{
    Task<IMessageChannel> ConnectToMessageChannelAsync(JsonElement request, CancellationToken ct = default);
    Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default);
}

// Creates ITransport instances from a connection descriptor
public interface ITransportFactory : IAsyncDisposable
{
    // Returns null if this factory does not handle the descriptor
    Task<ITransport?> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default);
}

// Server-side listener: handles incoming channel/stream requests from one transport
public interface ITransportListener : IAsyncDisposable
{
    // Returns null if this listener does not handle the request
    Task<IAsyncDisposable?> OnChannelOpenAsync(JsonElement request, IMessageChannel channel, CancellationToken ct = default);
    Task<IAsyncDisposable?> OnStreamOpenAsync(JsonElement request, Stream stream, CancellationToken ct = default);
}

// Routes channel/stream open requests across registered listeners
public interface ITransportRegistry
{
    void Register(ITransportListener listener);
}

// Routes ConnectTo across registered factories
public interface ITransportFactoryRegistry
{
    void Register(ITransportFactory factory);
    Task<ITransport> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default);
}
```

### `TransportFrame` (replaces `ReverseFrame`)

```csharp
public sealed record TransportFrame
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("channel-id")]
    public string? ChannelId { get; init; }

    [JsonPropertyName("stream-id")]
    public string? StreamId { get; init; }

    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; init; }

    [JsonPropertyName("data")]
    public string? Data { get; init; }

    public static class Types
    {
        public const string ChannelOpen      = "channel-open";
        public const string ChannelOpenError  = "channel-open-error";
        public const string ChannelMessage = "channel-message";
        public const string ChannelClose   = "channel-close";
        public const string StreamOpen     = "stream-open";
        public const string StreamData     = "stream-data";
        public const string StreamClose    = "stream-close";
        public const string Keepalive      = "keepalive";
        public const string TransportClose = "transport-close";
    }
}
```

### `LocalTransport` / `LocalTransportFactory`

- `LocalTransport.ConnectToMessageChannelAsync` creates a linked pair of `Channel<JsonElement>` instances (unbounded), wraps them as an `IMessageChannel`, and calls `ITransportRegistry.OnChannelOpenAsync` on the local registry with the server side of the pair.
- `LocalTransport.ConnectToStreamAsync` creates a `Pipe`, passes the write end as the server's stream and the read end as the client's stream (plus vice versa for the other direction), and calls `ITransportRegistry.OnStreamOpenAsync`.
- All work happens on background threads (via `Task.Run` or `Channel` background pump tasks); UI-thread marshalling is the listener's responsibility.
- `LocalTransportFactory.ConnectToAsync` handles `{ "type": "local" }` descriptors and returns a new `LocalTransport`.

### `HttpClientTransportFactory` / `HttpTransport`

> **WebSocket only.** The transport uses WebSocket exclusively. There is no HTTP/2 NDJSON fallback. WebSocket is inherently bidirectional: `HttpClientTransportFactory` opens a connection as the WebSocket client (forward direction); `ReverseHttpServerTransportFactory` uses an already-registered WebSocket opened by the remote machine (reverse direction). Both paths yield the same `HttpTransport` object — the direction of connection establishment is transparent to all transport consumers.

- Handles `{ "type": "http", "url": "https://...", "target": $connection }` descriptors.
- On connect: opens a WebSocket to `wss://<url>/transport/connect`.
- If the outer descriptor has `"dev-tunnel-token"`, sets `X-Tunnel-Authorization: tunnel <token>` on the upgrade request.
- The resulting `HttpTransport` sends/receives `TransportFrame` JSON text frames (control) and WebSocket binary frames (stream data).
- `HttpTransport` runs a background read loop dispatching frames to registered `IMessageChannel`/`Stream` pairs by `channel-id`/`stream-id`.
- The read loop sends a `keepalive` frame every 30 s.
- If the WebSocket connection closes, all vended channels and streams are completed/faulted.
- Internally, `ConnectToAsync` resolves the `target` sub-descriptor (the inner `$local`/`$reverse-http`/etc.) by forwarding through the target host's `ITransportRegistry` — the `target` is embedded in the `channel-open` payload and interpreted by the server.

### `HttpServerTransportFactory`

- Replaces `ReverseEndpointRouteBuilderExtensions`.
- Exposes `GET /transport/connect` (WebSocket upgrade).
- On each new connection: creates a `ServerHttpTransport`, starts its read loop.
- The `ServerHttpTransport` read loop:
  - `channel-open`: extract `request` from payload, iterate registered `ITransportListener` instances until one returns non-null, associate the returned `IAsyncDisposable` with the `channel-id`. If no listener handles the request (all return null), the server sends `channel-open-error { error-code: "not-found" }` and does not open the channel.
  - `channel-message`: deliver to the inbound `ChannelWriter<JsonElement>` of the named channel.
  - `channel-close`: on receiving `channel-close`, the receiving end **drains** any already-buffered `channel-message` frames (delivers them to the listener in order) before completing the reader. Frames arriving after the `channel-close` itself are discarded. Then call `DisposeAsync` on the associated server-side object.
  - `stream-open`/`stream-data`/`stream-close`: same pattern for streams.
  - `keepalive`: reset the lease timer (90 s default).
  - `transport-close` or connection drop: dispose all associated channels and streams.
- Implements the server-side leasing timer.

#### `ServerHttpTransport`

- Created by `HttpServerTransportFactory` for each accepted WebSocket connection. Represents one active physical connection from one client.
- Owns a background read loop that processes `TransportFrame` frames from the connection.
- Maintains a dictionary of live `IMessageChannel` / `Stream` objects keyed by `channel-id` / `stream-id`.
- Maintains the server-side lease timer (90 s default); reset on any frame received. On lease expiry: sends `transport-close`, disposes all channels and streams.
- **Lifecycle:** Created on new connection accept → destroyed on `transport-close` frame, lease expiry, or physical connection drop.
- **Clients:** `HttpServerTransportFactory` (creates it); `ITransportListener` instances (called by its read loop for `channel-open`/`stream-open` frames).

### `ReverseHttpClientTransportFactory` / `ReverseHttpTransport`

- Handles `{ "type": "reverse-http", "entity-id": $entityId, "target": $connection }` descriptors.
- On `ConnectToAsync`: opens a `{ "type": "reverse-register", "entity-id": "...", "target": $connection }` `IMessageChannel` to the forward server via the registered `HttpClientTransportFactory`. This registration channel stays alive for the lifetime of the transport factory.
- Multiplexes all `ConnectToMessageChannelAsync`/`ConnectToStreamAsync` requests for the same server over that one registration channel by embedding `channel-open`/`stream-open` frames in the registration channel's messages.
- One `ReverseHttpClientTransportFactory` instance per remote Phantom.Workspaces server it connects to.
- When the remote GUI Phantom.Workspaces instance starts, it proactively calls `ConnectToAsync` with the hosting instance's `$http` descriptor to register itself.
- **Registration side-effect:** Each `ReverseHttpClientTransportFactory` instance manages **one slot** in the `hub-urls` list of this machine's user-computer-profile entity, keyed internally by the hub's entity-id. The list contains at most one URL per configured hub — it is bounded by configuration, not connection history. The `connection-descriptor` shape is:
  ```json
  { "type": "reverse-http", "hub-urls": ["https://A-devtunnel/...", "http://192.168.1.5:5000"], "entity-id": "<this-machine's-entity-id>" }
  ```
  Lifecycle rules:
  - **Connect:** On first successful registration with hub H, the factory **upserts** its URL into the list for hub H's slot. If no slot exists yet, one is created. The list grows to at most N entries where N equals the number of hubs this machine is configured to register with (typically 1).
  - **Reconnect:** If the devtunnel URL rotates and the factory re-registers with hub H, it **replaces** its existing slot in place. The list does not grow.
  > **Stale URL window during rotation.** When a devtunnel URL changes (reconnect), there is a brief window between `Disconnect` (slot removed) and the new connection's `Connect` (slot upserted) where the `hub-urls` list has one fewer entry. `ReverseHttpForwardingTransportFactory` races all listed URLs in parallel with a connect timeout of **10 seconds** per attempt. If a stale or missing URL causes one parallel attempt to time out or fail, the remaining URLs are still tried; the race succeeds as long as at least one URL resolves. Callers observe increased latency only, not an error, during rotation.
  - **Disconnect:** When the registration channel to hub H closes, the factory **removes** its slot from the list.
  - **Shutdown:** All slots are removed; `hub-urls` becomes empty (or the `connection-descriptor` field is cleared).
  - **Crash / ungraceful termination:** If the PW process terminates without running the Shutdown path, the `hub-urls` list retains stale entries in the entity store. These entries are harmless until someone tries to use them: `ReverseHttpForwardingTransportFactory` will connect to hub A successfully, but `ReverseHttpServerTransportFactory` relay behavior on A will find no live registration for the crashed machine and the relay channel-open will fail. This failure propagates as a transport error to the caller.
  - **Startup (crash recovery):** On startup, before performing any registrations, `ReverseHttpClientTransportFactory` **clears** the entire `hub-urls` list (sets it to empty) in the machine's user-computer-profile entity. This removes any stale crash-era entries. It then re-populates the list as new registrations succeed via the normal Connect path. This means there is a brief window at startup where `hub-urls` is empty — callers during this window will receive a transport error until at least one registration succeeds.

  The stored `hub-urls` list is the URL-only projection of these slots; slot keys (hub entity-ids) are held in memory only and are not persisted.

  > **Stale-entry semantics:** Because crash recovery clears the list at startup, stale entries only exist in the interval between a crash and the next restart. During that window, relay attempts to the crashed machine fail fast at the hub (no registration found) rather than timing out at the network level. There is no silent data corruption — a failed relay is always surfaced as a transport error.

#### Auto-reconnect

**Auto-reconnect.** `ReverseHttpClientTransportFactory` automatically reconnects when the physical WebSocket connection drops. Reconnect uses exponential backoff starting at 1 s, doubling up to a cap of 60 s, with jitter. There is no maximum retry count — the factory keeps trying indefinitely until the process shuts down.

On disconnect, all logical channels multiplexed over the dropped connection are faulted (their readers complete with an exception). Callers that need to re-establish a logical channel must call `ConnectToAsync` again after observing the fault — the transport does not automatically re-open logical channels.

The `hub-urls` slot for this hub is removed on disconnect and re-upserted on successful reconnect (see lifecycle section above).

### `ReverseHttpServerTransportFactory`

`ReverseHttpServerTransportFactory` is registered as a listener on every machine that runs `HttpServerTransportFactory`. It handles both incoming registrations (from machines connecting to register themselves) and relay requests (from machines wanting to route through this machine to reach a registered entity).

- Listens for `{ "type": "reverse-register" }` `channel-open` requests from `HttpServerTransportFactory`.
- Stores the registration `IMessageChannel` indexed by `entity-id`.
- When a client calls `ConnectToMessageChannelAsync` with `$reverse-http { entity-id }`, looks up the registration channel, sends a `channel-open` frame over it asking the reverse peer to open a channel to its local `ITransportRegistry`.
- The reverse peer's `ReverseHttpClientTransportFactory` receives this, routes to its own local listeners, and sends back messages on the opened channel.
- Replaces `ReverseExecutionRegistry` + `ReverseConnectionAcceptor`.

### `ReverseHttpClientTransportRegistry`

- Lives on **Machine B** (the machine that dials the reverse connection). Manages the collection of `ReverseHttpClientTransportFactory` instances — one per distinct remote server that Machine B has registered with.
- Implements `ITransportFactoryRegistry`. When code on Machine B calls `ConnectToAsync` with a `$reverse-http` descriptor targeting a server Machine B has registered with, routes to the appropriate `ReverseHttpClientTransportFactory`.
- **Creation:** Constructed at PW startup on Machine B. `ReverseHttpClientTransportFactory` instances are added to it as connections to remote servers are established.
- **Destruction:** Disposing disposes all contained `ReverseHttpClientTransportFactory` instances, closing all reverse-registration channels.
- **Clients:** `ITransportFactoryRegistry` (Machine B) — registered alongside `UserComputerProfileTransportFactory` and `LocalTransportFactory`. Also referenced by `HttpServerTransportFactory` note: the requirement text mentions it there, but that reference describes the client-side registry registered in the composite factory on Machine B, not a server-side concept.
- **Key method:** `void Register(ReverseHttpClientTransportFactory factory)` — adds a factory for a specific remote server.

### Reverse HTTP Transport

#### `ReverseHttpServerTransportFactory` — relay behavior

`ReverseHttpServerTransportFactory` has two responsibilities:

1. **Registration storage** — when Machine C connects to hub A at startup, `ReverseHttpClientTransportFactory` on C establishes a long-lived WebSocket to A. `ReverseHttpServerTransportFactory` on A stores the resulting `IMessageChannel` in an internal registry keyed by entity-id: `_registrations[C-guid]`.

2. **Relay on demand** — when a `channel-open { "type": "reverse-http", "entity-id": "<C-guid>" }` frame arrives from Machine B (via a separate WebSocket connection to A):
   1. Looks up `_registrations[C-guid]` to retrieve Machine C's existing registration `IMessageChannel`.
   2. If not found, sends `channel-open-error { "error-code": "not-registered" }` to Machine B and closes the channel.
   3. Starts a background **relay pump**: two concurrent tasks — one reading frames from B's channel and writing to C's registration channel; one reading frames from C's registration channel and writing to B's channel. The pump is byte-transparent: Machine A never parses frame contents.
   4. Returns the relay session as `IAsyncDisposable`. Disposing cancels both pump tasks and sends `channel-close` to both sides.

   No new `channel-open` is sent to Machine C. No new listener on Machine C is needed. Machine B's subsequent frames (including `channel-open { "type": "chat-client" }`, `channel-open { "type": "mcp" }`, etc.) are forwarded transparently through the relay pump to Machine C, where C's existing listeners handle them normally.

- **Crash propagation.** The relay pump runs two concurrent tasks — one reading from B and writing to C, one reading from C and writing to B. If either transport closes unexpectedly (hub crash, Machine C crash, or network drop), the affected read loop exits with an exception or EOF. The pump then:
  1. Cancels the other direction's task.
  2. Sends `channel-close` to the still-connected side.
  3. Disposes both `IMessageChannel` instances.
  4. Exits cleanly.

  The still-connected machine (B or C) receives a clean `channel-close` rather than a silent hang. The `ITransport` on that machine surfaces this as a normal channel-close event, which callers can observe and handle (e.g. by retrying via a new `ConnectToAsync`).

- **Lifecycle:** Created at hub startup, lives until hub shuts down. Each relay `channel-open` handled spawns one relay pump (owned by the relay session). Relay pump dies when either side closes its channel.
- **Key method:** `Task<IAsyncDisposable> OnChannelOpenAsync(ReverseHttpDescriptor descriptor, IMessageChannel fromB, CancellationToken ct)`
- **Clients:** `HttpServerTransportFactory` (dispatches `reverse-http` frames to this factory on the server side); the relay pump itself owns both `IMessageChannel` references.

#### `ReverseHttpForwardingTransportFactory`

**Lives on: all machines**

An `ITransportFactory` registered on every machine alongside `ReverseHttpServerTransportFactory`. Handles `{ "type": "reverse-http" }` descriptors on the **client side** (outgoing connections). When a machine needs to connect to an entity that registered with a remote hub, this factory routes through the hub URLs embedded in the descriptor.

The `reverse-http` descriptor shape used in user-computer-profile entities:
```json
{ "type": "reverse-http", "hub-urls": ["https://A-devtunnel/...", "http://192.168.1.5:5000"], "entity-id": "C-guid" }
```

The `hub-urls` field is written into Machine C's user-computer-profile entity by `ReverseHttpClientTransportFactory` instances when Machine C registers with hub Machine A. `ReverseHttpServerTransportFactory` ignores `hub-urls` on any machine that already has C's registration channel. `ReverseHttpForwardingTransportFactory` uses `hub-urls` to reach the hub on any machine that does not.

**Behavior of `ConnectToAsync({ "type": "reverse-http", "hub-urls": [...], "entity-id": "C-guid" })`:**
1. Races all hub-urls in parallel: for each url in hub-urls, concurrently calls
   `HttpClientTransportFactory.ConnectToAsync({ "type": "http", "url": url })`.
2. Uses the first HttpTransport that successfully connects; cancels and disposes
   the remaining in-flight connection attempts.
3. Opens a relay channel on the winning transport:
   `HttpTransport.ConnectToMessageChannelAsync({ "type": "reverse-http", "entity-id": "C-guid" })`.
   Machine A's `ReverseHttpServerTransportFactory` handles this frame, looks up C's registration, and starts a relay pump.
4. Returns the relay-backed `IMessageChannel` wrapped as `ITransport`.

- **Lifecycle:** Stateless factory. Each `ConnectToAsync` opens fresh parallel HTTP connection attempts to all known hub URLs, uses the first to succeed, and cancels the rest.
- **Key method:** `Task<ITransport> ConnectToAsync(ReverseHttpDescriptor descriptor, CancellationToken ct)`
- **Clients:** `ITransportFactoryRegistry` on any machine; receives descriptors from `UserComputerProfileTransportFactory`.

### `UserComputerProfileTransportFactory`

- Handles `{ "type": "user-computer-profile", "entity-id": $entityId }` descriptors.
- Resolves the user-computer-profile entity from the entity store by `entity-id`.
- Reads the entity's `connection-descriptor` field (a self-describing transport descriptor set at registration time) and calls `ITransportFactoryRegistry.ConnectToAsync(connection-descriptor)` directly — **no caller-identity check**. The descriptor encodes all routing information needed.

> **Local identity:** `UserComputerProfileTransportFactory` is constructed with a reference to `EntityRepository` (the process-lifetime singleton). It compares the entity-id in the connection descriptor against `EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId` — the ID resolved at startup by `WorkspaceEntitySessionBootstrapper`. If they match, the connection is local and routes to `LocalTransportFactory`. This is the same identity that appears throughout the rest of the system (shortcut handlers, meta-variable substitution, agent-session `trusted-executor` fields).

The three cases, determined entirely by what is stored in the entity:

| Entity `connection-descriptor` | Who stored it | Resolved by |
|---|---|---|
| `{ "type": "local" }` | Local machine startup | `LocalTransportFactory` |
| `{ "type": "http", "url": "https://C/..." }` | Machine C's own startup (forward-reachable) | `HttpClientTransportFactory` |
| `{ "type": "reverse-http", "hub-urls": ["https://A/...", ...], "entity-id": "C-guid" }` | `ReverseHttpClientTransportFactory` on Machine C when it registered with hub A | `ReverseHttpServerTransportFactory` (server-side, on the machine that has C's registration); `ReverseHttpForwardingTransportFactory` (client-side, on the machine initiating the connection) |

`UserComputerProfileTransportFactory` contains no routing logic of its own beyond entity resolution and `ConnectToAsync` dispatch. The right factory is chosen by whichever `ITransportFactoryRegistry` is local to the calling machine.

> **`target` field.** If the descriptor includes a `target` field, after reaching Machine B's transport endpoint, the factory forwards the `target` descriptor to Machine B's `ITransportFactoryRegistry` for further routing. This enables nested routing: e.g., reaching a local MCP server on Machine B from Machine A.

- **Key method:** `Task<ITransport> ConnectToAsync(UserComputerProfileDescriptor descriptor, CancellationToken ct)`
- **Lifecycle:** Stateless factory.
- **Clients:** `ITransportFactoryRegistry` on any machine; called from `AgentChat.InitializeAsync` via `ExecutionTargetResolver`.

### `ExecutionTargetResolver`

- Reads the `default-execution-target` field from a resolved `TrustProfile` and produces a `$connection` descriptor suitable for `ITransportFactoryRegistry.ConnectToAsync`.
- If `default-execution-target` is absent: returns `{ "type": "local" }`.
- If `default-execution-target` is `{ "type": "user-computer-profile", "entity-id": "..." }`: returns a `$user-computer-profile` descriptor (which `UserComputerProfileTransportFactory` then resolves further).
- If `default-execution-target` is already a concrete `$http` or `$local` descriptor: returns it directly.
- **Key method:** `JsonElement Resolve(TrustProfile? trustProfile)` → `$connection` descriptor.
- **Lifecycle:** Stateless; created per-call or as a singleton.
- **Clients:** `AgentChat.InitializeAsync` — called after `AgentTrustProfileResolver` resolves the trust profile, before calling `ITransportFactoryRegistry.ConnectToAsync`.

### `AgentTrustProfileResolver`

- Reads `metadata["trust-profile"]` from an `AgentDefinition` and resolves the referenced trust profile entity (or inline trust profile) into a runtime `TrustProfile` via `ITrustProfileProvider`.
- If `metadata["trust-profile"]` is absent: returns `null` (local execution assumed).
- If it is an entity reference `{ "$ref": { "entity-name": [...] } }`: resolves via `ITrustProfileProvider.ResolveAsync`, which fetches the entity and recursively composes base profiles.
- If it is an inline trust profile: composes directly via `TrustProfileComposer`.
- **Key method:** `Task<TrustProfile?> ResolveAsync(AgentDefinition definition, ITrustProfileProvider provider, CancellationToken ct)`.
- **Lifecycle:** Stateless; created per-call or as a singleton.
- **Clients:** `AgentChat.InitializeAsync` (step 3); `AgentFactory.EnforceTrustProfileAsync` (validates the resolved profile's allowed client instances against the current machine before session construction).

### Transport listeners

#### `ShellTransportListener`

- Handles `{ "type": "shell", ... }` `stream-open` requests.
- Parses shell parameters (command, mode, working-directory, environment) from a new standalone `shell-parameters.json` JSON schema (existing shell entity schema references it via `allOf`).
- Spawns a PTY or pipe process, relays stdio to/from the `Stream`.
- Returns an `IAsyncDisposable` that kills the process on disposal.
- Replaces `LocalTrustedExecutor.HandleStreamAsync` shell path + `/stream/open` HTTP route.

#### `McpTransportListener`

- Handles `{ "type": "mcp", "connection": { /* mcp-server.json connection sub-object */ } }` `channel-open` requests.
- Wraps the `IMessageChannel` as an `IClientTransport` (using the existing `DelegatingMcpServer` pattern), connects to the MCP server (stdio or HTTP), and bridges the protocols.
- Returns an `IAsyncDisposable` representing the MCP server session.
- Replaces direct stdio/HTTP connection in `McpToolContextProvider`.

#### `ChatClientTransportListener`

- Handles `{ "type": "chat-client", "definition": { /* agent definition */ }, "mcp-servers": [...] }` `channel-open` requests.
- Each entry in `mcp-servers` uses the **`mcpTool` sub-schema from `AgentDefinition`** (the same schema used to declare MCP servers in agent definitions), extended with an `execution-target` field that specifies which machine hosts the MCP server:

  ```json
  {
    "type": "mcp",
    "name": "workspace-tools",
    "connection": {
      "endpoint": "http://localhost:…"
    },
    "execution-target": "<user-computer-profile-entity-id>"
  }
  ```

  `ChatClientTransportListener` reads `execution-target` to route the MCP connection through `ITransportFactoryRegistry` to the correct machine. When `execution-target` matches the local machine's `WorkspaceEntitySession.UserComputerProfileEntityId`, the MCP server connection is opened locally. Otherwise it is opened via whatever transport the registry resolves for that entity.
- Resolves the `IChatClient` via `AgentFactory.CreateChatClient`.
- Opens all `McpClientOverTransport` instances from the `mcp-servers` array in the channel-open request.
- Runs a message loop on the `IMessageChannel`:
  - `process-streaming` → calls `chatClient.GetStreamingResponseAsync`, emits `streaming-update` frames, completes with `streaming-update-complete`.
  - MCP tool calls: The executor (Machine B) has `McpClientOverTransport` instances already open (from the `mcp-servers` array). The `IChatClient` on Machine B uses MCP directly — **no `tool-call` frames traverse the chat channel**.
  - `steering` → injects a steering message into the in-progress turn.
  - `interrupt` → cancels the streaming turn.
- Returns a `ChatClientTransportSession` (implements `IAsyncDisposable`) wrapping the session.
- Replaces `AgentRespondHandler`, `RemoteAgentChatClient` endpoint, `WebRemoteChatClient`, `ReverseRemoteChatClient`.

#### `ChatClientTransportSession`

- The `IAsyncDisposable` returned by `ChatClientTransportListener.OnChannelOpenAsync`. Represents the server-side lifetime of one agent chat session over a transport channel.
- Owns: the `IChatClient` created by `AgentFactory.CreateChatClient`, all `McpClientOverTransport` instances opened from the `mcp-servers` descriptors in the channel-open request, and the background message-pump task.
- **Lifecycle:** Created when `ChatClientTransportListener` accepts a `channel-open` request → destroyed when the `IMessageChannel` is closed (client disposes `ChatClientOverTransport`), the lease expires, or `DisposeAsync` is called explicitly.
- On disposal: cancels the in-progress streaming turn (if any), disposes the `IChatClient`, closes all MCP client sessions.
- **Clients:** `ChatClientTransportListener` (creates it and returns it as the session handle); `ServerHttpTransport` / `LocalTransport` (holds reference for lease and disposal).

### Client-side adapters

#### `ChatClientOverTransport`

```csharp
public sealed class ChatClientOverTransport : IChatClient
{
    public ChatClientOverTransport(ITransport transport, JsonElement chatClientRequest) { ... }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IList<ChatMessage> messages,
        ChatOptions? options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Opens IMessageChannel if not already open
        // Sends { "type": "process-streaming", "content": messages }
        // Reads streaming-update frames, yields ChatResponseUpdate
        // Tool-call routing is handled at the AgentFramework level via Contents deserialization (no tool-call frames on the wire)
        // On streaming-update-complete: completes enumeration
    }
}
```

#### `McpClientOverTransport`

- Wraps `ITransport.ConnectToMessageChannelAsync` with an MCP connection request.
- Presents the `IMessageChannel` as an `IClientTransport` to the MCP SDK (replacing the `HttpClientTransport` or `StdioClientTransport`).
- Replaces direct connections in `McpToolContextProvider`.

#### `ShellOverTransport`

- Wraps `ITransport.ConnectToStreamAsync` with a shell request.
- Returns a `Stream` + `StreamMessageChannelStream` adapter for PTY control frames.
- Replaces `WebRemoteStreamClient` + `StreamMessageChannelStream` wiring in `StartShellFromEntityShortcutHandler`.

#### `CopilotSubAgentRouterMiddleware`

- `IChatClient` middleware that wraps an inner `IChatClient` (typically `ChatClientOverTransport` for remote, or `LocalTransport`-backed `ChatClientOverTransport` for local) and intercepts the `IAsyncEnumerable<ChatResponseUpdate>` stream to route sub-agent lifecycle events.
- Applied by `AgentFactory.CreateChatClient` when provider is `github-copilot` or `github-copilot-subagent`.
- **Key methods:**
  - `CopilotSubAgentRouterMiddleware(IChatClient inner, IRunningAgentChatFactory factory, ISubAgentTable subAgentTable, ISubAgentChatRegistry? registry, ILogger? logger)`
  - `IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IList<ChatMessage> messages, ChatOptions? options, CancellationToken ct)` — creates a `CopilotSubAgentRouter`, delegates to `inner.GetStreamingResponseAsync`, pipes the update stream through `CopilotSubAgentRouter.RouteUpdatesAsync`, yields root updates to caller.
  - `object? GetService(Type serviceType)` — delegates to inner.
- **Behavior:** When a `FunctionCallContent("copilot.subagent.start")` update arrives: calls `IRunningAgentChatFactory.CreateAsync(SubAgentDefinition, newSessionId)` → creates a new `AgentChat` entity on Machine A; registers with `ISubAgentTable`. Subsequent updates tagged with `parent_tool_call_id` are routed to the sub-agent's `ICopilotSubAgentReceiver`. `FunctionResultContent` signals completion state.
- **Lifecycle:** Created during `AgentChat.InitializeAsync`; registered as an owned resource. Disposed when the `AgentChat` is disposed.
- **Clients:** `AgentChat` (owns it as the outermost `IChatClient` wrapper for Copilot SDK providers); `StreamingPersistenceMiddleware` wraps it.
- **Prerequisite:** Further refactoring of `CopilotSdkChatClient` beyond issue #808 branch — `CopilotSdkChatClient` must yield the `CopilotSdkStreamAdapter`-translated stream directly without internal routing. See Scenario 3 for the full context.

### Removals (files to delete after migration)

| File | Replaced by |
|---|---|
| `Llm.Core/Trust/ReverseFrame.cs` | `Transport/TransportFrame.cs` |
| `Llm.Core/Trust/IReverseMessageChannel.cs` | `IMessageChannel` (+ `LocalTransport` for in-proc) |
| `Llm.Core/Trust/ReverseChannelConnection.cs` | `HttpTransport` + `ReverseHttpServerTransportFactory` |
| `Llm.Core/Trust/ReverseConnectionAcceptor.cs` | `ReverseHttpServerTransportFactory` |
| `Llm.Core/Trust/ReverseExecutionRegistry.cs` | `ReverseHttpServerTransportFactory` |
| `Llm.Core/Trust/ReverseExecutionWorker.cs` | `ReverseHttpClientTransportFactory` |
| `Llm.Core/Trust/ReverseTrustedExecutor.cs` | `ChatClientOverTransport` + `ReverseHttpTransport` |
| `Phantom.Workspaces/Trust/WebRemoteChatClient.cs` | `ChatClientOverTransport` |
| `Phantom.Workspaces/Trust/RemoteAgentChatClient.cs` | `ChatClientOverTransport` |
| `Phantom.Workspaces/Trust/RemoteTrustedExecutor.cs` | `ChatClientOverTransport` + `HttpTransport` |
| `Phantom.Workspaces/Trust/WebRemoteStreamClient.cs` | `ShellOverTransport` |
| `Web.Server/AgentRespondHandler.cs` | `ChatClientTransportListener` + new HTTP endpoint |

---

## Phase 5 — Implementation Plan

Sub-items are ordered by dependency. Items at the same level may be done in parallel.

> **Note:** This plan will be filed as a master bug with sub-item bugs in Phantom.Workspaces.

### Level 1 — Core abstractions (no dependencies)

**T1: Transport layer interfaces + `TransportFrame`**
- New project `Phantom.Workspaces.Transport` (or namespace in `Llm.Core`).
- `IMessageChannel`, `ITransport`, `ITransportFactory`, `ITransportListener`, `ITransportRegistry`, `ITransportFactoryRegistry`.
- `TransportFrame` record with all frame types.
- `TransportFactoryRegistry`, `TransportRegistry` (iterating implementations).
- `InProcessTransport` — creates a matched pair of in-memory `ITransport` instances connected via `Channel<TransportFrame>` (one channel per direction). Call `InProcessTransport.Create()` to get `(ITransport server, ITransport client)`. Replaces both the old "InMemoryTransport" concept and the "InProcessTransportPair" concept — they are the same thing.

### Level 2 — `LocalTransport` (depends on T1)

**T2: `LocalTransport` / `LocalTransportFactory`**
- `LocalTransport.ConnectToMessageChannelAsync` → `Channel<JsonElement>` pair, dispatch to local registry.
- `LocalTransport.ConnectToStreamAsync` → `Pipe` pair.
- `LocalTransportFactory` handles `{ "type": "local" }`.

### Level 3 — HTTP wire protocol (depends on T1)

**T3: `HttpClientTransportFactory` / `HttpTransport`**
- WebSocket (`/transport/connect`) physical connection.
- Background read loop, channel/stream dispatch by id.
- Keepalive frame every 30 s.
- Dev Tunnel token support (`X-Tunnel-Authorization`).

**T4: `HttpServerTransportFactory`** (depends on T3)
- Replaces `ReverseEndpointRouteBuilderExtensions`.
- Server-side lease timer (90 s).
- `channel-open` routing to `ITransportRegistry`.
- Replaces `/reverse/connect` with `/transport/connect`.

### Level 4 — Reverse HTTP transport (depends on T3, T4)

**T5: `ReverseHttpClientTransportFactory` / `ReverseHttpTransport`**
- Registration channel opened via `HttpClientTransportFactory`.
- Multiplexes channel/stream requests over one physical connection.
- Proactive registration at startup.
- Writes and maintains the `hub-urls` array in the `user-computer-profile` entity descriptor: upsert on connect, replace-in-place on reconnect, remove on disconnect, clear-all on shutdown, clear-before-register on startup (crash recovery).
- `ReverseHttpClientTransportRegistry` — the `ITransportFactoryRegistry` implementation on machines that register with remote hubs; holds all registered `ReverseHttpClientTransportFactory` instances (one per configured hub); dispatches `ConnectToAsync` to the appropriate factory based on the descriptor's `entity-id`; initialized at startup from `WorkspacesConfiguration`.

**T6: `ReverseHttpServerTransportFactory`** (depends on T4)
- Registration channel listener.
- Entity-id indexed connection map.
- Handles `{ "type": "reverse-http" }` descriptors.

### Level 4b — Relay transport (depends on T4, T5, T6)

**T6b: `ReverseHttpServerTransportFactory` relay behavior** (depends on T4, T6)

- Add relay handling to `ReverseHttpServerTransportFactory`: when a `channel-open { "type": "reverse-http", "entity-id": "<C-guid>" }` arrives from a non-registering caller (Machine B), look up `_registrations[C-guid]`, start a byte-transparent bidirectional relay pump, and return the relay session as `IAsyncDisposable`.
- On missing registration: send `channel-open-error { "error-code": "not-registered" }`.
- Relay pump tears down when either side closes its channel (crash propagation as described in Phase 4).
- Tests: relay pump routes frames B→C and C→B byte-transparently; missing registration returns `channel-open-error`.

**T6c: `ReverseHttpForwardingTransportFactory`** (depends on T3, T6b)
- Registered on all machines alongside `ReverseHttpServerTransportFactory` for `{ "type": "reverse-http" }` descriptors; handles the client side (outgoing connections).
- `ConnectToAsync` races all `hub-urls` in parallel via `HttpClientTransportFactory`; first successful connection wins, rest are cancelled and disposed.
- Opens a relay channel to the target: `channel-open { "type": "reverse-http", "entity-id": "<target>" }` on the winning hub connection.
- Returns the relay-backed `ITransport` to the caller.

**T6d: Integration test infrastructure** (depends on T6b)
- New project `Phantom.Workspaces.Transport.Tests`.
- `InProcessTransport` — created via `InProcessTransport.Create()`, which returns `(ITransport server, ITransport client)`; simulates a cross-machine link without network.
- `InProcessHttpServerTransportFactory` — accepts `InProcessTransport.Create().server` connections directly via `AcceptAsync(ITransport)` instead of a real TCP port.
- `InProcessReverseHubFixture` — wires `InProcessHttpServerTransportFactory` + `ReverseHttpServerTransportFactory` (including relay behavior) together in-process; exposes `SimulateClientRegistrationAsync(Guid machineEntityId)`.
- No scenario tests yet (T10 not yet complete).

### Level 5 — Profile routing (depends on T2, T5, T6, T6b, T6c)

**T7: `UserComputerProfileTransportFactory`**
- Entity lookup; local/reverse/forward determination.
- Re-dispatches through `ITransportFactoryRegistry`.
- Replaces `TrustedExecutorSelector` routing.
- `ExecutionTargetResolver` — reads `default-execution-target` from the agent's trust profile; resolves the target entity-id using `WorkspaceEntitySession`; returns the resolved `ITransportFactory` target for the session.
- `AgentTrustProfileResolver` — reads the trust profile entity for a given agent session; validates `allowed-client-instances` and `denied-client-instances` against the requesting machine's entity-id.

### Level 6 — Transport listeners (depends on T2, T5, T6)

**T8: `ShellTransportListener` + `ShellOverTransport`**
- Standalone `shell-parameters.json` schema.
- PTY/pipe process spawning.
- Replaces `LocalTrustedExecutor.HandleStreamAsync` + `WebRemoteStreamClient` + `/stream/open` route.

**T9: `McpTransportListener` + `McpClientOverTransport`**
- `DelegatingMcpServer`-based bridging.
- Replaces direct stdio/HTTP connection in `McpToolContextProvider`.
- MCP servers now use the agent's executor's `ITransport`.

**T10: `ChatClientTransportListener` + `ChatClientOverTransport`**
- Full Q6 wire protocol.
- Client-side tool routing (gui-local vs agent-executor vs hosting-instance).
- Replaces `AgentRespondHandler`, `WebRemoteChatClient`, `ReverseRemoteChatClient`, `RemoteAgentChatClient`.
- `CopilotSubAgentRouterMiddleware` — wraps the resolved `IChatClient` on the hub machine; intercepts sub-agent lifecycle events from the Copilot SDK stream and routes them to the correct `AgentChat` sink on Machine A; required for Scenarios 3, 4, and 5.

### Level 7 — Trusted executor rebuild (depends on T7, T8, T9, T10)

**T11: `TransportTrustedExecutor` adapter + remove old implementations**
- Delete old `ITrustedExecutor` implementation files (`LocalTrustedExecutor`, `WebRemoteTrustedExecutor`, `ReverseTrustedExecutor`, `RemoteTrustedExecutor`) listed in the Removals table — **do not delete the `ITrustedExecutor` interface itself**.
- Add `TransportTrustedExecutor`: a thin adapter that implements `ITrustedExecutor` by delegating to `ITransportFactoryRegistry`. This is the sole remaining implementation of the interface after this task.
- Update `AgentServices` / DI registration to bind `ITrustedExecutor` → `TransportTrustedExecutor`.
- Update `StartShellFromEntityShortcutHandler` to use `ShellOverTransport`.
- Update `McpToolContextProvider` to use `McpClientOverTransport`.
- Update agent chat construction to use `ChatClientOverTransport`.

### Level 8 — MCP executor context (depends on T11)

**T12: Per-tool executor context**
- Add `ExecutorTarget` annotation to tool construction.
- Tool-call routing is handled at the AgentFramework level via `FunctionCallContent` / `FunctionResultContent` deserialization within `ChatMessage.Contents`; no special tool-call frames exist in the chat channel.
- Ensure MCP tool calls use `agent-executor`; GUI/entity tools use `gui-local`.

### Level 9 — Integration tests (depends on T6d, T10, T12)

**T13: Transport integration tests — Scenarios 1–4**  (depends on T6d, T10)
- `Scenario1_LocalOpenAiTests.cs` — `LocalTransport` → `ChatClientTransportListener` → `DeterministicTestChatClient`; tool call round-trip via in-process MCP.
- `Scenario2_RemoteOpenAiTests.cs` — `InProcessReverseHubFixture` + `ReverseHttpServerTransportFactory`; full A→B→A turn via `DeterministicTestChatClient`.
- `Scenario3_RemoteCopilotSdkTests.cs` — same transport as Scenario 2; executor machine resolves chat client via `AgentFactory.CreateChatClient(definition)` with `provider: "github-copilot"` BYOK endpoint → `ScriptedByokChatServer` → `DeterministicTestChatClient`.
- `Scenario4_LocalCopilotSdkTests.cs` — all-local transport; same BYOK `AgentFactory.CreateChatClient` pattern as Scenario 3.
- `LeaseExpiryTests.cs` — fake timer advanced past 90 s mid-turn; assert clean cancellation.

**T14: Transport integration tests — Scenario 5 + error paths** (depends on T6d, T6c, T10)
- `Scenario5_HubRelayTests.cs` — Machine B uses `ReverseHttpForwardingTransportFactory`; Machine C registered with fixture hub via `SimulateClientRegistrationAsync`; Machine C chat client resolved via `AgentFactory.CreateChatClient` with BYOK `ScriptedByokChatServer`; assert frames travel B → relay pump on A → C byte-transparent.
- `RelayErrorTests.cs` — relay target not registered → `channel-open-error` → `TransportException` on B.
- `HubUrlFallbackTests.cs` — one hub URL fails → falls back to second; all URLs fail → throws within bounded timeout.
- Machine C crash (stale `hub-urls`) → `ReverseHttpServerTransportFactory` relay behavior returns error; on restart `hub-urls` is cleared.

---

## Trusted Executor Design — Detailed Scenarios

This section defines the complete design for how trust profiles, execution locations, and tool routing work together. It supersedes the high-level notes in Phase 4 and is the authoritative design for the trusted executor refactor.

---

### Terminology

| Term | Definition |
|---|---|
| **Hosting instance (H)** | The Phantom.Workspaces server that owns the workspace and entity database. Entity tools and workspace-backend scheduled tools must run here. |
| **GUI instance (G)** | The PW instance presenting the Avalonia UI. Workspace GUI tools (entity DAL operations, workspace pane control, etc.) must run here. In the common single-machine topology, G == H. |
| **Executor instance (E)** | The machine where LLM API calls are made and stdio MCP servers are spawned. Specified by the agent trust profile. In the local topology, E == G == H. In the remote topology, E is a separate user-computer-profile machine. |

---

### Trust Profile Role (Refined)

A trust profile entity specifies **capability constraints** — what a thing is allowed to do and where it is allowed to run. It answers the question "what is permitted?" not "where must this run?" The latter is the execution class (see below).

The trust profile continues to carry:
- `allowed-client-instances` — the set of machines the agent / MCP server is allowed to run on (`"."` = local, `"<guid>"` = specific user-computer-profile, `"*"` = any).
- `network-access-policy`, `mount-points`, `https-proxy-policy` — container-level rights for future container support.
- `allowed-mcp-tool-call-schemas` / `restricted-mcp-tool-call-schemas` — which MCP tool call payloads are allowed.

**New addition:** Trust profiles gain an optional `default-execution-target` field specifying the preferred execution target within the permitted machines:

```json
{
  "entity-types": ["llm-trust-profile"],
  "default-execution-target": {
    "type": "user-computer-profile",
    "entity-id": "<Machine-B-GUID>"
  },
  "allowed-client-instances": ["<Machine-B-GUID>"],
  "network-access-policy": "host-network"
}
```

When `default-execution-target` is absent, the hosting PW instance (`"."`) is used. When `default-execution-target` points to a `user-computer-profile`, `UserComputerProfileTransportFactory` resolves the connection descriptor at runtime.

---

### Execution Classes

Every tool, MCP server, and agent chat is annotated with an **execution class** that determines which instance in the topology runs it:

| Execution class | Where it runs | Determined by |
|---|---|---|
| `agent-executor` | The executor instance E, as resolved from the agent trust profile | MCP tools; defaults for most agent-defined tools |
| `gui-local` | The GUI instance G (the machine that initiated the agent chat session) | Hard-coded for workspace GUI tools, entity DAL tools, workspace pane tools |
| `hosting-instance` | The hosting instance H (the PW server that owns the workspace) | Hard-coded for workspace-backend scheduled tools, agent session tools |

In the common single-machine topology (G == H == E), all three classes resolve to the same machine and no round-trips are needed.

The execution class is **not** stored in a trust profile entity. It is a property of the tool kind:
- `{ "kind": "workspace-gui" }` → implicit class `gui-local`
- `{ "kind": "workspace-entity" }` → implicit class `gui-local` (routed via G which has entity DAL access)
- `{ "kind": "workspace-agent-session" }` → implicit class `hosting-instance`
- `{ "kind": "mcp", ... }` → implicit class `agent-executor`
- `{ "kind": "function", ... }` → explicit via construction; defaults to `agent-executor`

---

### Manifest Structure

#### Agent definition

```json
{
  "name": "My Agent",
  "model": "gpt-4o",
  "metadata": {
    "trust-profile": "my-remote-workstation"
  },
  "tools": [
    {
      "kind": "mcp",
      "serverName": "my-tools",
      "connection": { "kind": "stdio", "endpoint": "stdio://my-tool-server?command=my-tool-server" }
    },
    {
      "kind": "mcp",
      "serverName": "special-db-server",
      "connection": { "kind": "http", "endpoint": "http://localhost:5050/mcp" },
      "trust-profile": "my-db-server-profile"
    },
    { "kind": "workspace-gui" },
    { "kind": "workspace-entity" }
  ]
}
```

- `metadata["trust-profile"]` → name of trust profile entity → resolved to an execution target for the **agent chat session itself** and the default MCP servers.
- Each `mcp` tool may carry its own `"trust-profile"` override. This causes that MCP server to open on a different machine than the agent's default executor.
- `workspace-gui` and `workspace-entity` tools carry no trust-profile reference; their execution class is hard-coded to `gui-local`.

#### Trust profile entity (example)

```json
{
  "entity-types": ["llm-trust-profile"],
  "names": ["llm-trust-profiles", "my-remote-workstation"],
  "default-execution-target": {
    "type": "user-computer-profile",
    "entity-id": "a1b2c3d4-..."
  },
  "allowed-client-instances": ["a1b2c3d4-..."],
  "network-access-policy": "host-network",
  "allowed-mcp-tool-call-schemas": [{ ... }]
}
```

#### Shell entity

Shells continue to use the `user-computer-profile` entity as their implicit trust profile. The entity-id of the profile IS the client-instance-id. No separate trust profile entity is needed for shells.

---

### Tool Resolution at Session Start

When an agent chat session is constructed:

1. **Resolve trust profile** — `AgentTrustProfileResolver.ResolveAsync` reads `metadata["trust-profile"]`, fetches and composes the trust profile entity chain.
2. **Determine execution target** — Extract `default-execution-target` from the trust profile → yields a `$connection` descriptor for `ITransportFactoryRegistry`.
3. **Open chat-client channel** — `ITransportFactoryRegistry.ConnectToAsync($connection)` → `ITransport` → `ITransport.ConnectToMessageChannelAsync({ "type": "chat-client", "definition": <agentJson> })` → `IMessageChannel`.
4. **Resolve MCP servers** — For each `mcp` tool:
   a. Resolve the MCP tool's own trust profile (or inherit agent's).
   b. Derive a `$connection` descriptor for the MCP server's execution target.
   c. This descriptor is embedded in the `{ "type": "mcp", "connection": ... }` channel-open request, sent over the chat-client channel or directly via a separate `ConnectToMessageChannelAsync` on the executor's `ITransport`.
5. **Open `McpClientOverTransport`** — For each tool, open a `McpClientOverTransport` over the appropriate `ITransport` for its execution target. Standard MCP protocol handles all tool calls; no separate `tool-call` frames exist in the chat channel.
6. **Wrap with routing middleware** — If provider is `github-copilot` or `github-copilot-subagent`, wrap `ChatClientOverTransport` with `CopilotSubAgentRouterMiddleware`.

---

## Per-Scenario: Manifest → Running AgentChat

This section traces each scenario end-to-end: what the agent-manifest entity looks like in the PW entity store, how it becomes an `AgentDefinition`, what the factory does, and how `AgentChat.InitializeAsync` produces a live session. Steps marked **[NEW]** are part of the transport layer design and do not exist in the current codebase.

Agent manifests are **not** user-authored YAML files. They are `agent-manifest` entities stored in the PW entity store (MongoDB). Each entity carries a `manifest` field containing a declarative agent description: a `template` (the base `AgentDefinition`, with `${paramName}` placeholders if parameters are defined), `parameters` (what the user fills in at launch), and `resources` (tool references that `ToolResourceFactory` resolves into concrete `Tool` objects at session-start time). Tools are **not** listed in the `template` — they are appended after `resources` are resolved.

---

### Scenario 1 — Local agent, OpenAI endpoint

**Topology:** G == H == E = Machine A. One Phantom.Workspaces process. OpenAI REST API is called outbound from Machine A.

#### Agent manifest entity (in PW entity store)

```json
{
  "entity-id": "a1b2c3d4-e5f6-7890-ab12-cd34ef567890",
  "entity-types": ["entity", "agent-manifest"],
  "names": [["agent-manifests", "my-openai-agent"]],
  "display-name": { "default": "My OpenAI Assistant" },
  "manifest": {
    "name": "my-openai-agent",
    "displayName": "My OpenAI Assistant",
    "description": "Answers questions and can read entities and open tabs.",
    "template": {
      "kind": "prompt",
      "name": "my-openai-agent",
      "model": {
        "id": "gpt-4o",
        "provider": "openai",
        "apiType": "OpenAI",
        "connection": { "kind": "key", "apiKey": "${OPENAI_API_KEY}" },
        "options": { "temperature": 0.2 }
      },
      "instructions": "You are a helpful assistant.",
      "metadata": {}
    },
    "resources": [
      { "kind": "tool", "id": "fixed", "name": "workspace-gui" },
      { "kind": "tool", "id": "fixed", "name": "workspace-entity" },
      { "kind": "tool", "id": "mcp-server-entity", "name": "my-local-tools" }
    ]
  }
}
```

No `template.metadata["trust-profile"]` → local execution on Machine A. Tools are not in the `template`; they come from `resources` and are resolved at session-start time.

#### User workflow

1. User opens the `agent-manifest` entity in PW (e.g., double-click in entity browser → triggers `OpenAgentManifestShortcutHandler`).
2. PW shows the **AgentManifestLaunchpad** view with parameter input fields from `manifest.parameters`. If the manifest defines no parameters, the session starts immediately without showing the launchpad UI.
3. User fills in any required parameters and clicks **Start Session**.
4. `CreateAgentSessionEntityAsync(manifestEntity, agentSessionId, parameterValues)` → creates an `agent-session` entity in MongoDB.
5. `AgentManifestLoader.LoadManifestFromJson(manifest-field-JSON)` → `AgentManifest`.
6. `AgentFactory.CreateAgentChatAsync({ AgentManifest, Parameters, ToolResourceFactory, AgentSessionId, AgentServices, ForegroundScheduler })`.
7. All execution happens on **Machine A** (single machine). The OpenAI REST API is called outbound from Machine A.
8. User types a message in the agent chat UI → `AgentChat.EnqueueUserMessage(text)`.

#### Materialized `AgentDefinition` (after `CreateAgentDefinitionAsync`)

`CreateAgentDefinitionAsync` clones/substitutes the `template`, then calls `ToolResourceFactory.ResolveToolResourceAsync` for each entry in `resources` and appends the resulting `Tool` objects:

- `{ "id": "fixed", "name": "workspace-gui" }` → built-in workspace-gui tool
- `{ "id": "fixed", "name": "workspace-entity" }` → built-in workspace-entity tool
- `{ "id": "mcp-server-entity", "name": "my-local-tools" }` → fetches the `mcp-server` entity named "my-local-tools" from the entity store → builds MCP connection tool

```json
{
  "kind": "prompt",
  "name": "my-openai-agent",
  "model": {
    "id": "gpt-4o",
    "provider": "openai",
    "apiType": "OpenAI",
    "connection": { "kind": "key", "apiKey": "<resolved>" },
    "options": { "temperature": 0.2 }
  },
  "instructions": "You are a helpful assistant.",
  "metadata": {},
  "tools": [
    { "kind": "workspace-gui" },
    { "kind": "workspace-entity" },
    { "kind": "mcp", "serverName": "my-local-tools",
      "connection": { "kind": "stdio", "command": "my-tool-server", "args": ["--mcp"] } }
  ]
}
```

Tools are now present — they were appended by `ToolResourceFactory` resolving the `resources` list. They are **not** present in the `template` field of the entity.

#### Factory steps (`AgentFactory.CreateAgentChatAsync`)

1. `AgentManifest` provided → call `CreateAgentDefinitionAsync` (parameter substitution + resource resolution) → returns resolved `AgentDefinition` (above).
2. `EnforceTrustProfileAsync(resolvedDefinition, trustProfileProvider)`: no trust profile → passes validation.
3. `AgentChat.CreateAsync(InternalCreateAgentChatRequest { AgentDefinition, AgentSessionId, AgentServices, ForegroundScheduler, ... })`.

#### `AgentChat.InitializeAsync` steps

```
1.  RestoreAsync(agentSessionId)
      → No prior session: restoredAgent = null

2.  Resolve AgentDefinition
      → Use request.AgentDefinition (already fully resolved: tools appended by CreateAgentDefinitionAsync)

3.  [NEW] AgentTrustProfileResolver.ResolveAsync(definition, trustProfileProvider)
      → metadata["trust-profile"] absent → trustProfile = null
      → Effective execution target: { "type": "local" }

4.  [NEW] ExecutionTargetResolver.Resolve(trustProfile)
      → $connection = { "type": "local" }

5.  [NEW] ITransportFactoryRegistry.ConnectToAsync({ "type": "local" })
      → LocalTransportFactory matches
      → Returns LocalTransport (in-process channel pair, no network)

6.  [NEW] LocalTransport.ConnectToMessageChannelAsync({
          "type": "chat-client",
          "definition": <agentDefinitionJson>,
          "mcp-servers": [
            { "type": "mcp", "name": "workspace-gui",    "connection": { "endpoint": "local://workspace-gui-listener" },    "execution-target": "<Machine-A-profile-entity-id>" },
            { "type": "mcp", "name": "workspace-entity", "connection": { "endpoint": "local://workspace-entity-listener" }, "execution-target": "<Machine-A-profile-entity-id>" },
            { "type": "mcp", "name": "my-local-tools",   "connection": { "endpoint": "stdio://my-tool-server --mcp" },      "execution-target": "<Machine-A-profile-entity-id>" }
          ]
        })
      → On server side: ChatClientTransportListener.OnChannelOpenAsync fires
          a. AgentFactory.CreateChatClient(definition)
               provider = "openai"
               → OpenAI client over HttpClient
               → WrapWithMiddleware → ToolResultSteeringMiddleware(openAiClient, queueManager)
          b. For each mcp-server entry (execution-target = local machine):
               workspace-gui    → execution-target matches local → LocalTransport → WorkspaceGuiMcpServerListener → AIFunction providers
               workspace-entity → execution-target matches local → LocalTransport → WorkspaceEntityMcpServerListener → AIFunction providers
               my-local-tools   → execution-target matches local → LocalTransport → McpTransportListener → StdioClientTransport("my-tool-server --mcp")
          c. Returns ChatClientTransportSession (IAsyncDisposable) owning all MCP sessions
      → On client side: IMessageChannel linked to the session above
      → ChatClientOverTransport : IChatClient wrapping the channel

      (Provider is "openai" — NOT Copilot SDK → no CopilotSubAgentRouterMiddleware)

7.  Subscribe to ToolResultSteeringMiddleware.MessagesInjected
      → AppendSteeringMessagesToHistory

8.  Register ChatClientOverTransport as owned resource (IAsyncDisposable)

9.  Create IncrementalPersistenceChatHistoryProvider(definition, store)
    Create AgentFrameworkChatHistoryProvider(persistenceProvider)
    Create StreamingPersistenceMiddleware(chatClientOverTransport, persistenceProvider, store)
      → this.client = streamingMiddleware (the outermost IChatClient wrapper)

10. chatOptions = new ChatClientAgentOptions {
        ChatOptions = new ChatOptions(),
        ChatHistoryProvider = agentFrameworkChatHistoryProvider,
        UseProvidedChatClientAsIs = false,         // standard tool loop
        RequirePerServiceCallChatHistoryPersistence = true,
    }
    AgentFactory.ConfigureChatOptions(definition, chatOptions.ChatOptions)
      → chatOptions.Instructions = "You are a helpful assistant."
      → chatOptions.AdditionalProperties["agent_definition"] = definition

11. CreateRuntimeContextProviderRegistrationsAsync(definition, services)
      → AgentFactory.ExtractTools(definition) = [workspace-gui, workspace-entity, mcp]
      → All CustomTool instances
      → [NEW] For each tool: toolsetFactory.CreateToolsetAsync(tool, services)
             workspace-gui    → McpToolContextProvider(McpClientOverTransport(LocalTransport → workspace-gui listener))
             workspace-entity → McpToolContextProvider(McpClientOverTransport(LocalTransport → workspace-entity listener))
             mcp "my-local-tools" → McpToolContextProvider(McpClientOverTransport(LocalTransport → my-tool-server stdio))
      → registrations = [RuntimeContextProviderRegistration x3]

12. chatOptions.AIContextProviders = registrations.Select(r => new ToolFilteringAIContextProvider(r.Provider, IsToolEnabled))

13. chatClientAgent = new ChatClientAgent(streamingMiddleware, chatOptions)
    Set session serializer (serializes ChatClientAgent framework session to BSON)

14. frameworkSession = await chatClientAgent.CreateSessionAsync()
    persistenceProvider.SetAgentSessionId(frameworkSession, agentSessionId)

15. persistedMessages = store.ReadMessagesAsync(agentSessionId)
    LoadInitialHistory(persistedMessages)           // empty first run

16. SetSession(new AgentChatSession(chatClientAgent, frameworkSession))
    SetAgentSessionId(resolvedAgentSessionId)

17. (No sub-agent restore — first run)

18. StartProcessingLoop()
      → Starts background Task that drains queueManager, calls session.RunStreamAsync on each input

19. InitializeMcpToolsAsync()
      → For each McpToolContextProvider:
             McpClient.ListToolsAsync() → [AIFunction, ...]
             Populates toolIndex / toolRoots with ToolStateNode entries
             Fires ToolsChanged → AgentChatViewModel refreshes tool list in UI
```

**Session ready.** User message → `EnqueueUserMessage` → processing loop → `session.RunStreamAsync` → `streamingMiddleware.GetStreamingResponseAsync` → `ChatClientOverTransport` → `ChatClientTransportListener` on local channel → `ToolResultSteeringMiddleware(openAiChatClient).GetStreamingResponseAsync` → OpenAI REST API → streaming updates → UI.

---

### Scenario 2 — Remote executor, OpenAI endpoint

**Topology:**
- Machine A: PW server + GUI (G == H). Has MongoDB. Connected to the internet via dev tunnel.
- Machine B: user's workstation (E). `user-computer-profile` GUID = `b1b2b3b4-...`. Has connected via reverse HTTP to Machine A. OpenAI API key in its environment.

#### Agent manifest entity (in PW entity store)

```json
{
  "entity-id": "c3d4e5f6-a7b8-90cd-ef12-345678901234",
  "entity-types": ["entity", "agent-manifest"],
  "names": [["agent-manifests", "my-remote-openai-agent"]],
  "display-name": { "default": "Remote OpenAI Assistant" },
  "manifest": {
    "name": "my-remote-openai-agent",
    "displayName": "Remote OpenAI Assistant",
    "description": "Coding assistant that executes on my remote workstation.",
    "template": {
      "kind": "prompt",
      "name": "my-remote-openai-agent",
      "model": {
        "id": "gpt-4o",
        "provider": "openai",
        "apiType": "OpenAI",
        "connection": { "kind": "key", "apiKey": "${OPENAI_API_KEY}" },
        "options": { "temperature": 0.2 }
      },
      "instructions": "You are a helpful coding assistant with access to my local development tools.",
      "metadata": {
        "trust-profile": {
          "$ref": { "entity-name": ["trust-profiles", "my-remote-workstation"] }
        }
      }
    },
    "resources": [
      { "kind": "tool", "id": "fixed", "name": "workspace-gui" },
      { "kind": "tool", "id": "fixed", "name": "workspace-entity" },
      { "kind": "tool", "id": "mcp-server-entity", "name": "local-dev-tools" }
    ]
  }
}
```

`template.metadata["trust-profile"]` references the `trust-profiles/my-remote-workstation` entity by name, causing the agent to execute on Machine B.

#### Trust profile entity `my-remote-workstation` (in PW entity store on Machine A)

```json
{
  "entity-types": ["entity", "llm-trust-profile"],
  "names": [["trust-profiles", "my-remote-workstation"]],
  "default-execution-target": { "type": "user-computer-profile", "entity-id": "b1b2b3b4-..." },
  "allowed-client-instances": ["b1b2b3b4-..."],
  "network-access-policy": "host-network"
}
```

#### User workflow

1. User creates the trust profile entity in PW pointing to their remote workstation (GUID = Machine B's `user-computer-profile` entity-id).
2. Machine B starts PW and connects to Machine A via reverse HTTP tunnel. `ReverseHttpClientTransportFactory` on Machine B proactively registers with Machine A's `ReverseHttpServerTransportFactory`.
3. User opens the `agent-manifest` entity on Machine A. The AgentManifestLaunchpad appears (same flow as Scenario 1).
4. `CreateAgentSessionEntityAsync` → agent-session entity in MongoDB.
5. `AgentManifestLoader.LoadManifestFromJson` → `AgentManifest`.
6. `AgentFactory.CreateAgentChatAsync(...)` → `CreateAgentDefinitionAsync`:
   - Clones/substitutes `template`.
   - Resolves `resources` via `ToolResourceFactory`: workspace-gui, workspace-entity (built-in), mcp-server-entity "local-dev-tools" → fetches `mcp-server` entity → MCP tool definition.
7. `EnforceTrustProfileAsync` resolves the trust profile from `template.metadata["trust-profile"]`.
8. `AgentChat.CreateAsync` + `InitializeAsync`.
9. The LLM call goes out from **Machine B** (Machine B has the API key in its environment). MCP tools run on **Machine B**. GUI and entity tools execute on **Machine A**.

#### Materialized `AgentDefinition` (after `CreateAgentDefinitionAsync`)

Same structure as Scenario 1 but with `metadata: { "trust-profile": { "$ref": { "entity-name": ["trust-profiles", "my-remote-workstation"] } } }` and tools appended from `resources` resolution.

#### Factory steps

Identical to Scenario 1 steps 1–3.

#### `AgentChat.InitializeAsync` steps

```
1-2. Same as Scenario 1.

3.  AgentTrustProfileResolver.ResolveAsync(definition, trustProfileProvider)
      → metadata["trust-profile"] = { "$ref": { "entity-name": ["trust-profiles", "my-remote-workstation"] } }
      → ITrustProfileProvider.ResolveAsync(entityName)
           → fetches entity from MongoDB, TrustProfileComposer.ComposeAsync
      → trustProfile = TrustProfile {
            ExecutionTarget: { type: "user-computer-profile", entity-id: "b1b2b3b4-..." },
            HostingWorkspacesClientInstances: ["b1b2b3b4-..."],
            NetworkAccessPolicy: HostNetwork
        }

4.  ExecutionTargetResolver.Resolve(trustProfile)
      → type = "user-computer-profile", entity-id = "b1b2b3b4-..."
      → $connection = { "type": "user-computer-profile", "entity-id": "b1b2b3b4-...", "target": { "type": "local" } }

5.  ITransportFactoryRegistry.ConnectToAsync($connection)
      → UserComputerProfileTransportFactory matches
          a. Fetch user-computer-profile entity by entity-id
          b. Is this the local machine? No (b1b2b3b4 ≠ local profile GUID)
          c. Is Machine B connected via reverse HTTP? Yes (registered in ReverseHttpServerTransportFactory)
          d. Re-dispatch: { "type": "reverse-http", "entity-id": "b1b2b3b4-...", "target": { "type": "local" } }
      → ReverseHttpServerTransportFactory matches
          → Looks up registration channel for "b1b2b3b4-..."
      → Returns ReverseHttpTransport (backed by registration channel to Machine B)

6.  ReverseHttpTransport.ConnectToMessageChannelAsync({
          "type": "chat-client",
          "definition": <agentDefinitionJson>,
          "mcp-servers": [
            { "type": "mcp", "name": "workspace-gui",    "connection": { "endpoint": "local://workspace-gui-listener" },    "execution-target": "<Machine-A-profile-entity-id>" },
            { "type": "mcp", "name": "workspace-entity", "connection": { "endpoint": "local://workspace-entity-listener" }, "execution-target": "<Machine-A-profile-entity-id>" },
            { "type": "mcp", "name": "local-dev-tools",  "connection": { "endpoint": "stdio://dev-tools --mcp" },           "execution-target": "<Machine-B-profile-entity-id>" }
          ]
        })
      → Sends channel-open frame to Machine B over the registration channel
      → Machine B: ChatClientTransportListener.OnChannelOpenAsync fires
          a. AgentFactory.CreateChatClient(definition)
               provider = "openai" → OpenAI client (using OPENAI_API_KEY from Machine B's environment)
               WrapWithMiddleware → ToolResultSteeringMiddleware(openAiClient, queueManager)
          b. For each mcp-server entry:
               workspace-gui    → execution-target = Machine A → ITransportFactoryRegistry → ReverseHttpTransport → Machine A workspace-gui listener
               workspace-entity → execution-target = Machine A → ITransportFactoryRegistry → ReverseHttpTransport → Machine A entity listener
               local-dev-tools  → execution-target = Machine B → matches local → LocalTransport → McpTransportListener → stdio("dev-tools --mcp")
          c. Returns ChatClientTransportSession on Machine B

7.  Machine A: IMessageChannel linked to Machine B's ChatClientTransportSession
    ChatClientOverTransport : IChatClient  (provider = openai → no CopilotSubAgentRouterMiddleware)

8-19. Same as Scenario 1 steps 7–19, except:
    - Step 11: McpToolContextProvider instances wrap McpClientOverTransport
         workspace-gui    → channel to Machine A (reverse round-trip from B back to A)
         workspace-entity → same
         local-dev-tools  → local channel on Machine B
    - Step 19 InitializeMcpToolsAsync: MCP list-tools requests travel to Machine A (for workspace tools)
         and stay local on Machine B (for dev-tools). Both populate the tool tree on Machine A.
```

**Turn:** Machine A `EnqueueUserMessage` → processing loop → `ChatClientOverTransport.GetStreamingResponseAsync(fullHistory)` → `process-streaming` frame to Machine B → Machine B's `ToolResultSteeringMiddleware(openAiClient).GetStreamingResponseAsync` → OpenAI API from Machine B → tool call for workspace-gui → Machine B's MCP client sends MCP tool-call to Machine A's workspace-gui listener → executes on Machine A → result back to Machine B → continues → streaming-update frames → Machine A → `IncrementalPersistenceChatHistoryProvider` persists to Machine A's MongoDB.

---

### Scenario 3 — Remote executor, Copilot SDK endpoint

**Topology:** Same machines as Scenario 2 (A = server/GUI, B = remote executor with GitHub Copilot CLI installed and authenticated).

#### Background: issue #808 branch (`feat/808-split-copilot-sdk-chat-client`)

The #808 branch splits `CopilotSdkChatClient` into two clean pieces:
- **`CopilotSdkStreamAdapter`** — static, translates raw GitHub Copilot SDK `SessionEvent` objects into `IAsyncEnumerable<ChatResponseUpdate>`. Encodes sub-agent routing metadata in `AIContent.AdditionalProperties[""copilot.sdk.parent_tool_call_id""]`. Sub-agent lifecycle signals become `FunctionCallContent(""copilot.subagent.start"")`) and `FunctionResultContent`. No `AgentChat` knowledge.
- **`CopilotSubAgentRouter`** — consumes `IAsyncEnumerable<ChatResponseUpdate>`, routes updates to root or sub-agent sinks, creates `AgentChat` entities via `IRunningAgentChatFactory`. No raw SDK knowledge.

Currently in the #808 branch both pieces are still wired *inside* `CopilotSdkChatClient.GetStreamingResponseAsync`. The transport layer design requires one further extraction: **`CopilotSdkChatClient` must yield the translated stream to its caller with no routing**, and `CopilotSubAgentRouter` becomes `CopilotSubAgentRouterMiddleware : IChatClient` that wraps any `IChatClient` — including `ChatClientOverTransport`. This is the natural continuation of #808: the seam it creates between translation and routing is exactly the seam needed for Machine A to observe and route sub-agent lifecycle events from Machine B.

#### Agent manifest entity (in PW entity store)

```json
{
  "entity-id": "e5f6a7b8-c9d0-1234-5678-90abcdef1234",
  "entity-types": ["entity", "agent-manifest"],
  "names": [["agent-manifests", "my-remote-copilot-agent"]],
  "display-name": { "default": "Remote Copilot Assistant" },
  "manifest": {
    "name": "my-remote-copilot-agent",
    "displayName": "Remote Copilot Assistant",
    "description": "Copilot SDK agent that runs on my remote workstation.",
    "template": {
      "kind": "prompt",
      "name": "my-remote-copilot-agent",
      "model": {
        "id": "auto",
        "provider": "github-copilot",
        "apiType": "OpenAI",
        "connection": { "kind": "key", "apiKey": "${GITHUB_TOKEN}" },
        "options": {
          "additionalProperties": { "cliPath": "/usr/local/bin/gh" }
        }
      },
      "instructions": "You are a helpful coding assistant.",
      "metadata": {
        "trust-profile": {
          "$ref": { "entity-name": ["trust-profiles", "my-remote-workstation"] }
        }
      }
    },
    "resources": [
      { "kind": "tool", "id": "fixed", "name": "workspace-gui" },
      { "kind": "tool", "id": "fixed", "name": "workspace-entity" },
      { "kind": "tool", "id": "mcp-server-entity", "name": "local-dev-tools" }
    ]
  }
}
```

`template.model.provider = "github-copilot"` with `cliPath` pointing to the `gh` CLI on Machine B. The trust profile routes execution to Machine B.

#### User workflow

1. Same trust profile entity as Scenario 2.
2. Machine B has `gh` CLI installed and authenticated.
3. Machine B's PW has registered with Machine A via reverse HTTP.
4. User opens the `agent-manifest` entity on Machine A. Same AgentManifestLaunchpad flow. `CreateAgentDefinitionAsync` resolves `resources` identically to Scenario 2.
5. Copilot CLI runs on **Machine B**. Sub-agents are internally managed by the Copilot SDK on Machine B; their lifecycle events flow back to Machine A where `CopilotSubAgentRouterMiddleware` creates `AgentChat` entities.

#### Materialized `AgentDefinition` (after `CreateAgentDefinitionAsync`)

```json
{
  "kind": "prompt",
  "name": "my-remote-copilot-agent",
  "model": {
    "id": "auto",
    "provider": "github-copilot",
    "apiType": "OpenAI",
    "connection": { "kind": "key", "apiKey": "<resolved>" },
    "options": { "additionalProperties": { "cliPath": "/usr/local/bin/gh" } }
  },
  "instructions": "You are a helpful coding assistant.",
  "metadata": {
    "trust-profile": { "$ref": { "entity-name": ["trust-profiles", "my-remote-workstation"] } }
  },
  "tools": [
    { "kind": "workspace-gui" },
    { "kind": "workspace-entity" },
    { "kind": "mcp", "serverName": "local-dev-tools",
      "connection": { "kind": "stdio", "command": "dev-tools", "args": ["--mcp"] } }
  ]
}
```

#### Factory steps

Same as Scenario 2 steps 1–3.

#### `AgentChat.InitializeAsync` steps

```
1-5. Identical to Scenario 2.

6.  ReverseHttpTransport.ConnectToMessageChannelAsync({
          "type": "chat-client",
          "definition": <agentDefinitionJson>,
          "mcp-servers": [
            { "type": "mcp", "name": "workspace-gui",    "connection": { "endpoint": "local://workspace-gui-listener" },    "execution-target": "<Machine-A-profile-entity-id>" },
            { "type": "mcp", "name": "workspace-entity", "connection": { "endpoint": "local://workspace-entity-listener" }, "execution-target": "<Machine-A-profile-entity-id>" },
            { "type": "mcp", "name": "local-dev-tools",  "connection": { "endpoint": "stdio://dev-tools --mcp" },           "execution-target": "<Machine-B-profile-entity-id>" }
          ]
        })
      → Machine B: ChatClientTransportListener.OnChannelOpenAsync fires
          a. AgentFactory.CreateChatClient(definition)
               provider = "github-copilot"
               → CreateGitHubCopilotResult → CopilotSdkChatClient (uses cliPath="/usr/local/bin/gh" on Machine B)
               (Self-invoking: NOT wrapped with ToolResultSteeringMiddleware)
               [NEW] CopilotSdkChatClient does NOT create CopilotSubAgentRouter internally
                     — it only uses CopilotSdkStreamAdapter, yielding the translated stream directly
          b. MCP server connections opened: same as Scenario 2
          c. Returns ChatClientTransportSession on Machine B

7.  Machine A: IMessageChannel linked to Machine B's ChatClientTransportSession
    ChatClientOverTransport : IChatClient (provider = github-copilot)

    [NEW] provider = "github-copilot" → wrap with CopilotSubAgentRouterMiddleware:
    CopilotSubAgentRouterMiddleware(
        inner = ChatClientOverTransport,
        factory = agentServices.RunningAgentChatFactory,    // creates AgentChat on Machine A
        subAgentTable = this (AgentChat implements ISubAgentTable),
        registry = this (AgentChat implements ISubAgentChatRegistry)
    )
    → This is the IChatClient used for the rest of initialization

8.  Register CopilotSubAgentRouterMiddleware as owned resource

9-10. Create persistence providers, StreamingPersistenceMiddleware(copilotSubAgentRouterMiddleware, ...)

11. AgentFactory.ConfigureChatOptions:
      UseProvidedChatClientAsIs = true       ← CopilotSdkChatClient is ISelfInvokingToolChatClient
      (The agent framework skips its own function-invocation middleware)

12. CreateRuntimeContextProviderRegistrationsAsync:
      [NEW] McpToolContextProvider instances backed by McpClientOverTransport
         workspace-gui    → McpClientOverTransport → ReverseHttpTransport → Machine A workspace-gui listener
         workspace-entity → same
         local-dev-tools  → McpClientOverTransport → LocalTransport → Machine B dev-tools stdio

13. chatOptions.AIContextProviders = [ToolFilteringAIContextProvider x3]

14. chatClientAgent = new ChatClientAgent(streamingMiddleware, chatOptions)
      UseProvidedChatClientAsIs = true
      → ChatClientAgent does NOT inject its own FunctionInvocationMiddleware
      → Copilot SDK drives its own tool loop internally on Machine B

15-19. Same as Scenario 1 steps 14–19.
    Notable: InitializeMcpToolsAsync lists tools via MCP from Machine B's local servers AND
             from Machine A's workspace-gui/entity servers (via reverse transport).
             The Copilot SDK on Machine B also receives the MCP tool schemas at session open time.
```

**Turn with sub-agents:**
1. Machine A: `EnqueueUserMessage` → processing loop → `CopilotSubAgentRouterMiddleware.GetStreamingResponseAsync(latestMessage, chatOptions)`
2. `CopilotSubAgentRouterMiddleware` creates `CopilotSubAgentRouter(rootWriter, registry, factory, subAgentTable)`, calls `router.RouteUpdatesAsync(ChatClientOverTransport.GetStreamingResponseAsync(...))`
3. `ChatClientOverTransport` sends `process-streaming` to Machine B
4. Machine B: `CopilotSdkChatClient.GetStreamingResponseAsync` → `CopilotSdkStreamAdapter.TranslateCopilotSdkSessionEvents` → `IAsyncEnumerable<ChatResponseUpdate>`
5. `ChatClientTransportListener` emits each update as `streaming-update` frame
6. Machine A: `CopilotSubAgentRouter.RouteUpdatesAsync` processes each update:
   - Root text → `rootWriter.TryWrite(update)` → surfaces in root `AgentChat.History`
   - `FunctionCallContent("copilot.subagent.start", args)` → `HandleSubAgentStartedAsync`:
       a. `factory.CreateAsync(SubAgentDefinition, newSessionId)` → new `AgentChat` entity on Machine A (persisted in MongoDB)
       b. Sub-agent `AgentChat` uses `CopilotSubAgentChatClient : IChatClient` (a pure channel backed by `Channel<ChatResponseUpdate>`)
       c. `subAgentTable.Add(subAgentChat)` → sub-agent appears in `SubAgentsContainerViewModel`
   - Subsequent updates tagged `parent_tool_call_id = "sub-1"` → pushed to sub-agent's `ICopilotSubAgentReceiver`
   - Sub-agent `AgentChat.History` and UI updated on Machine A
   - `FunctionResultContent("sub-1", {event:"completed"})` → `SetCompletionState(Succeeded)`; lease disposed
7. MCP tool calls (workspace-gui or dev-tools) from Machine B: routed by `McpClientOverTransport` over MCP protocol; Machine A's workspace-gui listener executes locally; result returns to Machine B's `CopilotSdkChatClient`
8. Turn completes; `streaming-update-complete` frame; `IncrementalPersistenceChatHistoryProvider` persists to Machine A MongoDB

#### Sub-agent object graph (fully embodied)

`
Machine A — when Copilot SDK starts sub-agent "code-reviewer":
  [turn N: FunctionCallContent("sub-1", "copilot.subagent.start", {display-name: "code-reviewer"})]
  CopilotSubAgentRouterMiddleware.HandleSubAgentStartedAsync("sub-1"):
    ├─ factory.CreateAsync(SubAgentDefinition, newSessionId)
    │    └─ creates AgentChat entity "sub-1-session" in MongoDB (Machine A)
    │         └─ ICopilotSubAgentReceiver: receives updates pushed by CopilotSubAgentRouterMiddleware
    └─ subAgentTable.Add(agentChat)  → sub-agent appears in SubAgentsContainerViewModel on Machine A

  [subsequent updates tagged parent_tool_call_id = "sub-1"]
  CopilotSubAgentRouterMiddleware.RouteUpdate("sub-1", update):
    └─ ICopilotSubAgentReceiver.Push(update)
         └─ sub-agent AgentChat's CopilotSubAgentChatClient yields it via GetStreamingResponseAsync
              └─ sub-agent AgentChat's history and UI updated on Machine A

  [FunctionResultContent("sub-1", {event:"completed"})]
  CopilotSubAgentRouterMiddleware.HandleSubAgentResultAsync("sub-1"):
    └─ AgentChat.SetCompletionState(Succeeded); lease.DisposeAsync()
`

#### Copilot SDK session statefulness

- `CopilotSession` (the live CLI session) is held by `CopilotSdkChatClient` for the lifetime of the `ChatClientTransportSession`.
- The `ChatClientTransportSession` is alive for the lifetime of the `IMessageChannel`.
- On channel close (Machine A disposes `ChatClientOverTransport`), Machine B's `ChatClientTransportSession` disposes, which disposes `CopilotSdkChatClient` and its `CopilotSession`.
- All sub-agent sessions on Machine B are terminated when the parent session closes.

#### Required transport layer refactoring beyond #808

- `CopilotSdkChatClient` must be changed to expose its translated stream directly (remove the internal `CopilotSubAgentRouter` wiring).
- `CopilotSubAgentRouter` becomes `CopilotSubAgentRouterMiddleware : IChatClient`:

`csharp
public sealed class CopilotSubAgentRouterMiddleware : IChatClient
{
    public CopilotSubAgentRouterMiddleware(
        IChatClient inner,
        IRunningAgentChatFactory factory,
        ISubAgentTable subAgentTable,
        ISubAgentChatRegistry? registry = null,
        ILogger? logger = null) { ... }

    // Delegates to inner.GetStreamingResponseAsync, pipes the update stream through
    // CopilotSubAgentRouter.RouteUpdatesAsync, yields root updates to caller.
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IList<ChatMessage> messages, ChatOptions? options,
        [EnumeratorCancellation] CancellationToken ct) { ... }
}
`

- This middleware is only applied when the agent provider is `github-copilot` or `github-copilot-subagent`. Other providers (OpenAI, Ollama, etc.) skip it.

---

### Scenario 4 — Local agent, Copilot SDK (common case)

**Topology:** G == H == E = Machine A. GitHub Copilot CLI installed locally. All channels use `LocalTransport`; this scenario is structurally identical to Scenario 3 with no network boundary.

#### Agent manifest entity (in PW entity store)

```json
{
  "entity-id": "b9c0d1e2-6f7a-4b8c-9d0e-5f6a7b8c9d0e",
  "entity-types": ["entity", "agent-manifest"],
  "names": [["defaults", "agent-manifests", "github-copilot"]],
  "display-name": { "default": "GitHub Copilot Workspace Assistant" },
  "manifest": {
    "name": "github-copilot",
    "displayName": "GitHub Copilot Workspace Assistant",
    "description": "Default GitHub Copilot SDK agent for workspace entity operations.",
    "parameters": {
      "properties": [
        {
          "name": "working-directory",
          "kind": "string",
          "description": "The working directory for the Copilot agent process.",
          "required": false
        }
      ]
    },
    "template": {
      "kind": "prompt",
      "name": "github-copilot",
      "model": {
        "id": "auto",
        "provider": "github-copilot",
        "apiType": "OpenAI",
        "connection": { "kind": "key", "apiKey": "${GITHUB_TOKEN}" },
        "options": {
          "additionalProperties": { "working-directory": "${working-directory}" }
        }
      },
      "instructions": "You are a helpful AI assistant powered by the GitHub Copilot SDK.",
      "metadata": {}
    },
    "resources": [
      { "kind": "tool", "id": "fixed", "name": "workspace-entity" },
      { "kind": "tool", "id": "fixed", "name": "workspace-gui" },
      { "kind": "tool", "id": "mcp-server-entity", "name": "github" }
    ]
  }
}
```

No trust profile → local execution on Machine A. `${working-directory}` in the template is substituted from user input at session start. This is the real `defaults/agent-manifests/github-copilot` entity.

#### User workflow

1. User opens the `agent-manifest` entity on Machine A (e.g., from the entity browser).
2. AgentManifestLaunchpad shows the `working-directory` parameter field (from `manifest.parameters.properties`).
3. User optionally enters a working directory and clicks **Start Session**.
4. `CreateAgentSessionEntityAsync` → agent-session entity in MongoDB.
5. `AgentManifestLoader.LoadManifestFromJson(manifest-field-JSON)` → `AgentManifest`.
6. `AgentFactory.CreateAgentChatAsync({ AgentManifest, Parameters: { "working-directory": "/repo" }, ToolResourceFactory, ... })`.
7. `CreateAgentDefinitionAsync`: `AgentDefinitionParameterSubstitutor.Substitute` replaces `${working-directory}` → `/repo`; resolves `resources` (workspace-entity, workspace-gui built-in; mcp-server-entity "github" → fetches `mcp-server` entity).
8. Everything runs on **Machine A**. `gh` CLI is installed locally.

#### Materialized `AgentDefinition` (after `CreateAgentDefinitionAsync`)

```json
{
  "kind": "prompt",
  "name": "github-copilot",
  "model": {
    "id": "auto",
    "provider": "github-copilot",
    "apiType": "OpenAI",
    "connection": { "kind": "key", "apiKey": "<resolved>" },
    "options": { "additionalProperties": { "working-directory": "/repo" } }
  },
  "instructions": "You are a helpful AI assistant powered by the GitHub Copilot SDK.",
  "metadata": {},
  "tools": [
    { "kind": "workspace-entity" },
    { "kind": "workspace-gui" },
    { "kind": "mcp", "serverName": "github",
      "connection": { "kind": "http", "endpoint": "https://api.githubcopilot.com/mcp/", "apiKey": "<resolved>" } }
  ]
}
```

#### Factory steps

Same as Scenario 1 steps 1–3.

#### `AgentChat.InitializeAsync` steps

```
1-2. Same as Scenario 1.

3.  AgentTrustProfileResolver: no trust-profile → trustProfile = null
4.  ExecutionTargetResolver: $connection = { "type": "local" }

5.  ITransportFactoryRegistry.ConnectToAsync({ "type": "local" })
      → LocalTransportFactory → LocalTransport

6.  LocalTransport.ConnectToMessageChannelAsync({
          "type": "chat-client",
          "definition": <agentDefinitionJson>,
          "mcp-servers": [
            { "type": "mcp", "name": "workspace-entity", "connection": { "endpoint": "local://workspace-entity-listener" },               "execution-target": "<Machine-A-profile-entity-id>" },
            { "type": "mcp", "name": "workspace-gui",    "connection": { "endpoint": "local://workspace-gui-listener" },                  "execution-target": "<Machine-A-profile-entity-id>" },
            { "type": "mcp", "name": "github",           "connection": { "endpoint": "https://api.githubcopilot.com/mcp/" },              "execution-target": "<Machine-A-profile-entity-id>" }
          ]
        })
      → ChatClientTransportListener.OnChannelOpenAsync (in-process, no network):
          a. AgentFactory.CreateChatClient(definition)
               provider = "github-copilot"
               → CopilotSdkChatClient (local gh CLI)
          b. MCP sessions (all execution-target = local machine):
               workspace-entity → execution-target matches local → LocalTransport → WorkspaceEntityMcpServerListener (in-process)
               workspace-gui    → execution-target matches local → LocalTransport → WorkspaceGuiMcpServerListener (in-process)
               github           → execution-target matches local → LocalTransport → McpTransportListener → HttpClientTransport("https://api.githubcopilot.com/mcp/")
          c. Returns ChatClientTransportSession

7.  Machine A: IMessageChannel (in-process via LocalTransport)
    ChatClientOverTransport : IChatClient

    provider = "github-copilot" → wrap with CopilotSubAgentRouterMiddleware(
        inner = ChatClientOverTransport,
        factory = runningAgentChatFactory,
        subAgentTable = this,
        registry = this
    )

8-19. Same as Scenario 3 steps 8–19, but all transports are LocalTransport (in-process, no serialization).
    - MCP list-tools: all in-process
    - Sub-agent AgentChat entities: created locally on Machine A by CopilotSubAgentRouterMiddleware
    - No streaming-update frames cross a network boundary; updates flow through Channel<JsonElement> in memory
```

**This is identical to Scenario 3 in structure.** The only difference is `LocalTransport` replaces `ReverseHttpTransport`. All the same abstractions apply; the transport layer makes local and remote usage structurally identical.

---

### Scenario 5 — Peer-to-peer via hub relay (Copilot SDK)

**Topology:**
- Machine A = PW hub. Runs `HttpServerTransportFactory`, `ReverseHttpServerTransportFactory` (with relay behavior). Has GUI. No Copilot CLI.
- Machine B = User's GUI machine. Connects to Machine A via devtunnel reverse registration. The user creates the agent chat session here. No Copilot CLI.
- Machine C = Developer workstation. Connects to Machine A via devtunnel reverse registration. Has GitHub Copilot CLI installed and authenticated. Executes the agent chat.

Machine B and Machine C have no direct network connectivity to each other; they can only be reached through Machine A.

#### Agent manifest entity (in PW entity store)

Same structure as Scenario 3 but `metadata["trust-profile"]` references a trust profile that names Machine C's user-computer-profile as the execution target.

```json
{
  "entity-id": "f1a2b3c4-d5e6-7890-ab12-cd34ef567891",
  "entity-types": ["entity", "agent-manifest"],
  "names": [["agent-manifests", "my-relay-copilot-agent"]],
  "display-name": { "default": "Relay Copilot Assistant" },
  "manifest": {
    "name": "my-relay-copilot-agent",
    "displayName": "Relay Copilot Assistant",
    "description": "Copilot SDK agent executing on Machine C, initiated from Machine B via hub relay.",
    "template": {
      "kind": "prompt",
      "name": "my-relay-copilot-agent",
      "model": {
        "id": "auto",
        "provider": "github-copilot",
        "apiType": "OpenAI",
        "connection": { "kind": "key", "apiKey": "${GITHUB_TOKEN}" },
        "options": {
          "additionalProperties": { "cliPath": "/usr/local/bin/gh" }
        }
      },
      "instructions": "You are a helpful coding assistant.",
      "metadata": {
        "trust-profile": {
          "$ref": { "entity-name": ["trust-profiles", "machine-c-workstation"] }
        }
      }
    },
    "resources": [
      { "kind": "tool", "id": "fixed", "name": "workspace-gui" },
      { "kind": "tool", "id": "fixed", "name": "workspace-entity" },
      { "kind": "tool", "id": "mcp-server-entity", "name": "local-dev-tools" }
    ]
  }
}
```

#### Trust profile entity `machine-c-workstation` (in PW entity store on Machine A)

```json
{
  "entity-types": ["entity", "llm-trust-profile"],
  "names": [["trust-profiles", "machine-c-workstation"]],
  "default-execution-target": { "type": "user-computer-profile", "entity-id": "c1c2c3c4-..." },
  "allowed-client-instances": ["c1c2c3c4-..."],
                 "type": "reverse-http",
}
```
          3. Machine A: ReverseHttpServerTransportFactory relay behavior fires
Machine C's user-computer-profile `connection-descriptor` is `{ "type": "reverse-http", "entity-id": "c1c2c3c4-..." }` — resolvable on Machine A, not directly from Machine B.

#### User workflow

1. Machine C starts PW and connects to Machine A via reverse HTTP tunnel. `ReverseHttpClientTransportFactory` on Machine C proactively registers with Machine A's `ReverseHttpServerTransportFactory`.
2. Machine B also starts PW and connects to Machine A via reverse HTTP tunnel.
3. User (on Machine B) opens the `agent-manifest` entity. `CreateAgentSessionEntityAsync` creates an agent-session entity in MongoDB on Machine A.
4. `AgentManifestLoader.LoadManifestFromJson` → `AgentManifest`. `CreateAgentDefinitionAsync` resolves `resources` identically to Scenario 3.
5. The Copilot CLI runs on **Machine C**. Sub-agents are managed by the Copilot SDK on Machine C; their lifecycle events flow back to Machine B where `CopilotSubAgentRouterMiddleware` creates `AgentChat` entities on Machine A.

#### Materialized `AgentDefinition` (after `CreateAgentDefinitionAsync`)

Same structure as Scenario 3 with `metadata["trust-profile"]` referencing `machine-c-workstation` and tools resolved from `resources`.

#### Factory steps (differences from Scenario 3)

1. `AgentManifest` provided → `CreateAgentDefinitionAsync` → resolved `AgentDefinition`.
2. `EnforceTrustProfileAsync`: resolves trust profile for Machine C; validates allowed instances.
3. `AgentChat.CreateAsync` + `InitializeAsync`.

#### `AgentChat.InitializeAsync` steps

```
1-2. Same as Scenario 1.

3.  AgentTrustProfileResolver.ResolveAsync(definition, trustProfileProvider)
      → metadata["trust-profile"] = { "$ref": { "entity-name": ["trust-profiles", "machine-c-workstation"] } }
      → trustProfile = TrustProfile {
            ExecutionTarget: { type: "user-computer-profile", entity-id: "c1c2c3c4-..." },
            HostingWorkspacesClientInstances: ["c1c2c3c4-..."],
            NetworkAccessPolicy: HostNetwork
        }

4.  ExecutionTargetResolver.Resolve(trustProfile)
      → type = "user-computer-profile", entity-id = "c1c2c3c4-..."
      → $connection = { "type": "user-computer-profile", "entity-id": "c1c2c3c4-...", "target": { "type": "local" } }

5.  ITransportFactoryRegistry.ConnectToAsync($connection)   [running on Machine B]
      → UserComputerProfileTransportFactory matches
          a. Fetch user-computer-profile entity for entity-id "c1c2c3c4-..."
          b. Read entity.connection-descriptor:
                { "type": "reverse-http",
                  "hub-urls": ["https://A-devtunnel/...", "http://192.168.1.5:5000"],
                  "entity-id": "c1c2c3c4-..." }
             (Written by Machine C's ReverseHttpClientTransportFactory instances — one URL
              per hub they successfully registered with.)
          c. ITransportFactoryRegistry.ConnectToAsync(connection-descriptor)
             [No caller-identity check — the descriptor is self-describing]
      → ReverseHttpForwardingTransportFactory matches on Machine B
          (Machine A would route the same descriptor to ReverseHttpServerTransportFactory instead)
          1. Race all hub-urls in parallel via HttpClientTransportFactory.ConnectToAsync.
             First to succeed wins ("https://A-devtunnel/..." or "http://192.168.1.5:5000").
             → HttpTransport (WebSocket connection to Machine A via winning URL)
          2. HttpTransport.ConnectToMessageChannelAsync({
                 "type": "reverse-http",
                 "entity-id": "c1c2c3c4-..."
             })
           3. Machine A: ReverseHttpServerTransportFactory relay behavior fires
                a. Looks up ReverseHttpServerTransportFactory._registrations["c1c2c3c4-..."]
                   → IMessageChannel to Machine C (existing registration channel, opened at startup)
                   No ConnectToAsync call.
                b. Relay pump started: background task reads frames from B's IMessageChannel
                   and writes to C's, and vice versa. Transparent; no frame inspection.
                c. B's IMessageChannel and relay session returned to ReverseHttpForwardingTransportFactory
          4. ReverseHttpForwardingTransportFactory wraps B's IMessageChannel as ITransport
      → ITransport on Machine B is relay-backed.
        Writes: Machine B → HttpTransport → Machine A relay pump → ReverseHttpTransport → Machine C
        Reads:  Machine C → ReverseHttpTransport → Machine A relay pump → HttpTransport → Machine B

6.  RelayBackedTransport.ConnectToMessageChannelAsync({
          "type": "chat-client",
          "definition": <agentDefinitionJson>,
          "mcp-servers": [
            { "type": "mcp", "name": "workspace-gui",    "connection": { "endpoint": "local://workspace-gui-listener" },    "execution-target": "<Machine-B-profile-entity-id>" },
            { "type": "mcp", "name": "workspace-entity", "connection": { "endpoint": "local://workspace-entity-listener" }, "execution-target": "<Machine-B-profile-entity-id>" },
            { "type": "mcp", "name": "local-dev-tools",  "connection": { "endpoint": "stdio://dev-tools --mcp" },           "execution-target": "<Machine-C-profile-entity-id>" }
          ]
        })
      → channel-open frame travels B → relay on A → Machine C
      → Machine C: ChatClientTransportListener.OnChannelOpenAsync fires
          a. AgentFactory.CreateChatClient(definition)
               provider = "github-copilot"
               → CopilotSdkChatClient (uses cliPath="/usr/local/bin/gh" on Machine C)
               [NEW] CopilotSdkChatClient yields translated stream directly (no internal routing)
          b. MCP server connections opened (same as Scenario 3, running on Machine C)
          c. Returns ChatClientTransportSession on Machine C

7.  Machine B: IMessageChannel linked to Machine C's ChatClientTransportSession (via relay through A)
    ChatClientOverTransport : IChatClient (provider = github-copilot)

    [NEW] provider = "github-copilot" → wrap with CopilotSubAgentRouterMiddleware:
    CopilotSubAgentRouterMiddleware(
        inner = ChatClientOverTransport,
        factory = agentServices.RunningAgentChatFactory,  // creates AgentChat on Machine A
        subAgentTable = this,
        registry = this
    )

8-19. Same as Scenario 3 steps 8–19.
    - All ChatClientOverTransport frames travel via the relay on Machine A (transparently)
    - Machine A does not parse chat content — relay pump is a byte-level forward
    - Sub-agent AgentChat entities are created on Machine A by CopilotSubAgentRouterMiddleware
      running on Machine B
    - MCP tool calls for workspace-gui/entity travel: C → relay via A → B listener
    - MCP tool calls for local-dev-tools: C → local
```

#### Object graph

```
Machine B                  Machine A (hub)               Machine C
──────────                 ──────────────────            ──────────
AgentChat                  ReverseHttpServerTransportFactory (relay)  ChatClientTransportSession
  └─ CopilotSubAgentRouterMiddleware                         └─ CopilotSdkChatClient
       └─ ChatClientOverTransport                                  └─ CopilotSdkStreamAdapter
            └─ IMessageChannel (relay, B-side)   ←pump→   IMessageChannel (relay, C-side)
                    │                                              │
            ReverseHttpForwardingTransportFactory              ReverseHttpServerTransportFactory
                    │                                              │
            HttpTransport → (HTTPS to A)          ReverseHttpTransport (to C's registration channel)
```

**Sub-agent object graph:** Same as Scenario 3 — `CopilotSubAgentRouterMiddleware` on Machine B intercepts sub-agent events, creates `AgentChat` entities on Machine A, and routes sub-agent updates. Sub-agents run on Machine B (the routing observer), not Machine C (the relay only carries the outer chat client channel).

**Wire protocol note:** The relay on Machine A is a transparent frame pump. Machine B sends `ChatClientTransportFrame` frames; Machine A forwards them byte-for-byte to Machine C. Machine A does not parse chat content.

**Turn flow:** Machine B → `CopilotSubAgentRouterMiddleware.GetStreamingResponseAsync` → `ChatClientOverTransport` → `process-streaming` frame → relay pump on A → Machine C → `CopilotSdkChatClient` → Copilot SDK → `CopilotSdkStreamAdapter` → `streaming-update` frames → relay pump on A → Machine B → `CopilotSubAgentRouter.RouteUpdatesAsync` → root updates to `AgentChat.History` on Machine B; sub-agent events → new `AgentChat` entities on Machine A.

---

### InitializeAsync walkthrough

This section traces Scenario 5 at the same step-by-step depth as Scenarios 1–4. It is split into four parts that together cover the full lifecycle: Machine C's startup registration with the hub (pre-condition), Machine B's `AgentChat.InitializeAsync` and transport establishment, a live streaming turn, and teardown.

---

#### Part 1 — Machine C startup: reverse-registration with the hub (pre-condition)

This part describes the one-time setup that must complete before Machine B can relay to Machine C.

```
[Machine C — PW startup]

1.  DI resolves one ReverseHttpClientTransportFactory per configured hub URL.
    ReverseHttpForwardingTransportFactory is registered in Machine C's ITransportFactoryRegistry
    for "reverse-http" descriptors alongside ReverseHttpServerTransportFactory.
    (This is irrelevant to the outbound registration; it only matters if Machine C
    itself later needs to reach another peer via the hub.)

2.  ReverseHttpClientTransportFactory.InitializeAsync — crash recovery:
        EntityRepository.UpdateAsync(entity-id: "c1c2c3c4-...",
            op: set connection-descriptor.hub-urls = [])
    Clears any stale hub-urls left by a previous crash before writing new ones.

3.  HttpClientTransportFactory.ConnectToAsync({
            "type": "http",
            "url": "https://A-devtunnel/..."
        })
        → Opens WebSocket from Machine C to Machine A.
        → Returns HttpTransport (C→A).

4.  HttpTransport.ConnectToMessageChannelAsync({
            "type": "reverse-register",
            "entity-id": "c1c2c3c4-..."
        })
        → Sends channel-open frame to Machine A:
            {
              "type": "channel-open",
              "channel-id": "<reg-channel-guid>",
              "request": { "type": "reverse-register", "entity-id": "c1c2c3c4-..." }
            }

[Machine A — hub]

5.  HttpServerTransportFactory receives the channel-open.
    Iterates registered ITransportListener instances:
        ReverseHttpServerTransportFactory (relay)  — does not handle "reverse-register" → null
        ChatClientTransportListener — does not handle it → null
        ReverseHttpServerTransportFactory — handles "reverse-register" ✓

6.  ReverseHttpServerTransportFactory.OnChannelOpenAsync:
        _registrations["c1c2c3c4-..."] = registrationChannel    // IMessageChannel C↔A
    Returns a registration-session IAsyncDisposable tracked by HttpServerTransportFactory.

[Machine C — registration confirmed]

7.  ReverseHttpClientTransportFactory upserts Machine A's URL into Machine C's
    user-computer-profile entity (hub slot for Machine A):
        EntityRepository.UpdateAsync(entity-id: "c1c2c3c4-...",
            op: set connection-descriptor = {
                "type": "reverse-http",
                "hub-urls": ["https://A-devtunnel/...", "http://192.168.1.5:5000"],
                "entity-id": "c1c2c3c4-..."
            })
    This is the descriptor Machine B will read in Part 2.

8.  The registration IMessageChannel stays open indefinitely.
    Machine C's ReverseHttpClientTransportFactory holds it and listens for
    channel-open frames that Machine A sends back over it when a relay is set up.
```

**Pre-condition complete.** Machine A's `ReverseHttpServerTransportFactory._registrations["c1c2c3c4-..."]` holds a live WebSocket-backed `IMessageChannel` to Machine C.

---

#### Part 2 — Machine B: `AgentChat.InitializeAsync` and transport establishment

The user has opened the `my-relay-copilot-agent` manifest on Machine B. `CreateAgentDefinitionAsync` has already resolved resources. `AgentFactory.CreateAgentChatAsync` calls `AgentChat.InitializeAsync`.

                    "type": "reverse-http",
[Machine B]

1.  RestoreAsync(agentSessionId)
        → No prior session: restoredAgent = null

2.  AgentDefinition already fully resolved (tools appended by CreateAgentDefinitionAsync):
        provider = "github-copilot", cliPath = "/usr/local/bin/gh"
                "type": "reverse-http",

3.  AgentTrustProfileResolver.ResolveAsync(definition, trustProfileProvider)
        → metadata["trust-profile"] = { "$ref": { "entity-name": ["trust-profiles", "machine-c-workstation"] } }
        → ITrustProfileProvider.ResolveAsync fetches entity from MongoDB on Machine A,
          TrustProfileComposer.ComposeAsync
        → trustProfile = TrustProfile {
              ExecutionTarget: { type: "user-computer-profile", entity-id: "c1c2c3c4-..." },
              HostingWorkspacesClientInstances: ["c1c2c3c4-..."],
              NetworkAccessPolicy: HostNetwork
          }

4.  ExecutionTargetResolver.Resolve(trustProfile)
        → type = "user-computer-profile", entity-id = "c1c2c3c4-..."
        → $connection = { "type": "user-computer-profile", "entity-id": "c1c2c3c4-..." }

5.  ITransportFactoryRegistry.ConnectToAsync({ "type": "user-computer-profile",
                                               "entity-id": "c1c2c3c4-..." })
    [running on Machine B]
        → UserComputerProfileTransportFactory matches
            a. Fetch Machine C's user-computer-profile entity from the entity store
            b. Read entity.connection-descriptor:
                 { "type": "reverse-http",
                   "hub-urls": ["https://A-devtunnel/...", "http://192.168.1.5:5000"],
                   "entity-id": "c1c2c3c4-..." }
               (Written by Machine C's ReverseHttpClientTransportFactory in Part 1 step 7)
            c. Compare "c1c2c3c4-..." vs EntityRepository.WorkspaceEntitySession
               .UserComputerProfileEntityId on Machine B → not the local machine
            d. ITransportFactoryRegistry.ConnectToAsync(connection-descriptor)
               [re-dispatches — no caller-identity check]

6.  ReverseHttpForwardingTransportFactory.ConnectToAsync({
            "type": "reverse-http",
            "hub-urls": ["https://A-devtunnel/...", "http://192.168.1.5:5000"],
            "entity-id": "c1c2c3c4-..."
        })
    [Machine B — registered for "reverse-http" descriptors on the client side]

    6a. Race all hub-urls in parallel:
            Task<ITransport> t1 = HttpClientTransportFactory.ConnectToAsync(
                { "type": "http", "url": "https://A-devtunnel/..." });
            Task<ITransport> t2 = HttpClientTransportFactory.ConnectToAsync(
                { "type": "http", "url": "http://192.168.1.5:5000" });
            winningTransport = first of (t1, t2) to succeed
            // cancels and disposes the losing attempt
        → winningTransport = HttpTransport (WebSocket from Machine B to Machine A)

    6b. Open relay channel on the winning transport:
            IMessageChannel relayChannel =
                await winningHttpTransport.ConnectToMessageChannelAsync({
                    "type": "reverse-http",
                    "entity-id": "c1c2c3c4-..."
                });
        Sends channel-open frame to Machine A:
            {
              "type": "channel-open",
              "channel-id": "<relay-channel-guid>",
              "request": {
                "type": "reverse-http",
                "entity-id": "c1c2c3c4-..."
              }
            }

[Machine A — hub]

7.  HttpServerTransportFactory receives the channel-open. Iterates listeners:
        ReverseHttpServerTransportFactory — does not handle "relay" → null
        ChatClientTransportListener       — does not handle "relay" → null
        ReverseHttpServerTransportFactory relay behavior — handles "reverse-http" (relay) ✓

8.  ReverseHttpServerTransportFactory relay behavior:
        a. Looks up ReverseHttpServerTransportFactory._registrations["c1c2c3c4-..."]
           → toCChannel: Machine C's existing registration IMessageChannel
             (the WebSocket channel Machine C opened to Machine A at startup)
           No ConnectToAsync call. No new channel-open is sent to Machine C.

        b. Relay pump started (two background tasks, both on Machine A):
               Task pumpBtoC: while (fromBChannel.Reader has items)
                                  toCChannel.Writer.TryWrite(frame)
               Task pumpCtoB: while (toCChannel.Reader has items)
                                  fromBChannel.Writer.TryWrite(frame)
           Completely transparent — no frame inspection, no protocol awareness.
           Machine B's subsequent frames (including "chat-client" channel-opens) are
           forwarded byte-for-byte to Machine C via toCChannel.

        c. Returns RelaySession (IAsyncDisposable) to HttpServerTransportFactory.
           Relay is now live. Machine C's existing listeners (ChatClientTransportListener,
           etc.) will handle B's subsequent channel-open frames transparently.

[Machine B]

9.  ReverseHttpForwardingTransportFactory receives relayChannel (relay pump on A is now live).
    Wraps relayChannel as ITransport:
        ITransport.ConnectToMessageChannelAsync(request)
            → writes channel-open frames into relayChannel
            → Machine A's pumpBtoC forwards them byte-for-byte to Machine C
        Returns relay-backed ITransport to step 5 (UserComputerProfileTransportFactory),
        which returns it to step 4 (ITransportFactoryRegistry.ConnectToAsync).

    Physical path:
        B→A : Machine B → HttpTransport (WebSocket) → Machine A relay pump (pumpBtoC)
        A→C : relay pump → ReverseHttpServerTransportFactory → Machine C registration channel
        C→B : Machine C → registration channel → Machine A relay pump (pumpCtoB) → HttpTransport → Machine B

10. [Machine B] relay-backed ITransport.ConnectToMessageChannelAsync({
            "type": "chat-client",
            "definition": <agentDefinitionJson>,
            "mcp-servers": [
              { "type": "mcp", "name": "workspace-gui",
                "connection": { "endpoint": "local://workspace-gui-listener" },
                "execution-target": "<Machine-B-profile-entity-id>" },
              { "type": "mcp", "name": "workspace-entity",
                "connection": { "endpoint": "local://workspace-entity-listener" },
                "execution-target": "<Machine-B-profile-entity-id>" },
              { "type": "mcp", "name": "local-dev-tools",
                "connection": { "endpoint": "stdio://dev-tools --mcp" },
                "execution-target": "<Machine-C-profile-entity-id>" }
            ]
        })
    → Sends a second channel-open frame through the relay.
      Machine A's pumpBtoC forwards it byte-for-byte to Machine C.

[Machine C]

11. Machine C's ReverseHttpClientTransportFactory dispatches the forwarded channel-open
    to Machine C's local ITransportRegistry.
    ChatClientTransportListener handles "chat-client" ✓.

12. ChatClientTransportListener.OnChannelOpenAsync(request, channel, ct) [Machine C]:

        a. AgentFactory.CreateChatClient(definition)
                provider = "github-copilot"
                → CopilotSdkChatClient (invokes cliPath="/usr/local/bin/gh" on Machine C)
                  CopilotSdkChatClient yields translated IAsyncEnumerable<ChatResponseUpdate>
                  via CopilotSdkStreamAdapter — pure stream translation, no internal routing.

        b. Open MCP sessions per the mcp-servers array [all running on Machine C]:
                workspace-gui (execution-target = Machine B):
                    ITransportFactoryRegistry.ConnectToAsync on Machine C resolves
                    user-computer-profile → Machine B's connection-descriptor
                    ReverseHttpForwardingTransportFactory on Machine C opens a separate relay channel
                    through Machine A to Machine B's WorkspaceGuiMcpServerListener.
                    McpClientOverTransport wraps it.

                workspace-entity (execution-target = Machine B):
                    Same path → Machine B's WorkspaceEntityMcpServerListener.
                    McpClientOverTransport wraps it.

                local-dev-tools (execution-target = Machine C):
                    entity-id matches local machine on Machine C
                    → LocalTransportFactory → McpTransportListener
                    → spawns "dev-tools --mcp" stdio process on Machine C.
                    McpClientOverTransport wraps it.

        c. Returns ChatClientTransportSession (IAsyncDisposable) owning
           CopilotSdkChatClient and all three McpClientOverTransport instances.
           HttpServerTransportFactory on Machine C tracks it for lease/disposal.

[Machine B]

13. IMessageChannel chatChannel returned to Machine B (linked to Machine C's session
    via the relay on Machine A).
    ChatClientOverTransport : IChatClient wrapping chatChannel.

14. provider = "github-copilot" → wrap with CopilotSubAgentRouterMiddleware [Machine B]:
        CopilotSubAgentRouterMiddleware(
            inner        = chatClientOverTransport,
            factory      = agentServices.RunningAgentChatFactory,
                           // factory creates new AgentChat entities on Machine A
            subAgentTable = this (AgentChat on Machine B implements ISubAgentTable),
            registry      = this (AgentChat on Machine B implements ISubAgentChatRegistry)
        )
    → This is the IChatClient for all subsequent initialization.

15. Register CopilotSubAgentRouterMiddleware as owned resource (disposed when
    AgentChat on Machine B disposes).

16. Create persistence providers [Machine B]:
        IncrementalPersistenceChatHistoryProvider(definition, store)
        AgentFrameworkChatHistoryProvider(persistenceProvider)
        StreamingPersistenceMiddleware(copilotSubAgentRouterMiddleware, persistenceProvider, store)
        → this.client = streamingMiddleware

17. chatOptions configuration [Machine B]:
        UseProvidedChatClientAsIs = true
            ← CopilotSdkChatClient is ISelfInvokingToolChatClient;
              ChatClientAgent skips its own FunctionInvocationMiddleware
        AgentFactory.ConfigureChatOptions:
            chatOptions.Instructions = "You are a helpful coding assistant."
            chatOptions.AdditionalProperties["agent_definition"] = definition

18. CreateRuntimeContextProviderRegistrationsAsync [Machine B — logical;
    channels route through the relay transparently]:
        workspace-gui    → McpToolContextProvider(McpClientOverTransport(
                               LocalTransport → WorkspaceGuiMcpServerListener on Machine B))
        workspace-entity → McpToolContextProvider(McpClientOverTransport(
                               LocalTransport → WorkspaceEntityMcpServerListener on Machine B))
        local-dev-tools  → McpToolContextProvider(McpClientOverTransport(
                               relay→A → Machine C → LocalTransport → dev-tools stdio))

19. chatClientAgent = new ChatClientAgent(streamingMiddleware, chatOptions) [Machine B]
        UseProvidedChatClientAsIs = true
        → Copilot SDK drives its own tool loop on Machine C; framework loop not used.

20. frameworkSession = await chatClientAgent.CreateSessionAsync()
    persistenceProvider.SetAgentSessionId(frameworkSession, agentSessionId)

21. LoadInitialHistory(store.ReadMessagesAsync(agentSessionId))  // empty first run

22. SetSession(new AgentChatSession(chatClientAgent, frameworkSession))
    SetAgentSessionId(resolvedAgentSessionId)

23. StartProcessingLoop() [Machine B]
        → Starts background Task draining queueManager,
          calls session.RunStreamAsync on each user input.

24. InitializeMcpToolsAsync() [Machine B — MCP list-tools requests]
        workspace-gui    → list-tools → LocalTransport → Machine B listener → tool definitions
                           → populates tool tree on Machine B; ToolsChanged fires
        workspace-entity → same path → Machine B entity tools
        local-dev-tools  → relay on A → Machine C stdio → dev-tools tool definitions
        Fires ToolsChanged → AgentChatViewModel refreshes tool list in Machine B's UI.
```

**Session ready.** The relay on Machine A is live but carries no chat content yet; Machine C's `CopilotSdkChatClient` is constructed and the `gh` CLI process is running.

---

#### Part 3 — Turn execution: streaming from Machine B through the relay to Machine C

```
[Machine B]

1.  User types a message in the agent chat UI on Machine B.
    AgentChat.EnqueueUserMessage(text) → queueManager.Enqueue(UserMessage)

2.  Processing loop dequeues, calls session.RunStreamAsync(messages, chatOptions, ct).

3.  StreamingPersistenceMiddleware.GetStreamingResponseAsync → delegates to inner.

4.  CopilotSubAgentRouterMiddleware.GetStreamingResponseAsync(messages, options, ct):
        Creates CopilotSubAgentRouter(rootWriter, registry, factory, subAgentTable).
        Calls inner.GetStreamingResponseAsync
            → ChatClientOverTransport.GetStreamingResponseAsync(messages, options, ct).

5.  ChatClientOverTransport sends process-streaming frame over relayChannel:
        { "type": "process-streaming", "content": [<ChatMessage array>] }
    (This is a channel-message frame on the relay-backed IMessageChannel.)

[Machine A — transparent relay pump]

6.  pumpBtoC reads the channel-message frame from fromBChannel and writes it
    byte-for-byte into toCChannel (Machine C's side).
    No inspection, no buffering beyond channel scheduling.

[Machine C]

7.  ChatClientTransportSession message pump receives the process-streaming frame.

8.  Calls CopilotSdkChatClient.GetStreamingResponseAsync(messages, options, ct):
        → Sends the conversation to the gh Copilot CLI running on Machine C.
        → CopilotSdkStreamAdapter.TranslateCopilotSdkSessionEvents
          translates raw SessionEvent objects into IAsyncEnumerable<ChatResponseUpdate>.

9.  For each ChatResponseUpdate, ChatClientTransportSession emits a streaming-update frame:
        { "type": "streaming-update", "content": <ChatResponseUpdate> }

10. Tool call (workspace-gui example) [Machine C]:
        CopilotSdkChatClient yields FunctionCallContent("workspace.openEntity", {...}).
        ChatClientTransportSession emits a streaming-update frame wrapping the FunctionCallContent.

        Simultaneously, CopilotSdkChatClient's internal tool execution layer calls
        McpClientOverTransport for workspace-gui:
            Machine C's McpClientOverTransport sends MCP tools/call request.
            Route:  Machine C
                    → ReverseHttpForwardingTransportFactory (on Machine C, opening relay back through A)
                    → relay on Machine A (separate relay session for the MCP channel)
                    → Machine A's WorkspaceGuiMcpServerListener executes the tool locally.
            Result: Machine A → relay → Machine C.
        CopilotSdkChatClient injects FunctionResultContent and continues the turn.

11. Tool call (local-dev-tools) [Machine C]:
        McpClientOverTransport for local-dev-tools routes to Machine C's LocalTransport
        → McpTransportListener → spawned "dev-tools --mcp" stdio process on Machine C.
        Entirely local; no relay or network hop.

[Machine A — transparent relay pump, reverse direction]

12. All streaming-update frames emitted by Machine C's ChatClientTransportSession travel:
        Machine C → registration channel → Machine A pumpCtoB → fromBChannel → Machine B.
    Machine A never parses frame content.

[Machine B]

13. ChatClientOverTransport receives each streaming-update frame, deserializes, yields
    ChatResponseUpdate to CopilotSubAgentRouter.RouteUpdatesAsync.

14. CopilotSubAgentRouter.RouteUpdatesAsync [Machine B] processes each update:

        Root text update
            → rootWriter.TryWrite(update)
            → surfaces in AgentChat.History and streaming UI on Machine B

        FunctionCallContent("copilot.subagent.start", args)
            → HandleSubAgentStartedAsync:
                a. factory.CreateAsync(SubAgentDefinition, newSessionId)
                       → creates new AgentChat entity "sub-1-session" on Machine A
                         (persisted in Machine A's MongoDB)
                   Sub-agent AgentChat uses CopilotSubAgentChatClient : IChatClient
                   (a Channel<ChatResponseUpdate> backed pure sink)
                b. subAgentTable.Add(subAgentChat)
                       → sub-agent appears in SubAgentsContainerViewModel on Machine B

        Subsequent updates tagged parent_tool_call_id = "sub-1"
            → ICopilotSubAgentReceiver.Push(update)
            → sub-agent AgentChat.History and UI on Machine A updated

        FunctionResultContent("sub-1", { event: "completed" })
            → SetCompletionState(Succeeded); sub-agent lease disposed

15. CopilotSdkChatClient finishes the turn.
    Machine C's ChatClientTransportSession emits:
        { "type": "streaming-update-complete" }
    Relayed A→B. ChatClientOverTransport completes the IAsyncEnumerable.
    CopilotSubAgentRouter finalizes routing.

16. StreamingPersistenceMiddleware and IncrementalPersistenceChatHistoryProvider
    persist the completed turn to Machine A's MongoDB.
```

---

#### Part 4 — Teardown: relay channel close propagation

```
[Machine B — session close]

1.  AgentChat.DisposeAsync on Machine B triggers the disposal chain:
        CopilotSubAgentRouterMiddleware.DisposeAsync
        → ChatClientOverTransport.DisposeAsync
        → IMessageChannel.DisposeAsync (the relay-backed channel)
    Sends channel-close frame into relayChannel:
        { "type": "channel-close", "channel-id": "<relay-channel-guid>" }

[Machine A — relay pump]

2.  pumpBtoC reads the channel-close frame from fromBChannel and forwards it
    byte-for-byte to toCChannel (Machine C).
    Both pump tasks detect their source channel has completed and exit.

3.  RelaySession.DisposeAsync (the IAsyncDisposable returned in Part 2 step 8c):
        Cancels the two pump tasks (if not yet exited).
        fromBChannel.DisposeAsync()
        toCChannel.DisposeAsync()
    HttpServerTransportFactory removes the relay-session handle from its live-session map.

[Machine C]

4.  ReverseHttpClientTransportFactory receives the forwarded channel-close.
    Marks the corresponding IMessageChannel as complete (ChannelReader completed).

5.  ChatClientTransportSession.DisposeAsync on Machine C:
        Cancels the in-progress streaming turn (CancellationTokenSource.Cancel()).
        CopilotSdkChatClient.DisposeAsync()
            → terminates the gh CLI process on Machine C; CopilotSession closed.
        McpClientOverTransport x3 disposed:
            workspace-gui    → MCP shutdown sent; relay channel to Machine A closed.
            workspace-entity → same.
            local-dev-tools  → MCP shutdown sent; "dev-tools --mcp" process killed.
        IMessageChannel session object disposed.

[Machine B]

6.  ReverseHttpForwardingTransportFactory disposes the relay-backed ITransport.
    If no other channels remain open on the underlying HttpTransport, sends
    transport-close frame to Machine A and closes the WebSocket:
        { "type": "transport-close" }
    Machine A's ServerHttpTransport for the Machine B connection receives transport-close
    → disposes remaining channels/streams → removes transport from HttpServerTransportFactory.

hub-urls persistence rule:
    Machine C's hub-urls list in its user-computer-profile entity is NOT cleared
    by teardown of a single relay session.
    It persists until Machine C's registration channel to Machine A itself closes
    (i.e. Machine C's HttpTransport WebSocket to Machine A drops), at which point
    ReverseHttpClientTransportFactory removes its slot from hub-urls.
    Slot removal is tied to registration lifetime, not individual relay session lifetime.
```

**Teardown complete.** All per-turn objects are disposed. Machine A retains no per-turn state. Machine C's `user-computer-profile.connection-descriptor` continues to advertise `hub-urls` for future sessions until Machine C's registration channel to Machine A closes.

---

#### Object graph during a live turn

```
Machine B                            Machine A (hub)                 Machine C
─────────────────────────────────    ─────────────────────────────   ─────────────────────────────
AgentChat                            HttpServerTransportFactory       HttpServerTransportFactory
  └─ StreamingPersistenceMiddleware    └─ ServerHttpTransport(B)        └─ RegistrationSession
       └─ CopilotSubAgentRouterMiddleware  └─ RelaySession              ReverseHttpClientTransportFactory
            └─ ChatClientOverTransport          │                          └─ registrationChannel (→A)
                 └─ IMessageChannel ── ReverseHttpServerTransportFactory (relay pump)   ChatClientTransportSession
                   (relay, B-side)               ├─ pumpBtoC (bg task)     └─ CopilotSdkChatClient
                                                 └─ pumpCtoB (bg task)          └─ CopilotSdkStreamAdapter
                                     ReverseHttpServerTransportFactory               └─ gh CLI process
                                       └─ _registrations["c1c2c3c4-..."]
                                            = registrationChannel(→C) ────────┐  McpClientOverTransport x3
ReverseHttpForwardingTransportFactory                                                       │    workspace-gui
  └─ HttpTransport (WS to A)                                                   │      → relay(C→A→B)→WorkspaceGuiMcpServerListener on Machine B
                                                                               │    workspace-entity
UserComputerProfileTransportFactory                                            │      → relay(C→A→B)→WorkspaceEntityMcpServerListener on Machine B
  (resolved "c1c2c3c4-..."                                                     │    local-dev-tools
   → connection-descriptor                                                     │      → LocalTransport
   → dispatched to ReverseHttpForwardingTransportFactory)                                   │           → dev-tools --mcp (stdio)
                                                                               │
sub-agent AgentChats (if sub-agent turn):                                      │
  AgentChat "sub-1" (MongoDB on Machine A)  ◄──────────────────────────────────┘
    └─ CopilotSubAgentChatClient              [CopilotSubAgentRouterMiddleware on B
         (Channel<ChatResponseUpdate>)         pushes updates; factory creates entities on A]
```

---

### Summary: Manifest → Running Instance

| Step | S1 (Local OpenAI) | S2 (Remote OpenAI) | S3 (Remote Copilot) | S4 (Local Copilot) | S5 (Relay Copilot) |
|---|---|---|---|---|---|
| Manifest source | `agent-manifest` entity in PW entity store | same | same | same | same |
| Manifest loaded | `AgentManifestLoader.LoadManifestFromJson` | same | same | same | same |
| Parameter substitution | `AgentDefinitionParameterSubstitutor` (if params supplied) | same | same | same | same |
| Resource resolution | `ToolResourceFactory.ResolveToolResourceAsync` per `resources` entry | same | same | same | same |
| Trust profile resolved | null (local) | `my-remote-workstation` → TrustProfile | same | null (local) | `machine-c-workstation` → TrustProfile |
| Execution target | `{ "type": "local" }` | `{ "type": "user-computer-profile", ... }` | same | `{ "type": "local" }` | `{ "type": "user-computer-profile", ... }` (Machine C) |
| Transport | `LocalTransport` | `ReverseHttpTransport` → Machine B | same | `LocalTransport` | `ReverseHttpForwardingTransportFactory` → relay via A → Machine C |
| `IChatClient` on executor | `OpenAiChatClient` | `OpenAiChatClient` | `CopilotSdkChatClient` (pure stream) | `CopilotSdkChatClient` (pure stream) | `CopilotSdkChatClient` on Machine C (pure stream) |
| Sub-agent middleware | none | none | `CopilotSubAgentRouterMiddleware` on Machine A | same | `CopilotSubAgentRouterMiddleware` on Machine B; entities on Machine A |
| MCP tool transport | LocalTransport | ReverseHttp (workspace) + Local (dev-tools) | same | LocalTransport (all) | Relay→ReverseHttp (workspace on A) + Local (dev-tools on C) |
| LLM API calls | Machine A | Machine B | Machine B (via CLI) | Machine A (local CLI) | Machine C (via CLI) |
| Persistence | Machine A MongoDB | Machine A MongoDB | Machine A MongoDB | Machine A MongoDB | Machine A MongoDB |
| Sub-agent entities | n/a | n/a | Machine A (created by router middleware) | Machine A | Machine A (created by router middleware on Machine B) |
| Session statefulness | Thin (full history each turn) | Thin (full history each turn) | Stateful (`CopilotSdkChatClient` alive for channel lifetime) | Stateful (`CopilotSdkChatClient` alive for channel lifetime) | Stateful (`CopilotSdkChatClient` on C alive for relay channel lifetime) |

---

### New Interfaces and Types Required

**Removed from earlier design:** `IToolCallRouter`, `LocalToolCallRouter`, `RemoteToolCallRouter`, `ToolCallFrameDispatcher`, `ToolExecutionClass` — all replaced by the MCP server wrapping approach. Tools run via standard MCP protocol over `McpClientOverTransport`; no separate tool-call frames exist in the chat channel.

**Added: `CopilotSubAgentRouterMiddleware`** — extracted from `CopilotSubAgentRouter` (itself from #808 branch). See Scenario 3 for the full class sketch. Applied by `AgentFactory` when provider is `github-copilot` or `github-copilot-subagent`.

**Added: `ChatClientTransportListener`** — server-side handler for `{ "type": "chat-client" }` channel-open requests. Calls `AgentFactory.CreateChatClient(definition)` and opens `McpClientOverTransport` sessions per `mcp-servers` descriptor array. Returns a `ChatClientTransportSession` (`IAsyncDisposable`) owning all opened sessions.

**Added: `McpClientOverTransport`** — wraps an `ITransport` to provide MCP client semantics. Replaces `StdioClientTransport` / `HttpClientTransport` for remote MCP servers.

**Added: `ExecutionTargetResolver`** — reads `default-execution-target` from the resolved `TrustProfile`, produces a `` descriptor for `ITransportFactoryRegistry`.

---

### `ITrustedExecutor` transition

The existing `ITrustedExecutor` interface is **not deleted** — it is **re-implemented** as a thin adapter over `ITransportFactoryRegistry`. This preserves backward compatibility for all existing callers (shortcut handlers, `AgentServices`, etc.) during the transition without requiring them all to be migrated at once.

**Phase 1 (T11):** Delete the current internal implementations of `ITrustedExecutor` (`LocalTrustedExecutor`, `WebRemoteTrustedExecutor`, etc.) and replace them with a single `TransportTrustedExecutor` adapter class that implements `ITrustedExecutor` by delegating to `ITransportFactoryRegistry`. All existing callers continue to use `ITrustedExecutor` unchanged.

**Phase 2 (future, post-transport):** Once all callers have been verified to work correctly through the adapter, migrate them directly to `ITransportFactoryRegistry` / `ITransport` and delete `ITrustedExecutor` entirely. This is a separate task not in this plan.

T11 in Phase 5 is updated accordingly: it delivers `TransportTrustedExecutor`, not a deletion of the interface.

The table below shows the eventual (Phase 2) mapping from old abstractions to new ones:

| Old | New |
|---|---|
| `ITrustedExecutor.CreateAgentChatAsync` | `ChatClientOverTransport` via `ITransport.ConnectToMessageChannelAsync({ "type": "chat-client" })` |
| `ITrustedExecutor.OpenStreamAsync` | `ShellOverTransport` via `ITransport.ConnectToStreamAsync({ "type": "shell" })` |
| `TrustedExecutorSelector` | `AgentTrustProfileResolver` + `ExecutionTargetResolver` + `ITransportFactoryRegistry.ConnectToAsync` |
| `ReverseTrustedExecutor` | `ReverseHttpServerTransportFactory` + `ReverseHttpTransport` |
| `RemoteTrustedExecutor` | `HttpClientTransportFactory` + `HttpTransport` |
| `LocalTrustedExecutor` | `LocalTransportFactory` + `LocalTransport` |

---

## Integration Testing

### Goals

- Verify each scenario's object graph is wired correctly end-to-end (transport → chat-client → MCP → tool calls → responses).
- Verify the relay pump (Scenario 5) forwards frames correctly without inspection.
- Verify error paths: relay target not found, lease expiry, connection drop mid-turn.
- Run entirely in-process: no real devtunnels, no real HTTP servers, no real Copilot SDK.
- Fast: each test runs in milliseconds; no polling loops or real timers.

---

### Test transport infrastructure

These test-only classes live in a new `Phantom.Workspaces.Transport.Tests` project (or, if the dependency graph permits, within `Phantom.Workspaces.Llm.Core.Tests`).

**`InProcessTransport`**

Creates a matched pair of `ITransport` instances connected in memory via `Channel<TransportFrame>` (one channel per direction). Used to simulate a WebSocket connection between two machines without any network.

```csharp
// Factory method — returns a connected pair.
static (ITransport server, ITransport client) Create();
```

Both sides honour the full `TransportFrame` protocol (channel-open, channel-close, lease frames, data frames). `InProcessTransport` replaces `LocalTransport` in tests that need to simulate a cross-machine boundary — for example, the Machine A ↔ Machine B relay in Scenario 5.

**`InProcessHttpServerTransportFactory`**

An in-process substitute for `HttpServerTransportFactory` that accepts `InProcessTransportPair.serverSide` connections directly instead of listening on a real TCP port. Tests inject connections by calling `AcceptAsync(ITransport serverSide)` rather than by opening an HTTP socket.

**`InProcessReverseHubFixture`**

Sets up a simulated hub (Machine A) with `InProcessHttpServerTransportFactory` + `ReverseHttpServerTransportFactory` (including relay behavior) all wired together in-process. Provides:

```csharp
// Simulates Machine B or C registering with the hub.
// Returns the client-side ITransport for the registering machine.
Task<ITransport> SimulateClientRegistrationAsync(Guid machineEntityId);
```

Used by Scenario 2, 3, and 5 tests.

---

### Per-scenario test approach

#### Scenario 1 (Local, OpenAI)

| Layer | Fake / real |
|---|---|
| Transport | `LocalTransport` (real — no network involved) |
| `IChatClient` | `DeterministicTestChatClient` (already in codebase) |
| MCP | In-process `McpClientOverTransport` connected to an `InProcessTransport` |

**Arrange:** Create `LocalTransport`. Wire `AgentChat` → `ChatClientOverTransport` → `LocalTransport` → `ChatClientTransportListener` → `DeterministicTestChatClient`. Attach an in-process MCP server via `McpClientOverTransport` + `InProcessTransport`.

**Act:** Enqueue a scripted tool-call response in `DeterministicTestChatClient`. Submit a turn via `AgentChat`.

**Assert:** Turn completes; tool call is dispatched to the in-process MCP server; response arrives in `AgentChat.History`.

---

#### Scenario 2 (Remote executor, OpenAI)

| Layer | Fake / real |
|---|---|
| Hub (Machine A) | `InProcessReverseHubFixture` |
| Machine B transport | `ReverseHttpServerTransportFactory` on A; `InProcessTransport` as the physical link |
| `IChatClient` on B | `DeterministicTestChatClient` |

**Arrange:** Start `InProcessReverseHubFixture`. Call `SimulateClientRegistrationAsync` to register Machine B. On Machine A, create `AgentChat` pointing to a `ChatClientOverTransport` that targets Machine B via the reverse transport.

**Act:** Enqueue a scripted response in Machine B's `DeterministicTestChatClient`. Submit a turn.

**Assert:** The turn flows Machine A → `ChatClientOverTransport` → transport layer → Machine B's `ChatClientTransportSession` → `DeterministicTestChatClient` → response back to Machine A's `AgentChat.History`.

---

#### Scenario 3 (Remote executor, Copilot SDK)

> **Copilot SDK test rule:** Scenarios 3, 4, and 5 must resolve the chat client through `AgentFactory.CreateChatClient(definition)` using an `AgentDefinition` with `provider: "github-copilot"` and a BYOK endpoint aimed at `ScriptedByokChatServer`. Never hand-construct `CopilotSdkChatClient` in tests. This exercises the real factory wiring and keeps Copilot-specific concerns out of test harness classes.

| Layer | Fake / real |
|---|---|
| Hub (Machine A) | `InProcessReverseHubFixture` |
| Machine B `AgentDefinition` | `provider: "github-copilot"`, BYOK endpoint → `ScriptedByokChatServer` on `LocalTransport` |
| Machine B chat client | `AgentFactory.CreateChatClient(definition)` (constructs `CopilotSdkChatClient` internally) |
| Sub-agent middleware | `CopilotSubAgentRouterMiddleware` on Machine A |

Same transport wiring as Scenario 2. The executor machine (Machine B) uses an `AgentDefinition` with `provider: "github-copilot"` and a BYOK endpoint URL pointing to a `ScriptedByokChatServer` running over a `LocalTransport` channel on Machine B. `AgentFactory.CreateChatClient(definition)` resolves and constructs the `CopilotSdkChatClient` internally — the test never instantiates it directly. `ScriptedByokChatServer` wraps a `DeterministicTestChatClient` to provide scripted streaming responses including sub-agent lifecycle events. `CopilotSubAgentRouterMiddleware` wraps the resolved client on Machine A as in the real scenario.

**Assert:** Sub-agent lifecycle events cause `AgentChat` entities to be created on Machine A; update routing directs responses to the correct `AgentChat` sink.

---

#### Scenario 4 (Local, Copilot SDK)

Same as Scenario 3 except all transports are `LocalTransport` — no relay, no hub. The local `AgentDefinition` uses `provider: "github-copilot"` with a BYOK endpoint pointing to an in-process `ScriptedByokChatServer`. `AgentFactory.CreateChatClient` is the entry point; `CopilotSdkChatClient` is never directly constructed. Structurally identical to Scenario 3 in test code; the transport abstraction guarantees identical behaviour regardless of the physical link.

---

#### Scenario 5 (Hub relay, Copilot SDK)

| Layer | Fake / real |
|---|---|
| Hub (Machine A) | `InProcessReverseHubFixture` |
| Machine B transport | `ReverseHttpForwardingTransportFactory` connecting to the fixture hub, requesting relay to Machine C's entity-id |
| Machine C transport | Registered with the fixture hub via `SimulateClientRegistrationAsync` |
| Machine C `AgentDefinition` | `provider: "github-copilot"`, BYOK endpoint → `ScriptedByokChatServer` on `LocalTransport` |
| Machine C chat client | `AgentFactory.CreateChatClient(definition)` via `ChatClientTransportListener` (constructs `CopilotSdkChatClient` internally) |

**Arrange:** Register both Machine B and Machine C with the hub fixture. On Machine B, wire `CopilotSubAgentRouterMiddleware` → `ChatClientOverTransport` → `ReverseHttpForwardingTransportFactory`. Machine C's `ChatClientTransportListener` creates the chat client via `AgentFactory.CreateChatClient` using a BYOK-configured `AgentDefinition`. The BYOK endpoint points to a `ScriptedByokChatServer` on Machine C.

**Act:** Enqueue scripted sub-agent events in Machine C's `ScriptedByokChatServer`. Submit a turn from Machine B.

**Assert:**
- Frames travel Machine B → relay pump on A → Machine C (byte-transparent: Machine A never parses chat content).
- `CopilotSubAgentRouterMiddleware` on Machine B creates sub-agent `AgentChat` entities on Machine A.
- Relay channel teardown propagates correctly when Machine C closes its side of the `InProcessTransportPair`.

---

### Error path tests

**Relay target not registered**

`ReverseHttpServerTransportFactory` relay behavior receives a `reverse-http` channel-open for an entity-id that has no active registration in `ReverseHttpServerTransportFactory`.

Assert: a `channel-open-error` frame is returned to Machine B; `ReverseHttpForwardingTransportFactory.ConnectToAsync` throws `TransportException`.

---

**Lease expiry mid-turn**

Advance a fake timer past the 90-second lease threshold while a `GetStreamingResponseAsync` call is in flight.

Assert: transport closes cleanly; the in-progress `GetStreamingResponseAsync` receives a cancellation rather than hanging.

---

**Hub URL race — one URL fails**

In the Scenario 5 fixture, provide two hub URLs where the first connection attempt always fails (the `InProcessHttpServerTransportFactory` rejects the first connection).

Assert: `ReverseHttpForwardingTransportFactory` falls back to the second URL and the turn completes successfully.

---

**All hub URLs fail**

All parallel connection attempts fail (both `InProcessTransportPair` server sides are closed before connection).

Assert: `ReverseHttpForwardingTransportFactory.ConnectToAsync` throws within a bounded timeout rather than hanging indefinitely.

---

**Machine C crash (stale hub-urls)**

Register Machine C, then close its `ITransport` without deregistering — simulating a crash. Machine B attempts a relay to Machine C.

Assert: `ReverseHttpServerTransportFactory` relay behavior returns `channel-open-error`. When Machine C restarts and re-registers, its `hub-urls` list is cleared so stale entries cannot be reused.

---

### Test project structure

```
Phantom.Workspaces.Transport.Tests/
  Infrastructure/
    InProcessTransport.cs
    InProcessHttpServerTransportFactory.cs
    InProcessReverseHubFixture.cs
  Scenarios/
    Scenario1_LocalOpenAiTests.cs
    Scenario2_RemoteOpenAiTests.cs
    Scenario3_RemoteCopilotSdkTests.cs
    Scenario4_LocalCopilotSdkTests.cs
    Scenario5_HubRelayTests.cs
  ErrorPaths/
    RelayErrorTests.cs
    LeaseExpiryTests.cs
    HubUrlFallbackTests.cs
```

All tests use `[Fact]` — transport tests are pure async and carry no Avalonia dependency (`PhantomAvaloniaFact` is not used). Tests that exercise `ForegroundScheduler` use `SingleThreadPump` from the existing test infrastructure.

---

### Relationship to existing test infrastructure

| Class | Location | Role in transport tests |
|---|---|---|
| `DeterministicTestChatClient` | `Phantom.Workspaces.Llm.Core.Tests` | Scripted, readiness-gated `IChatClient`. Used directly as the fake executor-side chat client in Scenarios 1 and 2. |
| `ScriptedByokChatServer` | `Phantom.Workspaces.Llm.Core.Tests` | Protocol-generic scripted BYOK wire server. Used in Scenarios 3–5 as the BYOK endpoint target; accessed via a BYOK endpoint URL in the `AgentDefinition`, not passed directly as `IChatClient`. |
| `SingleThreadPump` | `AgentChatForegroundContextTests` (nested class) | Drives `ForegroundScheduler` on a dedicated thread. Used in any transport test that needs the scheduler pumped. |
| `InProcessTransport` | `Phantom.Workspaces.Transport.Tests` (new) | Fundamental building block for all multi-machine simulation; replaces real network connections in every scenario test. |
