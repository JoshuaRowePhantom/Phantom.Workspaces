# Build and installation

## Purpose

Define how Phantom.Workspaces is built, packaged, versioned, distributed, and installed —
**Windows first** — covering self-managed install, the in-app auto-updater, the tray icon,
and distribution via GitHub Releases. User-facing first-run configuration (repository mode,
MongoDB container, remote/dev-tunnel) is covered separately in `docs/design/installation.md`;
this document covers everything up to and including getting the app binaries onto the machine.

Scope of this iteration: Windows x64 (and arm64), distributed as a downloadable, self-updating
package from GitHub Releases. **winget packaging is deferred** — its design lives in
`docs/design/winget.md` and is not implemented yet. macOS and Linux packaging are placeholders
under "Future (non-Windows)".

## What we are shipping

- `Phantom.Workspaces` is the GUI entry point: `OutputType=WinExe`, `net10.0`, Avalonia 12,
  with a Windows `app.manifest` (`assemblyIdentity name="Phantom.Workspaces.Desktop"`).
- Secondary executables exist (`Phantom.Workspaces.Agent.Cli`, `Phantom.Workspaces.Web.Server`).
  The installable product is the GUI app; the CLI ships **alongside** it in the same package
  so `pw`/agent tooling is on the user's machine after install.

## Build

### Local/dev build

- Restore + build via the solution: `dotnet build Phantom.Workspaces.slnx -c Release`.
- Tests run through the approved harness `.\scripts\run-tests.ps1` (results in
  `scripts\test-results.log`).

### Release publish (Windows)

Produce a self-contained, framework-independent app so users need no preinstalled .NET:

```
dotnet publish Phantom.Workspaces\Phantom.Workspaces.csproj `
  -c Release `
  -r win-x64 `            # and a second pass for win-arm64
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true
```

Notes / proposed project settings (added to `Phantom.Workspaces.csproj` or a
`Directory.Build.props` publish section):

- `RuntimeIdentifiers` = `win-x64;win-arm64`.
- Keep `PublishTrimmed=false` initially (Avalonia + reflection-heavy schema/DI paths and the
  `x-field-editor` `Type.GetType` activation make trimming risky; revisit with a trim model
  later).
- `PublishReadyToRun=true` for faster cold start.
- LibGit2Sharp and Avalonia native assets must be included in the single-file bundle
  (`IncludeNativeLibrariesForSelfExtract`).
- Bundle the CLI publish output into the same staging folder used by the installer.

### Versioning

Single source of truth for version, consumed by the build, the `assemblyIdentity`, and any
package manifest:

- Define `Version` / `InformationalVersion` centrally in `Directory.Build.props` (e.g.
  `<Version>0.1.0</Version>`), or derive from Git tags via a tool (Nerdbank.GitVersioning or
  `MinVer`) so a tag like `v0.1.0` drives the assembly + package version deterministically.
- The Windows `app.manifest` version and the release tag are kept in sync with this value
  (the CI release job reads it). The deferred winget `PackageVersion` (`docs/design/winget.md`)
  would consume the same source.
- Semantic versioning; each release uses a distinct, strictly orderable version.

## Build environment and the Avalonia license key

Avalonia 12 requires a **license key at compile time**, supplied as the
`AvaloniaUILicenseKey` MSBuild item. In this repo that is wired in `Avalonia.Licensing.props`:
the key is read from the `AVALONIA_UI_LICENSE_KEY` environment variable, or from a local,
git-ignored `Avalonia.Licensing.Local.props`. The key is a **secret** and must never be
committed.

**Does GitHub provide a Windows build environment? Yes.** GitHub Actions offers hosted
**`windows-latest`** runners that can build, publish, sign, and package the app:

- Store the Avalonia key as a repository/organization **Actions secret**
  (`AVALONIA_UI_LICENSE_KEY`) and inject it into the build step's `env:` so
  `Avalonia.Licensing.props` picks it up — exactly mirroring the local env-var path, so no
  build-script divergence between dev machines and CI.
- The same Windows runner runs `dotnet publish` (win-x64 / win-arm64), code-signing, zip/
  installer packaging, and GitHub Release creation. (A deferred winget submission step is
  documented in `docs/design/winget.md`.)
- Other secrets (signing cert + password) are likewise Actions secrets, never in source. If
  the signing certificate cannot be exposed to hosted runners for policy reasons, a
  **self-hosted Windows runner** is the fallback for the signing/packaging job.

This makes the hosted Windows runner the single place that holds the license + signing
secrets, so contributors never need them to build unsigned local dev binaries.

## Install layout (versioned directories + `current` link)

Both the installer and the in-app auto-updater use the **same** on-disk layout so updating
is just "drop a new version folder and repoint the link":

```
%LOCALAPPDATA%\Phantom.Workspaces\
  app\
    current\           ->  symlink/junction to versions\0.2.0
    versions\
      0.1.0\           Phantom.Workspaces.exe, CLI, all assets
      0.2.0\           Phantom.Workspaces.exe, CLI, all assets
    updates\           scratch space for in-progress downloads
```

- `current` is a directory **symlink** (or NTFS **junction** if symlink privilege is
  unavailable — junctions need no elevation/Developer Mode, so prefer junction for
  per-user installs).
- All shortcuts, the App Execution Alias / PATH entry, and the startup task point at
  `app\current\Phantom.Workspaces.exe` — a stable path that never changes across versions.
- Installing a version = extract to `versions\<v>\`, then atomically repoint `current`.
- This is a **per-user** install under `%LOCALAPPDATA%` so neither install nor update needs
  elevation. Runtime config stays at `%APPDATA%\Phantom.Workspaces\config.json` (unchanged).

## Windows packaging options

We package the published output as one of the following. winget `installerType` mapping for
each is deferred to `docs/design/winget.md`.

### Option A — Standalone EXE / portable zip (lead)

- Publish the self-contained single-file `Phantom.Workspaces.exe` (+ CLI) and ship as a
  versioned **zip** hosted as a GitHub Release asset.
- First run bootstraps the managed install layout (`app\versions\<v>\` + `current` link),
  registers shortcuts/PATH alias, and is then self-updating via the in-app updater.
- **Pros:** no code-signing strictly required to start, simplest build, no MSIX identity
  plumbing, easy to host as a GitHub Release asset, fits the `current`-symlink updater.
- **Cons:** no Start-menu shortcut until the bootstrap step creates one, SmartScreen prompts
  unless signed.

### Option B — MSIX package

- Build an `.msix` (via the Windows App SDK / `MakeAppx` or a packaging project).
- **Pros:** clean install/uninstall, Start-menu entry, OS-managed identity and update story,
  can later go to the Store.
- **Cons:** requires a **code-signing certificate** (MSIX must be signed to install), more
  build complexity, identity/capability declarations, and packaged-app filesystem
  virtualization conflicts with our self-managed `current`-repointing updater and our
  `%APPDATA%\Phantom.Workspaces\config.json` / Docker / PowerShell installer flows.

### Option C — Classic installer (WiX/MSI or Inno Setup)

- Wrap the publish output in an MSI (WiX) or Inno Setup `.exe` that installs into the managed
  `app\` layout and creates shortcuts + the startup task.
- **Pros:** Start-menu/desktop shortcuts, per-user or per-machine, mature silent-install
  switches; works well unsigned (with SmartScreen caveat).
- **Cons:** extra tooling; must expose **silent** switches for unattended install.

### Recommendation

Lead with **Option A (portable zip)** to ship quickly with the least signing/identity
friction; it maps cleanly onto the `current`-symlink in-app updater. Plan **Option C (Inno
Setup or WiX)** as the "installed" experience that adds shortcuts and silent install. Defer
**MSIX (Option B)** — its file virtualization conflicts with the self-managed updater, so it
only becomes attractive alongside a signing certificate and a Store/OS-managed update story.

Code signing applies to every option: an Authenticode/EV cert removes SmartScreen warnings
and is mandatory for MSIX. Treated as a prerequisite for the "installed" milestones, not the
first portable drop.

## GitHub artifacts to create

This is the concrete set of repository artifacts (workflows, configuration, secrets,
environments, and release outputs) the build/installation design requires. Items marked
**(deferred)** are designed in `docs/design/winget.md` and not built yet.

### GitHub Actions workflows (`.github/workflows/`)

- **`ci.yml`** — pull-request + push-to-`main` continuous integration.
  - Triggers: `pull_request`, `push` to `main`.
  - Runner: `windows-latest` (matches the GUI/Windows target; injects
    `AVALONIA_UI_LICENSE_KEY`).
  - Steps: checkout → setup .NET 10 → restore → build `-c Release` → `.\scripts\run-tests.ps1`
    → upload `scripts\test-results.log` as a workflow artifact on failure.
  - Concurrency group per ref to cancel superseded runs.
- **`release.yml`** — tag-triggered release pipeline (detailed below).
  - Trigger: `push` of tag `v*` (plus `workflow_dispatch` for manual re-runs).
  - Uses the `release` GitHub Environment (holds signing + release secrets, optional manual
    approval gate).
- **`publish-validation.yml`** (optional) — periodic/`workflow_dispatch` job that runs a full
  `dotnet publish` for `win-x64`/`win-arm64` and the publish smoke test without releasing, to
  catch packaging regressions between releases.
- **`codeql.yml`** (optional) — CodeQL security scanning on a schedule + PRs.
- **`dependency-review.yml`** (optional) — dependency-review action on PRs.
- **`winget-submit.yml`** **(deferred)** — see `docs/design/winget.md`; opens the PR to
  `microsoft/winget-pkgs` after a release.

### Composite action / reusable steps (`.github/actions/`)

- **`setup-build`** (composite action) — checkout, `actions/setup-dotnet@v4` (.NET 10), NuGet
  cache, and license-key env wiring; shared by `ci.yml` and `release.yml` to avoid drift.

### Repository configuration files (`.github/`)

- **`dependabot.yml`** — NuGet (and `github-actions`) update schedules.
- **`release.yml`** (under `.github/` for **release-notes** categorization) — configures
  GitHub's auto-generated release notes (categories/labels), so the changelog comes from
  merged PRs rather than a tracked file.
- **`CODEOWNERS`** — ownership for `docs/design/`, workflows, and packaging scripts.
- **Issue/PR templates** (`ISSUE_TEMPLATE/`, `pull_request_template.md`) — optional.
- **`copilot-setup-steps.yml`** **(if/when used)** — cloud-agent environment setup.

### Secrets and variables

- **Actions secrets (repo or org):** `AVALONIA_UI_LICENSE_KEY` (compile-time Avalonia
  license), `CODE_SIGN_CERT` (base64 PFX) + `CODE_SIGN_PASSWORD` (signing), and **(deferred)**
  `WINGET_TOKEN`. All stored as GitHub secrets — never in source.
- **Environment `release`:** scopes signing/release secrets to the release workflow and can
  require a manual approval before publishing.
- **Variables:** non-secret config such as the package identifier and asset-name prefix.

### Branch protection / rulesets

- Protect `main`: require `ci.yml` to pass, require review, linear history; tags `v*` push by
  maintainers only (drives releases).

### Packaging assets in-repo (`build/` or `packaging/`)

- **`packaging/zip/`** — scripts that assemble the portable zip from `dotnet publish` output
  and emit the per-asset `.sha256`.
- **`packaging/inno/Phantom.Workspaces.iss`** or **`packaging/wix/`** — Inno Setup script / WiX
  project for the "installed" experience (Option C), added at that milestone.
- **`install.ps1`** (repo root or `packaging/`) — `irm … | iex` bootstrap that downloads
  `releases/latest`, verifies the checksum, and performs the managed-layout install.
- These are tracked, secret-free build inputs; no certificates or tokens are committed.

### Release outputs (per `vX.Y.Z` GitHub Release)

- `Phantom.Workspaces-<version>-win-x64.zip` + `.sha256`
- `Phantom.Workspaces-<version>-win-arm64.zip` + `.sha256`
- **(later)** `Phantom.Workspaces-<version>-win-x64-setup.exe` + `.sha256` (Option C installer)
- Auto-generated release notes (from `.github/release.yml` categories).
- These stable-named, hashed assets are what the in-app updater, the tray notifier, the
  README "latest" link, and a future winget manifest all consume.

## Release automation (CI/CD)

The **`release.yml`** workflow, triggered on a `v*` tag (or `workflow_dispatch`), running on
`windows-latest` under the `release` environment:

1. **Setup** — `setup-build` composite action (checkout, .NET 10, cache, license-key env).
2. **Build/test** — restore, build `-c Release`, run `.\scripts\run-tests.ps1`; fail on
   non-zero / failing log (upload the log artifact on failure).
3. **Derive version** — read the single version source (tag → `Version`/`InformationalVersion`)
   so every artifact and asset name is consistent.
4. **Publish** — `dotnet publish` for `win-x64` and `win-arm64` (self-contained single-file,
   ReadyToRun), staging GUI + CLI into per-arch folders.
5. **Package** — assemble the portable zip per arch (lead); later build Inno/WiX (or MSIX);
   **sign** the binaries/installer with the signing cert; compute each asset's `.sha256`.
6. **Release** — create the GitHub Release for the tag, attach the assets + checksum files,
   and generate notes from `.github/release.yml`. Capture asset URLs + SHA256.
7. **Post-release (optional)** — upload build provenance/attestations; trigger downstream docs.

> **Deferred:** publishing to **winget** adds a final "winget submit" job (`winget-submit.yml`
> or a step here) using `WINGET_TOKEN`. Documented in `docs/design/winget.md`; **not** part of
> the current pipeline.

Secrets (signing cert/password) live in GitHub Actions secrets / the `release` environment —
never in the repo, consistent with the no-secrets-in-source rule.

### Releases are managed via GitHub

**GitHub Releases is the single source of truth and distribution point** for every shipped
version, and the in-app updater + tray notifier read from it directly:

- Each release is a Git **tag** (`vX.Y.Z`) with a GitHub Release whose assets are the
  per-architecture zips (and later installers) plus their `.sha256` checksum files. Tagging
  is the trigger that drives the whole pipeline above.
- The release notes/body are generated from merged PRs/commits (e.g. GitHub auto-generated
  notes) so the changelog is maintained on GitHub, not in tracked files.
- The updater's "what's the latest version" question is answered by the GitHub Releases API
  (`releases/latest`); the tray's 6-hourly check hits the same endpoint. There is no separate
  update server to run — GitHub hosts both the metadata and the binaries.
- Pre-release/draft GitHub Releases are ignored by the stable-channel check (only published,
  non-prerelease releases are considered), giving a built-in staging path.
- Asset URLs are stable (`releases/download/<tag>/<name>`), so the in-app updater, the tray
  notifier, the README "latest" link (and a future winget manifest) all reference the same
  GitHub-hosted artifacts.

## Auto-update (in-app, via Settings)

The GUI exposes auto-update in the Settings dialog, built on the versioned-directory layout
above. The updater never modifies a running version's files in place; it stages a new
version folder and repoints `current`.

### Update flow

1. **Check.** An `UpdateService` queries the latest GitHub Release (the
   `releases/latest` API) and compares its tag against the running
   `InformationalVersion`. A check runs on a Settings button ("Check for updates now") and,
   if enabled, periodically/at startup (event-driven, no busy-wait timers).
2. **Download.** If newer, download the matching-architecture zip asset into
   `app\updates\<version>.zip`, verifying its **SHA256** against the value published with the
   release (the per-asset `.sha256`) before trusting it.
3. **Unzip to new directory.** Extract into `app\versions\<version>\` (a fresh directory;
   never the running one).
4. **Repoint `current`.** Replace the `current` symlink/junction to target the new version
   directory. Because shortcuts/alias/startup-task all reference `app\current\...`, they
   immediately resolve to the new exe.
5. **Restart.** Prompt the user to restart (or relaunch automatically): exit the current
   process and start `app\current\Phantom.Workspaces.exe`. The link swap can't happen on
   files locked by the running process, which is exactly why the running version's own folder
   is never touched — only the link moves.
6. **Cleanup.** After a successful launch of the new version, prune old `versions\*` folders
   (keep the previous one for rollback), and delete the staged zip.

### Rollback / safety

- If the new version fails to launch, the updater (or a tiny launcher shim) can repoint
  `current` back to the previous version directory that was intentionally retained.
- The link swap is the only mutating step and is effectively atomic; a crash mid-download
  leaves `current` untouched.
- Downloads are integrity-checked (SHA256) and only fetched over HTTPS from the project's
  GitHub Releases; signed binaries (once signing lands) add Authenticode verification.

### Settings UI

A new "Updates" category in the existing `WorkspacesSettingsViewModel`/`SettingsDialogWindow`:

- **Automatic updates**: Off / Notify only / Download & install.
- **Check for updates now** button with status (current version, latest version, progress).
- **Run automatically at startup** toggle (see next section).
- Channel selection is out of scope initially (stable only).

### Code changes

- New `UpdateService` (GUI layer): `CheckAsync()`, `DownloadAsync()`, `ApplyAsync()` —
  fully async, `ConfigureAwait` in non-UI paths, no blocking calls (GUI must not freeze).
- New `InstallLayout` helper: resolves `app\`, `current`, `versions\<v>`, `updates\`;
  creates/repoints the junction/symlink.
- New `UpdateSettingsViewModel` + AXAML category; persisted into `WorkspacesConfiguration`
  (new `Update` section: mode, last-check, optional pinned version).
- Reuse the same SHA256/release-asset metadata the release pipeline produces.

### Periodic update check (every 6 hours)

- The app checks the GitHub Releases page for a newer version **once per 6 hours** while
  running, plus once shortly after startup.
- Implemented as a scheduled background check driven by an injected clock/timer abstraction
  (`IUpdateCheckScheduler`) so the cadence is configurable and **tests can advance time
  deterministically** rather than waiting wall-clock (consistent with the no-timing-based
  tests convention).
- The interval (default `06:00:00`) is a constant/configurable setting; a check is also
  triggered on demand from Settings and from the tray icon.
- Results raise an event consumed by the Settings UI and the tray icon (toast + menu state).
  No blocking calls; all network work is async and off the UI thread.

## System tray (notification area) icon

The GUI runs a Windows tray icon for quick access and update awareness, using Avalonia's
`TrayIcon` (`Avalonia.Controls.TrayIcon` with a `NativeMenu`).

### Behavior

- **Double-click → open.** Double-clicking the tray icon shows/activates the main window
  (restoring from minimized/hidden). Single right-click opens the context menu.
- **Context menu** includes at least:
  - **Open Phantom.Workspaces** (same as double-click; the default/bold item).
  - **Check for updates** and, when an update is staged/available, **Update now** —
    the update action is available **directly** from the tray menu (calls
    `UpdateService.DownloadAsync`/`ApplyAsync`), so users can update without opening the
    window.
  - **Run at startup** (mirrors the Settings toggle; checkable item).
  - **Settings…** (opens the Settings dialog).
  - **Exit** (fully quits, including the tray icon and background update checks).
- **New-version notification.** When the 6-hourly check (or a manual check) finds a newer
  release, the tray shows a notification/toast ("Phantom.Workspaces 0.2.0 is available")
  and the menu surfaces **Update now**. Clicking the toast (or the menu item) starts the
  update flow. The toast/menu reflect progress and a "Restart to finish" state after apply.
- **Tray icon state** can reflect status (normal vs. an "update available" badge/overlay).

### Window/lifecycle interaction

- Closing the main window can minimize-to-tray (configurable) rather than exit, so the
  periodic update check and notifications keep working; **Exit** from the tray is the
  explicit full-quit. This is a setting (default: close-to-tray on, matching "run at
  startup" usage).
- The tray icon and the periodic `IUpdateCheckScheduler` share the app lifetime; both are
  torn down on Exit.

### Code changes

- New `TrayIconViewModel` (or `TrayIconController`) owning the `TrayIcon` + `NativeMenu`,
  wired in `App.axaml`/`App.axaml.cs`. Menu commands bind to existing commands
  (`UpdateService`, `StartupTaskService`, open/settings/exit on `MainWindowViewModel`).
- `UpdateService` raises an `UpdateAvailable` event the tray subscribes to for toasts and
  menu enable/label state.
- New `IUpdateCheckScheduler` (6h cadence, injectable clock) started at app launch.
- Close-to-tray handled in the main window close handler + a `Visual`/`Update` setting.

## Run automatically at startup (Windows scheduled task)

A Settings toggle, "Run automatically at startup", registers/unregisters a **Windows
Scheduled Task that runs at logon**.

- **Why a scheduled task** (vs. the `Run` registry key / Startup folder): a logon-triggered
  task can run without a console flash, survives across updates because it targets the stable
  `app\current\Phantom.Workspaces.exe` path, and can carry options (e.g. start minimized /
  delayed start) cleanly.
- **Registration**: create a per-user logon-triggered task named e.g.
  `Phantom.Workspaces Startup` whose action launches `app\current\Phantom.Workspaces.exe`
  (optionally with a `--startup`/`--minimized` argument). Implemented via the
  `Microsoft.Win32.TaskScheduler` API or by shelling `schtasks`/PowerShell
  `Register-ScheduledTask`. Per-user logon trigger needs no elevation.
- **Unregistration**: removing the toggle deletes the task.
- **Idempotent**: registering re-points an existing task at `current` (handy if the layout
  ever moves).
- **Code**: new `StartupTaskService` (Windows-only) with `Enable()` / `Disable()` /
  `IsEnabled()`; surfaced through `UpdateSettingsViewModel` (or a small
  `StartupSettingsViewModel`) and persisted as a setting. The uninstaller removes the task.

## Easy download from the GitHub Releases page

We want a one-click "get it installed" path straight from GitHub (and, later, winget — see
`docs/design/winget.md`):

- **Stable latest link.** `https://github.com/<org>/Phantom.Workspaces/releases/latest`
  always resolves to the newest release; link it prominently from the README and project
  homepage.
- **Predictable asset names.** Name release assets deterministically per architecture, e.g.
  `Phantom.Workspaces-<version>-win-x64.zip` / `-win-arm64.zip` (and the installer
  `Phantom.Workspaces-<version>-win-x64-setup.exe` once Option C lands), plus a
  `*.sha256` checksum file per asset. Stable names let users (and scripts) predict the
  download URL.
- **Self-installing zip → managed layout.** The portable zip, when first run, bootstraps
  itself into the `%LOCALAPPDATA%\Phantom.Workspaces\app\versions\<v>\` layout and creates the
  `current` link + shortcuts, so a plain "download zip and run" produces a managed,
  auto-updatable install — a first-run `--install`/elevation-free bootstrap step in
  `InstallLayout`.
- **README install section** documents the paths: "download the latest zip from Releases" and
  "build from source" (a future `winget install Phantom.Workspaces` path is tracked in
  `docs/design/winget.md`).
- Optionally a small `install.ps1` hosted in the repo that downloads `releases/latest`'s
  zip, verifies the checksum, and performs the same bootstrap — a `irm ... | iex` one-liner
  for quick installs.

## Install footprint and configuration handoff

- App binaries install per-user by default (under `%LOCALAPPDATA%`) to avoid elevation for
  the app itself.
- Runtime configuration remains `%APPDATA%\Phantom.Workspaces\config.json` via
  `ConfigurationPersistenceService`; first launch runs the setup wizard
  (`docs/design/installation.md`). Packaging must not virtualize/redirect this path — a
  consideration that specifically affects a future MSIX package.
- The MongoDB-container install flow still shells out to
  `scripts/install-mongodb-container.ps1` and may install Docker Desktop; the app package
  depends on neither at install time (they are first-run concerns).

## Documentation

- README gains a "Install" section: "download the latest zip from Releases" and a "build from
  source" section (`dotnet publish` command above). A future `winget install
  Phantom.Workspaces` line is added when winget support lands (`docs/design/winget.md`).

## Test tasks

1. **Publish smoke test** — a CI step asserts `dotnet publish` for `win-x64`/`win-arm64`
   produces a runnable single-file exe (launch `--version`/headless self-check) and bundles
   the CLI.
2. **Version consistency test** — assert the assembly `InformationalVersion`, `app.manifest`
   identity version, and the release tag all derive from the single version source.
3. **Config-path test** — verify the packaged app reads/writes
   `%APPDATA%\Phantom.Workspaces\config.json` without virtualization redirection (guards a
   future MSIX path).
4. **Install-layout test** — `InstallLayout` creates `versions\<v>\`, repoints the
   `current` junction/symlink atomically, and `current\Phantom.Workspaces.exe` resolves to
   the active version (falls back to junction when symlink privilege is absent).
5. **Update apply test** — given a staged version folder, `UpdateService.ApplyAsync` repoints
   `current` and leaves the previous version retained for rollback; a corrupted/hash-mismatch
   download is rejected and `current` is untouched.
6. **Update check test** — `UpdateService.CheckAsync` reports "update available" only when the
   latest release tag is strictly newer than the running version (deterministic, mocked
   release source; no network/timing waits).
7. **Startup-task test** — `StartupTaskService.Enable/Disable/IsEnabled` registers a per-user
   logon task targeting `app\current\Phantom.Workspaces.exe`, is idempotent, and removal
   deletes it (mock the scheduler API; no real task side effects in unit scope).
8. **Zip bootstrap test** — first-run bootstrap of the portable zip produces the managed
   `app\` layout + `current` link equivalent to the installer's.
9. **Update-check cadence test** — `IUpdateCheckScheduler` triggers a check ~once per 6 hours
   using an injected clock; advancing virtual time fires checks deterministically (no
   wall-clock waits), and the interval is configurable.
10. **Tray menu test** — `TrayIconViewModel` exposes Open/Check-for-updates/Update-now/Run-at-
    startup/Settings/Exit; "Update now" is enabled only when an update is available and invokes
    `UpdateService`; double-click activates the main window.
11. **Tray notification test** — an `UpdateAvailable` event from `UpdateService` raises a tray
    toast and flips the menu into the "Update now / update available" state (event-driven,
    mocked notification sink).
12. **Latest-release selection test** — the GitHub Releases query ignores draft/pre-release
    entries and selects the newest published, non-prerelease tag (mocked release source).

> winget-specific test tasks (manifest validation, installer silent-switch, manifest hash
> match) are deferred to `docs/design/winget.md`.

## Future (non-Windows)

These are **placeholders**; each gets its own design doc when we ship the platform. The
central version source and the `current`-symlink install layout are intended to carry over.

### macOS (placeholder)

- TODO: full design. Sketch: build a `.app` bundle, ship a signed + **notarized**
  `.dmg`/`.pkg` GitHub Release asset.
- The `current`-symlink layout maps cleanly to an app-bundle-in-`~/Applications` updater.
- "Run at startup" maps to a `LaunchAgent` (`~/Library/LaunchAgents`) instead of a scheduled
  task; tray icon maps to an `NSStatusItem`.
- Package-manager distribution: **Homebrew cask** (the winget analog) — see the placeholder
  in `docs/design/winget.md`.

### Linux (placeholder)

- TODO: full design. Sketch: framework-dependent or self-contained tarball as the portable
  artifact, with the same self-managed `current` layout under `~/.local/share`.
- "Run at startup" maps to a **systemd user service** / XDG autostart entry; tray icon maps to
  a `StatusNotifierItem`/AppIndicator.
- Package-manager distribution: `.deb`/`.rpm` repositories, **Flatpak** (Flathub), and/or Snap
  — see the placeholder in `docs/design/winget.md`.
