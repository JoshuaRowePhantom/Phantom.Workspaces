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
2. **Configure port** — add/ensure a `TunnelPort` for the GUI's listening port (parsed from
   `WorkspacesWebHost.ListenUrl`) with the configured `Protocol` (default `https`).
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

Tunnel auth is a **transport gate only** — application-level authorization remains independent
(unchanged from the existing design).

## Status surfaced to UI

`DevTunnelHostStatus` drives:

- `ConnectionStatusViewModel.AccessPoint` (the copyable/selectable access-point text box) — set to the
  public tunnel URL while `Hosting`.
- The top-right global status dropdown row (per `devtunnels-web-access.md`): state indicator, public
  endpoint, access mode, last error, and quick actions (restart / copy endpoint).

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
4. Dev tunnels are preview infrastructure — not a substitute for workspace-level authorization.

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

## Open questions

1. Tunnel persistence scope: one stable per-machine tunnel (reused across runs) vs. ephemeral per
   session. Default recommendation: stable per-machine, keyed by `user-computer-profile`.
2. Management identity acquisition UX: interactive first-run login vs. headless token source only.
3. Whether to also forward the reverse-execution WebSocket port automatically or only the primary
   web port (initial scope: the primary `ListenUrl` port).

## Source references

1. Dev Tunnels overview — https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/overview
2. Dev Tunnels security — https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/security
3. Dev Tunnels .NET SDK (`Microsoft.DevTunnels.Management` / `.Connections` / `.Contracts`) —
   https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/
4. Related: [`devtunnels-web-access.md`](devtunnels-web-access.md),
   [`reverse-tunnel-trust-execution.md`](reverse-tunnel-trust-execution.md).
