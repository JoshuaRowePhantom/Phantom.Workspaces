# Design: user-computer-profile-connectivity

> **Status:** Phase 2 — Options (awaiting your decision). Requirements refined with research findings.
> **Prefix:** `[profile-connectivity]`
> **Target repo:** `JoshuaRowePhantom/Phantom.Workspaces`

## Requirements

### Data model

- Add an optional top-level `connectivity` array attribute to the `user-computer-profile`
  entity (JSON schema `user-computer-profile.json`). Optional; profiles without it behave
  exactly as today.
- Each entry is an object discriminated by a required `type`, with per-type fields
  (kebab-case keys). Types: `rdp`, `devbox`, `ssh`. (`powershell` is intentionally **not**
  a connectivity type — see below.)
- **`rdp`** — a directly-dialable host:
  - `hostname` (required, string) — DNS/IP target.
  - `port` (optional, integer, default 3389).
  - `username` (optional, string).
- **`devbox`** — a Microsoft Dev Box / Windows 365 / AVD **brokered** target. A Dev Box has
  **no dialable hostname**; it is reached through the AVD gateway after Entra auth, so it
  needs identity/broker fields rather than a host (see Research §1):
  - `dev-center-uri` (required, string) — the DevCenter dataplane base URI.
  - `project-name` (required, string).
  - `dev-box-name` (required, string).
  - `tenant-id` (required, string) — Entra tenant (GUID or domain).
  - `user-principal-name` (required, string) — UPN for auth and the `ms-avd:` URI.
  - `avd-resource-id` (optional, string) — AVD resource GUID; if absent it is discovered at
    runtime from the DevCenter `remoteConnection` REST response.
- **`ssh`** — schema only in this design (implementation punted):
  - `hostname` (required, string), `port` (optional, int, default 22),
    `username` (optional, string), `private-key-id` (optional, string — secret-store id).
- Multiple entries of the same `type` are allowed (e.g. two `rdp` hosts); the UI
  disambiguates when more than one entry of a type exists (see Open Questions Q3).

> **Note on field names:** the owner's original example used `type` / `hostname` /
> `private-key-id`. Research showed a Dev Box cannot be expressed with a hostname, so
> `devbox` uses broker/identity fields instead. `type`, `hostname`, and `private-key-id`
> are retained where they apply (`rdp`, `ssh`).

### Behaviour — shortcut handlers (open a protocol against a connectivity entry)

- For each applicable connectivity entry on a profile, the profile surfaces shortcut(s) that
  open that protocol. A shortcut only applies when a matching entry exists (and, for embedded
  RDP, only on Windows).
- **RDP — two handlers:**
  - **Embedded-window** — opens a live RDP view inside a workspace tab.
  - **External-process** — launches the OS RDP client (`mstsc.exe`) as a separate process.
- **DevBox — external-process only.** Embedding is **not possible** (brokered + Entra auth;
  see Research §1/§4). The handler launches Microsoft's "Windows App" via the documented
  `ms-avd:connect?resourceid=…&username=…` URI, or the `cloudPcConnectionUrl` obtained from
  the DevCenter `remoteConnection` REST call. An embedded DevBox variant is **out of scope**
  with justification.
- **PowerShell — dropped.** A "connectivity powershell" is subsumed by the existing
  `StartShellOnProfileShortcutHandler` (opens `pwsh`/`bash` against the profile via the
  trusted-executor/transport). No new PowerShell connectivity handler is added.
- **SSH — punted.** Schema is defined; no handler is implemented. A clear extension point is
  left (a future `OpenSshShortcutHandler`).

### Multi-view (same host in multiple workspaces)

- Requirement: the same RDP host can be shown **simultaneously** in two (or more) workspaces.
- Research (see §2/§3) establishes this is only achievable by owning the decode/render loop:
  a **single** shared RDP connection whose decoded framebuffer is fanned out to **N** Avalonia
  `WriteableBitmap` surfaces. Two independent sessions to a client-Windows host conflict
  (the second takes over and disconnects the first). The Microsoft ActiveX control cannot
  fan out (no framebuffer access). Therefore multi-view drives the embedding-library choice.
- Input (keyboard/mouse) is arbitrated to the currently-focused view; the others are
  live mirrors of the same session.

### Platform / constraints

- App targets `net10.0`, Windows-only (`win-x64;win-arm64`). Embedded RDP is Windows-only in
  practice; handlers guard with OS checks and degrade gracefully.
- New handlers follow the `ShortcutHandler` base-class pattern (registered in
  `MainWindowViewModel`); embedded RDP is a `WorkspaceTabViewModel`; external launches follow
  the `UrlOpener` / `Process.Start` pattern.

### Out of scope

- SSH connection handler (schema only).
- Embedded DevBox/AVD (impossible to embed).
- PowerShell connectivity handler (subsumed by existing shell).
- Credential-manager UI for `private-key-id` (reuses existing secret store if needed).
- Changes to agent transport routing (`UserComputerProfileTransportFactory`); connectivity
  is a user-initiated "open a session" feature, distinct from agent transport.

---

## Research findings (summary; full citations to be carried into the filed bugs)

### §1 — DevBox/W365/AVD connection model
Dev Box, Windows 365, and AVD share AVD's **reverse-connect** transport: no inbound/dialable
host; both ends connect out to `*.wvd.microsoft.com`. Connections are **brokered** via a
signed `.rdp` from a feed after **Entra ID** auth. External launch options (only):
`ms-avd:connect?resourceid=<guid>&username=<upn>` (GA, officially documented), or the
`webUrl` / `cloudPcConnectionUrl` / `rdpConnectionUrl` returned by the DevCenter
`…/remoteConnection` REST API. `mstsc.exe` and the RDP ActiveX **cannot** connect (no Entra /
gateway-token support). The old Remote Desktop MSI client is end-of-support 2026-03-27; the
successor is **Windows App**. Minimal fields: `dev-center-uri`, `project-name`, `dev-box-name`,
`tenant-id`, `user-principal-name` (+ optional `avd-resource-id`).

### §2 — Embeddable RDP options (Avalonia / net10.0 Windows)
| Option | Embeddable in Avalonia | License | Maintained | arm64 | Effort |
|---|---|---|---|---|---|
| **Devolutions IronRDP** (`Devolutions.IronRdp`) | ✅ via `WriteableBitmap` (no HWND/COM) — first-party Avalonia example | MIT OR Apache-2.0 | ✅ v2025.12.4 | ✅ prebuilt `win-arm64` | 🟡 Low-Med |
| MS RDP ActiveX (`mstscax.dll`) | ✅ via `NativeControlHost`+HWND, needs WinForms pump / COM plumbing | Windows EULA (use-only) | ✅ (OS) | ✅ in System32 | 🔴 High |
| FreeRDP (`libfreerdp`) | ⚠️ C only, self-built DLLs, HWND/bitmap wiring | Apache-2.0 | ✅ | ⚠️ build-yourself | 🔴 Very High |

IronRDP renders decoded RGBA into an Avalonia `WriteableBitmap` shown in a plain `<Image>`
(official `Devolutions.IronRdp.AvaloniaExample`), with keyboard/mouse/clipboard/resize wired.

### §3 — Multiple views of the same host
Two RDP *sessions* to a client-Windows host conflict (single-session-per-user; second takes
over). The only protocol-safe way to show one host in two views is **one connection,
fan-out rendering**: IronRDP/FreeRDP expose the decoded framebuffer (`EndPaint` /
`primary_buffer`) so the app can blit dirty rects into N `WriteableBitmap`s (FreeRDP's own
SDL3 client already fans out to multiple windows for multimon). The **ActiveX control cannot**
(opaque HWND, no framebuffer API). Verdict: multi-view ⇒ IronRDP.

---

## Options (Phase 2)

### Option A — IronRDP embedded (WriteableBitmap) + external handlers  ⟵ recommended

**Architecture:** Add `Devolutions.IronRdp`. An embedded RDP tab
(`RemoteDesktopTabViewModel : WorkspaceTabViewModel`) renders a shared session's decoded
frames into a `WriteableBitmap`. A `RdpSessionManager` keeps **one** `RdpSession` per host and
fans out frames to every attached view (enabling multi-view). External RDP uses `mstsc.exe`;
DevBox uses the `ms-avd:` / `cloudPcConnectionUrl` launcher. Handlers: `OpenRdpEmbedded…`,
`OpenRdpExternal…`, `OpenDevBoxExternal…`.

**Pros:** Meets every requirement including embedded window **and** simultaneous multi-view;
Avalonia-native rendering (no HWND/COM/airspace/WinForms); permissive license; arm64 prebuilt;
lowest embedding effort; matches existing `WriteableBitmap`/`Image` UI patterns.
**Cons:** New third-party native dependency (Rust DLL) shipped per-RID; IronRDP NuGet TFM is
`net8.0` (net10-compatible but verify at ship); we own input-arbitration/resize logic for
multi-view; codec breadth slightly below `mstscax` (software GFX).

### Option B — Microsoft RDP ActiveX embedded + external handlers

**Architecture:** Embed `mstscax.dll` via a WinForms `AxHost` HWND hosted in Avalonia
`NativeControlHost`. External + DevBox handlers as in A.

**Pros:** Highest protocol fidelity (RemoteFX/H.264, full RD-Gateway/smart-card); no
third-party dependency; in-box on every Windows incl. arm64.
**Cons:** **Cannot do multi-view** (opaque HWND; two sessions conflict) — fails a core
requirement; heavy COM/ActiveX + WinForms message-pump plumbing; airspace/z-order and DPI
issues; no maintained interop NuGet (hand-generate via `aximp`). High effort, worse outcome.

### Option C — External-process only (no embedding)

**Architecture:** No embedded tab. RDP → `mstsc.exe`; DevBox → Windows App URI. Ship only the
external handlers now; defer embedding/multi-view.

**Pros:** Trivial, no new dependency, fully consistent with the existing `Process.Start`
pattern; still delivers DevBox and one-click RDP.
**Cons:** Fails the explicit embedded-window and multi-view requirements. Only sensible as a
phase-1 slice if you want to defer embedding.

**Recommendation:** **Option A.** It is the only option that satisfies both the embedded-window
and same-host-multi-view requirements, and it is also the lowest-effort embedding path.
(Option C's external handlers are a strict subset of A and could be an early commit within A.)

---

## Open questions (please confirm to proceed to Phase 3/4)

1. **Pick an option** — proceed with **Option A (IronRDP)**? (B is more work and can't do
   multi-view; C defers embedding.)
2. **DevBox scope** — is external launch (Windows App via `ms-avd:` / REST `cloudPcConnectionUrl`)
   acceptable, given embedding is impossible? And should we implement the **REST discovery**
   (call DevCenter `…/remoteConnection` to fetch the launch URL, needing an Entra token via the
   existing secret/credential store), or start with a **statically-configured `avd-resource-id`
   + `ms-avd:` URI** only?
3. **Same-type disambiguation** — when a profile has multiple entries of one type, how should the
   shortcut choose: a submenu/pick-list, open all, or "first only" for v1?
4. **Embedded vs external default** — which is the primary "open RDP" action surfaced on the
   entity, and how is the other exposed (separate shortcut, modifier key, context submenu)?
5. **Multi-view depth for v1** — deliver full simultaneous multi-view (shared session fan-out)
   in the first cut, or land single embedded view first and add fan-out as a follow-up commit?
6. **DevBox auth token** — for REST discovery, confirm we should acquire the Entra token through
   the existing secret store / credential flow (ties into #1241/#1267 credential work).
