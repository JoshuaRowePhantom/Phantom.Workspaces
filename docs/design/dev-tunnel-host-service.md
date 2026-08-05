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
  Entra ID / GitHub) used to create/own tunnels by performing an **interactive sign-in** and then
  caching/refreshing the token in the OS secret store (acquire + cache; see Decision 3). It does
  **not** read an externally-provisioned token; a raw token is never stored in tracked files.
- `IDevTunnelRelayHost` → thin wrapper over `TunnelRelayTunnelHost` so hosting can be faked.

The concrete `DevTunnelHostService` composes these; the SDK types live only in the concrete
implementations.

> **Implementation note (status: implemented).** The implementation lives in
> `Phantom.Workspaces/Services/DevTunnel/`:
> `DevTunnelHostState`/`DevTunnelHostStatus`, `IDevTunnelHostService` + `DevTunnelHostService`,
> `IDevTunnelAuthTokenProvider` + `GitHubDevTunnelAuthTokenProvider`,
> `IDevTunnelEndpointResolver` + `DevTunnelEndpointResolver` (+ `DevTunnelEndpointResolution`),
> `DevTunnelConnectionMonitor` (+ `IDelayScheduler`/`RealDelayScheduler`,
> `DevTunnelConnectionStatus`/`DevTunnelReconnectOptions`), `IDevTunnelRelayHost`, and the
> `DevTunnelServiceFactory` composition helper. Key deviations from the sketch above, all to maximize
> testability behind SDK-free seams:
> - Instead of exposing the SDK `TunnelManagementClient` through a factory, the orchestration depends on
>   a **domain-level `IDevTunnelManagementClient`** seam (`EnsureTunnelAsync` /
>   `SetSingleForwardedPortAsync` / `ApplyAccessModeAsync` / `GetAccessPointUrlAsync`, plus
>   `DevTunnelDescriptor`); the client-side resolver depends on an SDK-free **`IDevTunnelLookupClient`**
>   (`LookupByNameAsync` → `DevTunnelLookupResult`). Both are implemented by the single concrete
>   `DevTunnelManagementClientWrapper`, which is the only place `Microsoft.DevTunnels.*` types appear
>   (alongside `TunnelRelayDevTunnelHost`). The orchestration, resolver, and monitor are fully
>   unit-tested with fakes (`DevTunnelHostServiceTests`, `DevTunnelEndpointResolverTests`,
>   `DevTunnelConnectionMonitorTests`) — no network.
> - **Management identity uses the GitHub token** (`GitHubDevTunnelAuthTokenProvider` →
>   `GitHubAuthTokenResolver`, `TunnelAuthenticationSchemes.GitHub`) rather than a bespoke interactive
>   MSAL sign-in. This unifies host and client identity through the same GitHub sign-in the web client
>   already uses for `X-Tunnel-Authorization` (see `devtunnels-web-access.md`), and keeps the
>   "no raw token in tracked files" rule. `IDevTunnelAuthTokenProvider` remains the seam, so an
>   interactive provider can be substituted later without touching the rest.
> - **The logical tunnel name is carried as a tunnel _label_, not the SDK custom `Name`.** Custom tunnel
>   names require a service feature ("allow custom tunnel names") that is disabled for most accounts and
>   returns `403 Forbidden` on create. `DevTunnelManagementClientWrapper` therefore creates the tunnel
>   with `Labels = [name]` and both host (`EnsureTunnelAsync`) and client (`LookupByNameAsync`) locate it
>   by that label (server-side `TunnelRequestOptions.Labels` filter + client-side `HasLabel` match), so no
>   custom-names feature is needed.
> - **Tunnel updates must not carry ports.** Ports are managed individually
>   (`SetSingleForwardedPortAsync` via create-or-update / delete per port); including a `Ports` collection
>   on `UpdateTunnelAsync` is rejected by the service ("Batch update of ports is not supported"). When an
>   existing tunnel is fetched with `IncludePorts`, `ApplyAccessModeAsync` clears `Ports`/`Endpoints`
>   before updating access control. Because that update clears the cached tunnel's ports, the relay host
>   does **not** reuse the cached tunnel: `TunnelRelayDevTunnelHost.StartAsync` first calls
>   `GetConnectReadyTunnelAsync` (a fresh `GetTunnelAsync` with `IncludePorts` + host scopes, guaranteeing a
>   non-null `Tunnel.Ports`, which the SDK relay host requires) and connects with that.
> - **Auto tunnel discovery (client side).** Every Workspaces-owned tunnel also carries a stable marker
>   label (`DevTunnelNaming.WorkspacesMarkerLabel = "phantom-workspaces"`). A client configured with the
>   tunnel name `"auto"` (or blank — `DevTunnelNaming.IsAuto`) skips name matching and instead discovers
>   the single marker-labeled tunnel via `IDevTunnelLookupClient.DiscoverSingleAsync` (throws when none or
>   more than one is found, guiding the user to set a specific name). `DevTunnelEndpointResolver` routes
>   auto → `DiscoverSingleAsync`, named → `LookupByNameAsync`; covered by `DevTunnelEndpointResolverTests`.
>   The host's `EnsureTunnelAsync` likewise accepts `"auto"`: it reuses the single existing marker tunnel
>   or creates a marker-only one. The settings UI documents the `"auto"` selector.
> - `MainWindowViewModel.InitializeWebHostAsync` starts the host service (via `DevTunnelServiceFactory`)
>   only when a tunnel is configured (`DevTunnel.TunnelName`/`TunnelId` set), sets
>   `ConnectionStatusViewModel`'s **local** access point to `webHost.ListenUrl`, the **tunnel name**, and
>   forwards every `DevTunnelHostStatus` change to `SetDevTunnelStatus(state, accessPointUrl, lastError)` —
>   which publishes the real public tunnel URL on `Hosting` and flags `HasProblem` on `Error`/`Reconnecting`.
>   Hosting runs in the background so a sign-in/relay failure never blocks GUI startup; failures surface
>   through `DevTunnelHostStatus.Error`. The network display (`ConnectionStatusWindow`) shows the local and
>   dev tunnel access points, the tunnel name, the host status text, and a translucent red exclamation
>   glyph (also overlaid on the main-window 🌐 button) when `HasProblem`.
> - The `Microsoft.DevTunnels.{Contracts,Management,Connections}` `PackageReference`s (1.3.50) are added
>   to `Phantom.Workspaces`. The `DevTunnelManagementClientWrapper` is unit-tested with a **Moq**
>   `ITunnelManagementClient` (`DevTunnelManagementClientWrapperTests`, enabled by `InternalsVisibleTo`)
>   — covering label-not-custom-name creation, marker-label find/lookup/auto-discovery, the single-port
>   management, and the "no ports/endpoints on a tunnel update" service constraint. The relay host
>   (`TunnelRelayDevTunnelHost`) remains thin glue verified by compilation; full live round-trip behavior
>   is covered only by the opt-in smoke test (see Testing strategy), not the fast suite.
> - **Client connect-by-tunnel-name is wired into repository creation, with reconnect.** `WorkspacesConfiguration.ToRepositorySource()`
>   projects `DataAccessMode.DevTunnelWeb` to the existing `WebRepositorySource` when `DataAccess.WebEndpoint`
>   is set, otherwise to a new **`DevTunnelNameRepositorySource(TunnelName, AccessMode, AccessTokenSource)`**
>   when `DevTunnel.TunnelName` is set. `EntityRepository.CreateUnderlyingDataAccessLayerAsync` builds a
>   **`ReconnectingWebDataAccessLayer`** for that source: it resolves the live relay URI via
>   `DevTunnelServiceFactory.CreateEndpointResolver(...)` and, on a connection drop, re-resolves the tunnel
>   (picking up a changed forwarded port) and reconnects with bounded exponential backoff via the
>   `DevTunnelConnectionMonitor` — retrying the in-flight data operation against the fresh inner layer,
>   without restarting the workspace. Connectivity failures are classified at the web client via the typed
>   **`WebDataAccessRequestException`** (`IsConnectivityFailure`: no response, or status >= 500); 4xx
>   application errors are not retried. Auth reuses the proven scheme: **Token** mode sends the resolved
>   pre-shared token; **Private** mode reuses the GitHub identity token as `X-Tunnel-Authorization` (same as
>   the explicit-endpoint dev tunnel path). Covered by `RepositorySourceTests` and
>   `ReconnectingWebDataAccessLayerTests` (deterministic, injected clock). The monitor surfaces
>   Connected/Reconnecting/Failed status via `ReconnectingWebDataAccessLayer.Status`/`StatusChanged`;
>   binding that to the workspace connection-status UI is the remaining follow-up.
>
> **Manual test setup.** `playspace-config.json` hosts the tunnel (`remoteHosting.enabled: true`,
> `devTunnel.tunnelName: "phantom-workspaces-playspace"`), and `playspace-config-2.json` connects over it
> (`dataAccess.mode: "devTunnelWeb"`, same `devTunnel.tunnelName`, `userComputerProfileOverride:
> "playspace-second-instance"`). Both are exposed as Visual Studio launch profiles in
> `Phantom.Workspaces/Properties/launchSettings.json`, so the host and the override-profile client can be
> run side by side on one machine.

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
2. **Tunnel name (new).** The user supplies just a **tunnel name**. The client resolves the live
   endpoint at connect time via a new `IDevTunnelEndpointResolver`, using the **unified dev tunnel
   sign-in** (`IDevTunnelAuthTokenProvider`) for identity:
   - look up the tunnel by name with a `TunnelManagementClient` authenticated by the signed-in
     identity;
   - read its **single** forwarded `TunnelPort` (relying on the host's single-port invariant) — no
     port number is configured by the user;
   - construct the relay endpoint URI for that port. For **Private** access the per-connection
     `X-Tunnel-Authorization` is derived from the signed-in identity (the user never types a token);
     only in **Token** mode is a pre-shared token read from `AccessTokenSource`;
   - hand the resolved base URI (and header, if any) to `WebClientDataAccessLayer`.

   Because the port is discovered, the host can change its listening port (the host re-points its
   single forwarded port) and the client still reconnects to the right place by name. Because identity
   comes from the same sign-in as hosting, the client and host share one account experience.

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
- `IDevTunnelEndpointResolver.ResolveAsync(tunnelName, accessMode, ct)` →
  `(Uri baseUri, string? tunnelAuthToken)`. Backed by `TunnelManagementClient` and the unified
  `IDevTunnelAuthTokenProvider` (same sign-in as the host); `tunnelAuthToken` is null for Private
  access (identity-based) and the pre-shared token only in Token mode. Fakeable in tests.
- `DevTunnelConnectionMonitor` wraps the web DAL connection with the resolver + backoff policy and
  raises status events.

## Authentication model

The same dev-tunnels identity is used on **both ends** — the host (to create/own/host its tunnel) and
a connecting client (to reach a Private tunnel). They share **one** unified sign-in.

1. **Unified dev tunnel sign-in (host *and* client identity).** `IDevTunnelAuthTokenProvider` performs
   the interactive sign-in (Microsoft account / Entra ID / GitHub) once and caches/refreshes the token
   in the OS secret store (Decision 3). The **same** provider and the **same** "Dev tunnel account" GUI
   serve two roles:
   - **Host:** the token is given to `TunnelManagementClient` (access-token callback) to create/host
     the tunnel.
   - **Client (Private access):** the token authorizes the client against the relay. The client's
     `IDevTunnelEndpointResolver` / web DAL obtain the per-connection `X-Tunnel-Authorization` from the
     signed-in identity via the SDK — the client does **not** type or store a token. This is why
     connecting to a Private tunnel by name "just works" after the user signs in, exactly like hosting.
2. **Tunnel access mode (what the host requires of inbound clients)** — governed by
   `DevTunnelAccessMode`:
   - **Private** (default): clients authenticate with their **signed-in identity** (role 1 above) — no
     separate credential to manage. This is the unified, recommended path.
   - **Token**: an explicit **pre-shared** access token for granting access to someone who will *not*
     sign in (e.g. an automation, or sharing without an account). The host mints a tunnel/port-scoped,
     short-lived token; the recipient configures it via `AccessTokenSource` and the web DAL sends it as
     `X-Tunnel-Authorization: tunnel <token>` (already supported). This is a deliberately *different*
     mechanism from sign-in — it is the equivalent, on the client side, of the externally-provisioned
     token path we dropped for the host: only used when identity sign-in is not possible.
   - **Anonymous**: opt-in only, visibly warned (`IsAnonymousAccessWarningVisible`). Not the default.

So `AccessTokenSource` does **not** duplicate the sign-in flow — it is the *opt-out* of identity, for
Token mode only. For the default (Private) experience there is a single unified sign-in shared by host
and client, and no token-source field is shown.

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

0. **Shared "Dev tunnel account" sign-in (unified).** A single account section (the Decision-3
   sign-in: `[ Sign in ]` / `Signed in as <account>` / `[ Sign out ]`) is presented **once** and used
   by both roles — hosting and connecting-by-name to a Private tunnel. The same control/state backs
   `IDevTunnelAuthTokenProvider`. There is **no** separate token entry for Private access on either
   side; the token-source field appears only when **Token** access mode is explicitly chosen.
1. **Host side (`RemoteAccessSettingsView` / `RemoteAccessSettingsViewModel`).** Keep the current
   access-point/listen settings. Show the shared sign-in section. Surface the resolved **tunnel name**
   and the live public access point (read-only, copyable) once hosting, plus the host status. The host
   already enforces a single port, so no port field is shown.
2. **Client side (`DevTunnelWebSettingsView` / `DevTunnelWebSettingsViewModel`,
   `RepositoryConnectionModeViewModels`).** Show the same shared sign-in section, then a **"Connect
   by"** choice with two options:
   - **Access point** (existing) — the explicit `WebEndpoint` text box, unchanged.
   - **Tunnel name** (new) — a single tunnel-name text box; no port input (auto-discovered). For
     **Private** access no token is entered (identity comes from the shared sign-in); the
     `AccessTokenSource` field is shown **only** for **Token** access mode and applies to both connect
     options.
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

## Testing strategy

Dev tunnel work is tested **without ever contacting the real Microsoft Dev Tunnels service** (no
network, no credentials, no `*.devtunnels.ms`), consistent with the repo's deterministic,
no-timing-based test conventions. The enabling principle is that the SDK is reached only through the
seams defined above — `IDevTunnelManagementClientFactory`, `IDevTunnelAuthTokenProvider`,
`IDevTunnelRelayHost`, `IDevTunnelEndpointResolver`, `DevTunnelConnectionMonitor` — so every test
injects fakes and the SDK types never appear in the logic under test.

### Layers and how each is tested

1. **`DevTunnelHostService` (orchestration) — unit tests with fakes.**
   - *Fake management client* records calls (get/create tunnel, create/update/delete port, set access
     control, mint token) and returns canned `Tunnel`/`TunnelPort` contracts. Assert ensure-or-create,
     the single-port invariant (stale port removed), access control per `AccessMode`, and persistence
     of `TunnelId`/`HostedPorts`.
   - *Fake relay host* exposes controllable `Started`/`Dropped`/`Reconnected`/`Failed` triggers (no
     sockets). Assert status transitions `Starting → Hosting`, `Hosting → Reconnecting → Hosting`, and
     `→ Error` on hard failure.
   - *Fake auth provider* returns a token or simulates a sign-in/refresh failure. Assert `Error` +
     `LastError` and that hosting stops.
2. **`IDevTunnelEndpointResolver` (client name resolution) — unit tests.** Fake management client
   returns a tunnel with exactly one forwarded port; assert the relay base URI is built correctly, that
   no user-supplied port is needed, and that `tunnelAuthToken` is null for Private and the pre-shared
   token only for Token mode.
3. **`DevTunnelConnectionMonitor` (reconnect) — deterministic unit tests.** Drive failures by raising
   a fake connection's failure event and advance retries through an **injected clock/scheduler** (no
   real timers, no `Task.Delay`). Assert: re-resolution happens on failure (picking up a changed port),
   reconnect follows bounded backoff at the scheduled ticks, a healthy connection performs **no** extra
   management calls, and explicit-`WebEndpoint` mode retries the same URL without re-resolving.
4. **Client HTTP path (web DAL over a tunnel-style endpoint) — local-server integration tests.** To
   exercise the actual request path and header injection produced by the resolver, point
   `WebClientDataAccessLayer` at a **local in-process test web server** (Kestrel/`TestServer`) using a
   tunnel-style base URI, and assert behavior and that `X-Tunnel-Authorization: tunnel <token>` is sent
   in Token mode and absent in Private mode. This reuses the pattern already called for in
   `devtunnels-web-access.md` (web DAL over tunnel-style base URI + header injection) — **no real
   tunnel** is involved.
5. **View models — unit tests.** Feed a fake `DevTunnelHostStatus` / monitor state and assert UI state:
   shared sign-in shown/hidden by signed-in state, the `AccessTokenSource` field visible **only** for
   Token mode, the access point exposed as copyable text, and "Connect by" validation requiring exactly
   one of `WebEndpoint` / `TunnelName`. (Avalonia headless test pattern, as elsewhere in
   `Phantom.Workspaces.Tests`.)
6. **User-computer-profile override — unit tests.** Use a fake `ICurrentExecutionContextProvider` whose
   `EffectiveComputerName` is overridden and assert that `ComputerUserProfileDiscoveryTool` /
   `WorkspaceEntitySessionBootstrapper` compose a **diverged** `computer-user-profile` entity name while
   the shared `users/username` and `computers/hostname` entities are unchanged. (Extends the existing
   `DiscoveryToolsTests` fake-provider pattern.)

### Determinism

- No real network or tunnel service in any automated test; the fakes are the boundary.
- No timing-based waits: reconnect/backoff and relay drop/recover are driven by injected
  clock/scheduler and explicit event triggers, not `Task.Delay` (matches the standing
  "all tests deterministic / event-driven synchronization" convention).
- `IDevTunnelRelayHost` is thin SDK glue and is **not** unit-tested directly; its behavior is covered
  indirectly via the orchestration fakes and the opt-in smoke test. The `TunnelManagementClient` wrapper
  **is** unit-tested with a Moq `ITunnelManagementClient` (`DevTunnelManagementClientWrapperTests`),
  since its label/port/access-control mapping encodes service constraints worth guarding.

### Optional live smoke test (opt-in, not in the fast suite)

A single, **opt-in** integration test may actually sign in, create a tunnel, forward a port, and
verify a round-trip — for local/manual verification only. It must be **skipped by default** (gated by
an environment variable / `Skip`, like the slow Git tests run only under `-Mode full`), never run in
the deterministic fast suite or CI, and never require committed credentials. It is a safety net, not
the primary strategy — the fakes above are.

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
18. **Unified sign-in shared by host and client:** the host and the client tunnel-name resolver use the
    **same** `IDevTunnelAuthTokenProvider` instance/flow; a single fake sign-in satisfies both.
19. **Private connect needs no token:** in Private mode `IDevTunnelEndpointResolver` returns a null
    `tunnelAuthToken` (identity-derived) and the client connects without any `AccessTokenSource`; the
    token-source UI is hidden for Private and shown only for Token mode. The resolver must **not** treat
    a null Connect token as fatal in Private mode — the Management API's tunnel-name (list) path never
    mints a per-tunnel Connect token, so the owner connects using their GitHub identity. When the
    resolver yields no token, the tunnel-name web data-access layer authorizes with the GitHub identity
    token plus a 401-refresh resolver (mirroring the explicit-access-point `UseGitHubAuthToken` path)
    rather than sending no `X-Tunnel-Authorization` header.
20. **Token mode uses `AccessTokenSource`:** in Token mode the resolver/web DAL sends the pre-shared
    `X-Tunnel-Authorization` token resolved from `AccessTokenSource` (never a raw token persisted).

## Testing support: user-computer-profile override

To test multiple Workspaces instances on a **single** physical machine (e.g. one acting as host and
another as client over the dev tunnel), each instance must resolve to a **distinct**
`user-computer-profile`. Today the profile identity is derived purely from the real OS user + host
name (`ICurrentExecutionContextProvider.UserName` / `ComputerName`), so every instance on a machine
collapses to the same profile — and therefore the same per-machine tunnel, MCP-server namespace, and
session area. This override makes the effective profile identity configurable.

### Config item

Add a testing-only override to `WorkspacesConfiguration` (and its persisted profile), e.g.:

```csharp
/// <summary>
/// Testing only: overrides the computer identity used when composing this instance's
/// user-computer-profile entity name, so multiple instances can run on one machine with distinct
/// profiles. Null/empty = use the real host name. Not for production use.
/// </summary>
public string? UserComputerProfileOverride { get; init; }
```

When set, the effective computer component of the profile name becomes the override value instead of
the real host name; the user component is unchanged. (Overriding the *computer* component, not the
user, keeps it aligned with how a tunnel/host is "a machine".)

### Plumbing (the resolver path, generally)

The override is applied once, at the single point that defines the instance's identity, and flows to
everything that builds a `computer-user-profiles/.../computers/hostname/<computer>` name:

1. **`ICurrentExecutionContextProvider` / `CurrentExecutionContextProvider`** — gains an
   `EffectiveComputerName` (real `ComputerName` unless overridden). The override value is injected from
   `WorkspacesConfiguration.UserComputerProfileOverride` when the provider is constructed. `ComputerName`
   (the *real* host, used for the `computers/hostname` **computer** entity) stays as-is; only the
   profile-composition uses `EffectiveComputerName`. This keeps the real computer entity shared while
   the profile diverges per instance.
2. **`ComputerUserProfileDiscoveryTool`** — composes the `computer-user-profile` entity name from the
   effective computer name, so discovery creates/updates the per-instance profile entity.
3. **`WorkspaceEntitySessionBootstrapper.InitializeAsync`** — constructs the same
   `userComputerProfileEntityName` from the effective computer name and resolves the per-instance
   `UserComputerProfileEntityId`. (It currently `new`s a `CurrentExecutionContextProvider()` directly;
   that construction takes the override.)
4. **General profile consumers** — anything that derives the machine prefix from the profile name uses
   the same effective name, including:
   - the dev tunnel host (Decision 1) → distinct tunnel per instance;
   - `McpServerEntityToolResourceFactory` / `AgentSessionShortcutContext` machine MCP-server prefix
     (`computer-user-profiles/.../copilot/mcp-servers`);
   - the copilot session discovery area (`.../copilot/sessions`).

   Centralizing the override in the execution-context provider (rather than at each call site) ensures
   these stay consistent and a single switch reroutes the whole instance to its own profile namespace.

### GUI

Under settings (Remote Access or a "Diagnostics/Advanced" group), add a clearly-labeled **testing**
field: `User-computer-profile override (testing)` — a text box bound to
`UserComputerProfileOverride`, with helper text "Leave blank for normal use. Set a unique value to run
a second instance on this machine for testing." Empty = no override.

### Caveats

- **Testing only**, and visibly marked so. It changes which entities (profile, sessions, MCP servers,
  hosted tunnel) this instance reads/writes — a wrong value silently points the instance at a
  different namespace.
- Does **not** change the real `users/username` or `computers/hostname` entities (shared); only the
  composed `computer-user-profile` name diverges.
- Changing it at runtime should re-bootstrap the session identity (ties into the pending
  `live-service-reconfiguration` work) or, more simply, require a restart.

## Decisions

1. **Tunnel persistence scope: stable per-machine.** The host maintains one stable, reused tunnel per
   machine, keyed by `user-computer-profile`, persisting its `TunnelId`/`TunnelName` so the public URL
   and tunnel name are stable across app restarts. (Ephemeral per-session tunnels are not used.) The
   effective `user-computer-profile` is overridable for testing so multiple instances can run on one
   machine, each with its own profile and therefore its own tunnel — see
   "Testing support: user-computer-profile override".
2. **Reverse-execution WebSocket is forwarded automatically — no extra port.** The reverse-execution
   WebSocket endpoints (`MapReverseEndpoints`) are routes on the **same** Kestrel application as the
   web data-access and agent endpoints, all bound to the single `WorkspacesWebHost.ListenUrl` port.
   Forwarding that one port therefore carries web data access **and** the reverse-execution WebSocket
   over the same dev tunnel; there is nothing additional to forward. This reinforces the single-port
   invariant: one forwarded port serves all three endpoint families.
3. **Management identity: interactive sign-in only.** The host obtains its dev-tunnels access token by
   having the user **sign in interactively** (Microsoft account / Entra ID / GitHub — the identities
   `devtunnel user login` uses); the app then caches the token in the OS secret store and refreshes it
   silently. `IDevTunnelAuthTokenProvider` encapsulates this acquire-and-cache flow.
   - We **explicitly do not** support an externally-provisioned ("headless") token source for the
     management identity — i.e. reading a token the user set in an env var / keychain for unattended,
     no-UI installs. That scenario is out of scope; hosting requires a signed-in user.
   - This is separate from the **tunnel access mode** for inbound clients. In particular,
     `DevTunnelConfiguration.AccessTokenSource` continues to name the source of the *Token-mode client
     access token* (`X-Tunnel-Authorization`), which is unrelated to how the host signs in.
   - **GUI:** Remote Access settings show a single **"Dev tunnel account"** row — signed out shows a
     `[ Sign in to host a dev tunnel ]` button (hosting controls disabled until signed in); signed in
     shows `Signed in as <account>` (read-only) with a `[ Sign out ]` link, plus inline errors
     ("Sign-in expired — sign in again"). No token-source field for the host identity.

## Open questions

1. **Interactive sign-in flow mechanism.** Whether the sign-in (Decision 3) uses a
   **system-browser + loopback redirect** flow or a **device-code** flow. Device-code is simpler (no
   loopback listener) at the cost of a copy-paste step; system-browser+loopback is more seamless. Both
   sit behind `IDevTunnelAuthTokenProvider`, so this can be decided/changed without affecting the rest
   of the design.

## Source references

1. Dev Tunnels overview — https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/overview
2. Dev Tunnels security — https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/security
3. Dev Tunnels .NET SDK (`Microsoft.DevTunnels.Management` / `.Connections` / `.Contracts`) —
   https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/
4. Related: [`devtunnels-web-access.md`](devtunnels-web-access.md),
   [`reverse-tunnel-trust-execution.md`](reverse-tunnel-trust-execution.md).
