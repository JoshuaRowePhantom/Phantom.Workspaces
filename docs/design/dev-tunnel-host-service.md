# DevTunnelHostService design

## Purpose

Define a `DevTunnelHostService` that, when remote hosting is enabled, automatically exposes the
Phantom.Workspaces GUI's local web server (`WorkspacesWebHost`) through a Microsoft Dev Tunnel and
forwards the port the GUI is listening on — **without** shelling out to the `devtunnel` CLI.

This supersedes the CLI-based `DevTunnelManager` sketched in
[`devtunnels-web-access.md`](devtunnels-web-access.md): hosting and port forwarding are performed
**in-process via the Microsoft Dev Tunnels SDK**, which is more robust (no external process to
discover/install/orchestrate), testable (mockable client), and lets us surface live state directly.

## Current state (what exists today)

- `WorkspacesWebHost` runs Kestrel on `RemoteHostingSettings.ListenUrl` (e.g. `http://localhost:5280`)
  and exposes `string? ListenUrl`. It does **not** create or forward a tunnel.
- `DevTunnelConfiguration` (`TunnelId`, `TunnelName`, `HostedPorts`, `Protocol`, `AccessMode`,
  `AccessTokenSource`) is persisted via settings view models but is otherwise **inert** at runtime —
  nothing populates `TunnelId`/`HostedPorts` from a live tunnel.
- Client-side only: `WebClientDataAccessLayer` / `ReverseExecutionClientHost` send
  `X-Tunnel-Authorization: tunnel <token>` when *connecting through* an externally-provisioned tunnel.
- `ConnectionStatusViewModel.AccessPoint` currently falls back to the local `ListenUrl` because no
  real public tunnel URL is captured.

`DevTunnelHostService` fills the gap: it creates/hosts the tunnel and produces the real public access
point.

## SDK dependencies

Use the official Microsoft Dev Tunnels .NET SDK (no CLI process):

- `Microsoft.DevTunnels.Contracts` — `Tunnel`, `TunnelPort`, `TunnelAccessControl`,
  `TunnelAccessControlEntry`, `TunnelAccessScopes`, `TunnelEndpoint`.
- `Microsoft.DevTunnels.Management` — `TunnelManagementClient` (management-plane REST client:
  create/get/update/delete tunnels and ports, manage access control, mint access tokens).
- `Microsoft.DevTunnels.Connections` — `TunnelRelayTunnelHost` (in-process host that accepts relay
  connections and forwards them to the local listening port).

These are added as `PackageReference`s to `Phantom.Workspaces` (the GUI/composition project that owns
`WorkspacesWebHost`). The service is abstracted behind interfaces so the SDK types do not leak into
view models and can be faked in tests.

## Responsibilities

`DevTunnelHostService` owns the full lifecycle of a Workspaces-owned tunnel:

1. **Ensure tunnel** — get-or-create a persistent tunnel for this machine via the management client,
   reusing `DevTunnelConfiguration.TunnelId`/`TunnelName` when present, otherwise creating one and
   persisting the resulting id/name back to configuration.
2. **Configure port (single-port invariant)** — ensure **exactly one** `TunnelPort` exists on the
   tunnel, for the GUI's current listening port (parsed from `WorkspacesWebHost.ListenUrl`) with the
   configured `Protocol` (default `https`). Any previously-forwarded ports that no longer match the
   current listen port are removed, so a tunnel always has a single, unambiguous forwarded port. This
   single-port invariant is what makes the **additive** tunnel-name client mode possible: a client
   can connect by tunnel name and discover the port automatically (see "Client tunnel connection
   scheme") — this does **not** remove the existing explicit access-point mode.
3. **Apply access control** — set the tunnel/port access to match
   `DevTunnelConfiguration.AccessMode` (Private / Token / Anonymous).
4. **Host** — start a `TunnelRelayTunnelHost` to accept relay traffic and forward it to the local
   port (`127.0.0.1:<listenPort>`), so the tunnel "follows" whatever the GUI is listening on.
5. **Resolve access point** — compute the public tunnel URL for the hosted port and publish it.
6. **Track state** — expose live status (Stopped / Starting / Hosting / Reconnecting / Error + last
   error) for the network status display and the top-right global status dropdown.
7. **Reconfigure / stop** — restart cleanly when settings change (access mode, token source, listen
   URL) and stop/dispose on shutdown, releasing the relay host (the tunnel resource itself persists
   in the service so the URL is stable across restarts).

## Public shape (abstractions)

```csharp
namespace Phantom.Workspaces.Services;

public enum DevTunnelHostState { Stopped, Starting, Hosting, Reconnecting, Error }

public sealed record DevTunnelHostStatus(
    DevTunnelHostState State,
    string? AccessPointUrl,   // public https URL of the hosted port, when Hosting
    string? TunnelId,
    DevTunnelAccessMode AccessMode,
    string? LastError);

public interface IDevTunnelHostService : IAsyncDisposable
{
    DevTunnelHostStatus Status { get; }
    event EventHandler<DevTunnelHostStatus>? StatusChanged;

    /// Ensures the tunnel exists, forwards the supplied local port, and begins hosting.
    Task StartAsync(int localPort, DevTunnelConfiguration configuration, CancellationToken ct = default);

    /// Applies a configuration change (e.g. access mode) without losing the tunnel identity.
    Task ReconfigureAsync(int localPort, DevTunnelConfiguration configuration, CancellationToken ct = default);

    Task StopAsync(CancellationToken ct = default);
}
```

Supporting seams kept behind interfaces for testability:

- `IDevTunnelManagementClientFactory` → wraps construction of `TunnelManagementClient` (user agent +
  access-token callback). Fakeable in tests.
- `IDevTunnelAuthTokenProvider` → supplies the **management** identity token (Microsoft account /
  Entra ID / GitHub) used to create/own tunnels, resolved from environment or OS secret store via a
  source name (never a raw token in tracked files). Mirrors the existing
  `DevTunnelConfiguration.AccessTokenSource` convention.
- `IDevTunnelRelayHost` → thin wrapper over `TunnelRelayTunnelHost` so hosting can be faked.

The concrete `DevTunnelHostService` composes these; the SDK types live only in the concrete
implementations.

## Lifecycle and integration

1. **Startup wiring** — in `MainWindowViewModel.InitializeWebHostAsync`, after
   `webHost.StartAsync(...)` succeeds and `webHost.ListenUrl` is known:
   - parse the local port from `ListenUrl`;
   - if `RemoteHosting.Enabled` and dev tunnel hosting is enabled, call
     `devTunnelHostService.StartAsync(localPort, configuration.DevTunnel)`.
   - The service's `StatusChanged` updates `ConnectionStatusViewModel` — specifically, on `Hosting`
     it calls `SetAccessPoint(status.AccessPointUrl)`; on stop/error it clears or annotates it. This
     **replaces** the current local-`ListenUrl` fallback with the real public tunnel URL.
2. **Web server coupling** — the tunnel host starts only after Kestrel binds; the relay forwards to
   `127.0.0.1:<listenPort>`. If `ListenUrl` changes, `ReconfigureAsync` re-points the forwarded port.
3. **Settings/reconfiguration** — settings flows write `DevTunnelConfiguration`; applying them calls
   `ReconfigureAsync` (ties into the pending `live-service-reconfiguration` work so changes apply
   without an app restart). Changing `AccessMode` updates access control in place; changing identity
   restarts the relay host.
4. **Shutdown** — `MainWindowViewModel` disposal awaits `devTunnelHostService.DisposeAsync()` after
   `webHost.DisposeAsync()`.

## Port-forwarding flow (SDK)

1. `TunnelManagementClient.GetTunnelAsync` / `CreateTunnelAsync` to ensure the tunnel (using
   `TunnelId`/`TunnelName`).
2. `CreateOrUpdateTunnelPortAsync` with `new TunnelPort { PortNumber = localPort, Protocol = "https" }`.
3. Build access control (see below) and `UpdateTunnelAsync`.
4. `TunnelRelayTunnelHost.StartAsync(tunnel)` — the host connects outbound to the relay and forwards
   inbound relay streams to `127.0.0.1:<localPort>`. No inbound firewall ports are opened.
5. Resolve the public URL from the tunnel's `TunnelEndpoint` for `localPort` (the
   `*.devtunnels.ms` relay URI), publish it as the access point.
6. Persist `TunnelId`, resolved `TunnelName`, and `HostedPorts = [localPort]` back to
   `DevTunnelConfiguration`.

## Client tunnel connection scheme

The web client (`Phantom.Workspaces.Data.Web.Client` in `DataAccessMode.DevTunnelWeb`) supports
**two** ways to locate a host, side by side — the tunnel-name mode is **added**, the existing explicit
access-point mode is **retained**:

1. **Explicit access point (existing).** The user supplies a full relay endpoint URL
   (`WebEndpoint`, e.g. `https://<id>-<port>.<cluster>.devtunnels.ms/`). Used as-is. No management
   lookup required. This remains fully supported and is the right choice when the endpoint is fixed or
   the client has no management access.
2. **Tunnel name (new).** The user supplies just a **tunnel name** (and, for Token mode, a token
   source). The client resolves the live endpoint at connect time via a new
   `IDevTunnelEndpointResolver`:
   - look up the tunnel by name with a (read-scoped) `TunnelManagementClient`;
   - read its **single** forwarded `TunnelPort` (relying on the host's single-port invariant) — no
     port number is configured by the user;
   - construct the relay endpoint URI for that port (and attach the `X-Tunnel-Authorization` token for
     Token mode);
   - hand the resolved base URI to `WebClientDataAccessLayer`.

   Because the port is discovered, the host can change its listening port (the host re-points its
   single forwarded port) and the client still reconnects to the right place by name.

### Resolution + reconnect refresh

- The resolved endpoint is **cached** for the life of a healthy connection.
- A client-side `DevTunnelConnectionMonitor` watches the web/data connection. **On connection
  failure** (request errors, websocket drop, DNS/relay failure) it **re-resolves** the tunnel by name
  — picking up a changed port or a re-created tunnel endpoint — and reconnects, with bounded
  exponential backoff and jitter, **without restarting the workspace**.
- Re-resolution is event-driven (triggered by failures), not a fixed-interval poll, so a healthy
  connection does no extra management calls; a persistently failing one keeps retrying at the backoff
  cadence until it succeeds or the workspace is closed.
- The explicit-access-point mode reconnects to the same fixed URL on failure (no re-resolution, since
  there is nothing to discover).
- Reconnect state is surfaced to the workspace's connection status (Connected / Reconnecting / Failed
  + last error) so the user sees recovery without taking action.

### Config and abstractions (client side)

- `DevTunnelConfiguration` (or the repository `DevTunnelWeb` settings) gains an optional
  **`TunnelName`** alongside the existing `WebEndpoint`; exactly one of the two identifies the host.
  When `TunnelName` is set, `WebEndpoint` is resolved dynamically; when only `WebEndpoint` is set,
  behavior is unchanged.
- `IDevTunnelEndpointResolver.ResolveAsync(tunnelName, accessMode, tokenSource, ct)` →
  `(Uri baseUri, string? tunnelAuthToken)`. Backed by `TunnelManagementClient`; fakeable in tests.
- `DevTunnelConnectionMonitor` wraps the web DAL connection with the resolver + backoff policy and
  raises status events.

## Authentication model

Two distinct token concerns (kept separate):

1. **Management / hosting identity** — needed to create and host the tunnel. Resolved by
   `IDevTunnelAuthTokenProvider` from the configured source (env var / OS keychain), consistent with
   `AccessTokenSource`. Supplied to `TunnelManagementClient` via its access-token callback so it can
   refresh without restart.
2. **Tunnel access (clients connecting in)** — governed by `DevTunnelAccessMode`:
   - **Private** (default): only the owning identity / authorized identities; clients authenticate to
     the relay.
   - **Token**: anonymous-but-token — clients send `X-Tunnel-Authorization: tunnel <token>`
     (already supported by `WebClientDataAccessLayer`). The service mints a port/tunnel-scoped,
     short-lived access token via the management client when this mode is selected.
   - **Anonymous**: opt-in only, visibly warned in the UI (existing
     `IsAnonymousAccessWarningVisible`). Not the default for workspace data APIs.

Tunnel authentication **is** the authentication boundary we rely on for remote access: the dev
tunnel's access control (Private identity / Token / Anonymous) is what gates whether a remote client
can reach the workspace web endpoints at all. There is no separate application-level identity layer
in front of the web data-access API — admittance to the tunnel is admittance to the API. Consequently:

- The chosen `DevTunnelAccessMode` is a real security decision, not a transport convenience (this is
  why **Anonymous** must be opt-in and visibly warned, and why **Private** is the default).
- Token mode's `X-Tunnel-Authorization` token is the client's credential; it must be short-lived,
  tunnel/port-scoped, and never persisted raw.
- Any future per-request application authorization (e.g. per-workspace ACLs) would be an **additional**
  layer on top of tunnel auth, not a replacement for it; until that exists, tunnel auth is the
  authentication we depend on.

## Status surfaced to UI

`DevTunnelHostStatus` drives:

- `ConnectionStatusViewModel.AccessPoint` (the copyable/selectable access-point text box) — set to the
  public tunnel URL while `Hosting`.
- The top-right global status dropdown row (per `devtunnels-web-access.md`): state indicator, public
  endpoint, access mode, last error, and quick actions (restart / copy endpoint).

## UI changes (additive)

These are additive — the existing access-point inputs stay; a tunnel-name option is added alongside.

1. **Host side (`RemoteAccessSettingsView` / `RemoteAccessSettingsViewModel`).** Keep the current
   access-point/listen settings. Surface the resolved **tunnel name** and the live public access point
   (read-only, copyable) once hosting, plus the host status. The host already enforces a single port,
   so no port field is shown.
2. **Client side (`DevTunnelWebSettingsView` / `DevTunnelWebSettingsViewModel`,
   `RepositoryConnectionModeViewModels`).** Add a **"Connect by"** choice with two options:
   - **Access point** (existing) — the explicit `WebEndpoint` text box, unchanged.
   - **Tunnel name** (new) — a single tunnel-name text box; no port input (auto-discovered). For Token
     access mode, the existing token-source field applies to both options.
   The two options are mutually exclusive for a given connection; validation requires exactly one of
   `WebEndpoint` / `TunnelName`. Default remains the access-point option so existing configurations are
   untouched.
3. **Connection status.** The workspace's connection status reflects the client
   `DevTunnelConnectionMonitor` state (Connected / Reconnecting / Failed + last error) so name-based
   reconnects are visible.

## Threading, async, resilience

- All network/SDK calls are asynchronous; the GUI must never block (no
  `GetAwaiter().GetResult()`), consistent with the codebase's async-data-access rule. Non-UI library
  code uses `ConfigureAwait(false)`; `StatusChanged` is marshaled to the UI thread by the consumer
  (the existing dispatcher pattern in `ConnectionStatusViewModel`).
- The relay host auto-reconnects on transient relay drops; the service reflects `Reconnecting` and
  returns to `Hosting` on recovery.
- Management token expiry triggers a silent refresh via the token callback; on hard auth failure the
  service enters `Error` with `LastError` set and stops hosting.

## Security notes

1. Private by default; anonymous opt-in and warned.
2. Never persist raw tokens in tracked files — store only a **source name** (env var / keychain key);
   resolve at runtime. (Repository rule: no secrets in tracked files.)
3. Use least-privilege, short-lived, tunnel/port-scoped access tokens for Token mode.
4. Dev tunnels are preview infrastructure. Because tunnel auth is currently the authentication
   boundary for remote workspace access (see Authentication model), the access mode must be chosen
   deliberately; workspace-level authorization is a future **additional** layer, not yet present.

## Test tasks

1. `DevTunnelHostService.StartAsync` ensures-or-creates a tunnel and adds a port for the supplied
   local port (fake `IDevTunnelManagementClientFactory`), persisting `TunnelId`/`HostedPorts`.
2. Reusing an existing `TunnelId` does not create a new tunnel.
3. Access control is built correctly per `AccessMode` (Private / Token / Anonymous) — assert the
   `TunnelAccessControl` entries / token minting calls.
4. `Hosting` status publishes a non-null public `AccessPointUrl`; `StatusChanged` fires with the
   correct sequence (`Starting` → `Hosting`).
5. `ConnectionStatusViewModel` shows the public tunnel URL (not the local `ListenUrl`) as the access
   point once the service reports `Hosting`, and reverts/annotates on stop/error.
6. `ReconfigureAsync` applies an access-mode change without changing `TunnelId`.
7. Relay drop → `Reconnecting` → `Hosting` transition (fake relay host).
8. Management auth failure → `Error` with `LastError` populated and hosting stopped.
9. The local port is parsed correctly from `WorkspacesWebHost.ListenUrl` (including non-default ports).
10. No raw token is ever written to persisted configuration (only the source name).
11. Disposal stops the relay host and is idempotent.
12. **Single-port invariant:** when the listen port changes, the host removes the stale `TunnelPort`
    and leaves exactly one port matching the new listen port; a tunnel never ends up with two ports.
13. **Reverse-execution over the same port:** the reverse-execution WebSocket route is reachable
    through the single forwarded port (no separate port is created/forwarded).
14. **Client tunnel-name resolution:** `IDevTunnelEndpointResolver` looks up a tunnel by name, reads
    its single forwarded port, and produces the correct relay base URI (and `X-Tunnel-Authorization`
    token for Token mode) — without any user-supplied port.
15. **Client reconnect refresh:** on a simulated connection failure, `DevTunnelConnectionMonitor`
    re-resolves the tunnel by name (picking up a changed port) and reconnects with bounded backoff,
    without restarting the workspace; a healthy connection performs no extra management lookups.
16. **Explicit-access-point mode unchanged:** a client configured with `WebEndpoint` connects to that
    fixed URL and does not perform name resolution; reconnect retries the same URL.
17. **Client settings validation:** exactly one of `WebEndpoint` / `TunnelName` is required; the
    default remains the access-point option so existing configurations are unaffected.

## Decisions

1. **Tunnel persistence scope: stable per-machine.** The host maintains one stable, reused tunnel per
   machine, keyed by `user-computer-profile`, persisting its `TunnelId`/`TunnelName` so the public URL
   and tunnel name are stable across app restarts. (Ephemeral per-session tunnels are not used.)
2. **Reverse-execution WebSocket is forwarded automatically — no extra port.** The reverse-execution
   WebSocket endpoints (`MapReverseEndpoints`) are routes on the **same** Kestrel application as the
   web data-access and agent endpoints, all bound to the single `WorkspacesWebHost.ListenUrl` port.
   Forwarding that one port therefore carries web data access **and** the reverse-execution WebSocket
   over the same dev tunnel; there is nothing additional to forward. This reinforces the single-port
   invariant: one forwarded port serves all three endpoint families.

## Open questions

1. **Management identity acquisition.** Creating/owning a dev tunnel through the management SDK
   requires an authenticated identity with the dev-tunnels service (a Microsoft account / Entra ID /
   GitHub user, the same identities `devtunnel user login` uses). Two ways to obtain it:
   - **Interactive first-run login** — the app performs an OAuth sign-in the first time hosting is
     enabled, caching the refresh token in the OS secret store (most user-friendly; needs an embedded
     auth flow).
   - **Headless token source only** — the app reads a pre-provisioned dev-tunnels access token from a
     configured source (env var / keychain key named by `AccessTokenSource`) and never prompts (best
     for unattended/service installs; the user must provision the token out-of-band).

   Decision needed: support interactive login, headless-only, or both (headless as an override). This
   does not affect the *tunnel access mode* (Private/Token/Anonymous) for inbound clients — it only
   concerns how *this host* authenticates to the dev-tunnels service to manage its own tunnel.

## Source references

1. Dev Tunnels overview — https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/overview
2. Dev Tunnels security — https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/security
3. Dev Tunnels .NET SDK (`Microsoft.DevTunnels.Management` / `.Connections` / `.Contracts`) —
   https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/
4. Related: [`devtunnels-web-access.md`](devtunnels-web-access.md),
   [`reverse-tunnel-trust-execution.md`](reverse-tunnel-trust-execution.md).
