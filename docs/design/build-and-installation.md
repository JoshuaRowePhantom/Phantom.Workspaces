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
- Secondary executables exist (`Phantom.Workspaces.Agent.Cli`, `Phantom.Workspaces.Web.Server`),
  but they are **not shipped for now**. The installable product is the **GUI app only**; the
  CLI/server may be packaged later.

## Build

### Local/dev build

- Restore + build via the solution: `dotnet build Phantom.Workspaces.slnx -c Release`.
- Tests run through the approved harness `.\scripts\run-tests.ps1` (results in
  `scripts\test-results.log`).

### Linux build/test on a Windows desktop via WSL

The Docker-backed data-layer tests (`Category=SlowDocker`, e.g. MongoDB Atlas Local) need a
**Linux** container engine, which hosted `windows-latest` cannot provide. A Windows developer
can reproduce the `ubuntu-latest` CI job **locally** using **WSL2**, which provides a real
Linux kernel + Docker. This lets you run the same `-Mode full` suite the Linux CI job runs,
before pushing.

#### One-time setup

1. **Enable WSL2 + a distro:** `wsl --install -d Ubuntu` (reboot if prompted), then set WSL2 as
   default (`wsl --set-default-version 2`).
2. **Docker in WSL:** either install **Docker Desktop** with the **WSL2 integration** enabled
   for the Ubuntu distro, or install the native Docker engine inside the distro. Verify with
   `docker run hello-world` from inside WSL.
3. **.NET 10 SDK in WSL:** install the Linux .NET 10 SDK inside the distro (Microsoft package
   feed or the install script). Verify `dotnet --info` shows the SDK.
4. **PowerShell in WSL:** install **PowerShell (`pwsh`)** in the distro, because the approved
   harness is `scripts/run-tests.ps1`. `pwsh` runs it unchanged (the script is cross-platform —
   `dotnet test` on the `.slnx`, `Join-Path`, `Set-Content`).
5. **Clone into the WSL filesystem** (e.g. `~/src/Phantom.Workspaces`), **not** under
   `/mnt/c/...`. Building/testing on the native ext4 filesystem is dramatically faster than the
   Windows-mount path, and avoids cross-OS file-watcher/permission quirks.

#### Running the Linux suite

From inside WSL, in the repo root:

```bash
# the cross-platform Docker/data-layer tests (mirrors the ubuntu-latest CI job)
pwsh ./scripts/run-tests.ps1 -Mode full -TestNames Phantom.Workspaces.Data.MongoDB.Tests
# or the whole non-Windows-only set
pwsh ./scripts/run-tests.ps1 -Mode full
```

- Results land in `scripts/test-results.log` exactly as on Windows.
- The MongoDB Atlas Local container is started/managed by the test fixture; per the existing
  design it is left running between runs, so the first run pays the container start cost and
  subsequent runs are fast.

#### What does and doesn't run on Linux

- **Runs on Linux:** the platform-agnostic .NET test projects — data layers (MongoDB/offline/
  data-core), LLM/core, tools, schema/serialization. These are exactly what the `ubuntu-latest`
  job covers.
- **Does not run on Linux:** Windows-only tests — the install/update/tray/startup integration
  (junction/symlink, `StartupTaskService`, single-instance) and any Win32-specific paths. Run
  those from Windows (`.\scripts\run-tests.ps1`). Avalonia GUI/headless tests can run on Linux
  only with a virtual display (`xvfb`) and are otherwise kept on the Windows job.
- The **GUI app itself is Windows-only** (`WinExe`); WSL is for *building/testing the
  cross-platform libraries and Docker integrations*, not for producing the shipped exe.

#### Why this mirrors CI

This is deliberately the same split as the CI matrix (*GitHub Actions workflows* below):
`windows-latest` for build/GUI + fast tests, `ubuntu-latest` for the Docker/data-layer suite.
WSL gives a developer the Linux half on their Windows box, so a red Linux-only test can be
reproduced and fixed locally instead of round-tripping through CI.

### Manual build/install verification (`scripts\test-install.ps1`)

A developer script that exercises the **whole** build → package → install → update →
uninstall flow on the local desktop, in a **sandbox**, without creating a GitHub release or
touching the developer's real install. This lets us validate packaging/updater behavior in
seconds instead of waiting for a release round-trip.

#### What it does (stages)

1. **Publish.** Run the same self-contained single-file `dotnet publish` the release pipeline
   uses, for the current runtime identifier (default `win-x64`), into a temporary staging
   folder. A `-FastPublish` switch can skip ReadyToRun and single-file compression for quicker
   iteration.
2. **Package.** Assemble the versioned portable zip + compute its `.sha256`, exactly as the
   packaging step would — producing a real artifact to install from.
3. **Serve a fake release (optional).** Stand up a **local release source** so the in-app
   updater can be pointed at it without GitHub: either a `file://` folder or a tiny localhost
   HTTP server that mimics the `releases/latest` + asset-download + `.sha256` shape. The script
   writes a manifest describing version `A` (and later `B`).
4. **Install into a sandbox.** Run `Phantom.Workspaces.exe --install --silent` with the install
   root **overridden** to a throwaway sandbox directory (see *Required seam* below) instead of
   `%LOCALAPPDATA%`. Assert the managed layout was created: `app\versions\A\`, the `current`
   junction/symlink resolving to `A`, and (optionally) shortcuts/startup-task creation in a
   sandboxed/skipped mode.
5. **Simulate an update.** Publish + package a higher version `B`, publish it to the fake
   release source, then drive the updater path — either by invoking the staged
   `--apply-update <version-B-directory> --relaunch` directly, or by triggering `UpdateService`
   against the local source. Assert `current` now resolves to `B`, version `A` is retained for
   rollback, and the relaunched executable reports `B`.
6. **Rollback check (optional).** Force `B` to "fail to start" (a flag/marker) and assert the
   next launch repoints `current` back to `A`.
7. **Uninstall + clean up.** Run `--uninstall --purge` against the sandbox and assert shortcuts,
   startup task, and the `app\` tree are removed; then delete the sandbox and stop the local
   server. The script is **idempotent** and always cleans up (even on failure, via `finally`).

Each stage prints PASS/FAIL and the script exits non-zero on the first failure, so it doubles
as a smoke test a developer (or a pre-release skill) can run on demand.

#### Required seam: install-root + update-source overrides

For the script to run safely it must **not** write to the real per-user install or hit GitHub:

- **Install-root override** — `InstallLayout` resolves its root from an override
  (environment variable `PHANTOM_WORKSPACES_INSTALL_ROOT`, or a hidden `--install-root <path>`
  argument) before falling back to `%LOCALAPPDATA%\Phantom.Workspaces\app`. The script points
  this at a temporary sandbox. (This is the same seam the unit tests use via `IFileSystem`.)
- **Update-source override** — `IReleaseSource` accepts an override base URL/path
  (`PHANTOM_WORKSPACES_UPDATE_FEED`) so the updater reads the **local** fake release instead of
  the GitHub Releases API.
- **Integration sentinels** — shortcut/startup-task creation honor a "sandbox/dry-run" mode so
  the script doesn't pollute the real Start menu or Task Scheduler (or it targets uniquely-named
  throwaway entries it deletes).

These overrides are development- and test-only and default to the production values, so shipping
behavior is unchanged.

#### Parameters (sketch)

```
scripts\test-install.ps1
  [-RuntimeIdentifier win-x64]   # publish runtime identifier
  [-FastPublish]                 # skip ReadyToRun and compression for speed
  [-Sandbox <path>]              # install root; default: a new temporary directory
  [-SkipUpdate]                  # stop after install
  [-KeepSandbox]                 # don't clean up (for inspection)
  [-Serve]                       # run the local fake-release HTTP server
```

#### Relationship to other tests

- Complements the **integration tier** in *Testing strategy*: those are categorized xUnit
  tests of individual seams (junction repoint, apply-update, scheduled task); this script is a
  **manual, end-to-end, real-artifact** walkthrough of the packaged exe.
- Mirrors what `release.yml` + the in-app updater do, minus GitHub — so passing it locally gives
  high confidence the release will install/update correctly.
- Windows-only (the install/update story is Windows). It is **not** part of the required PR
  checks (it produces real local side effects in a sandbox); it is a developer/maintainer tool,
  optionally invoked by the `create-release` skill as a pre-release confidence check.

### Release publish (Windows)

Produce a self-contained, single-file app so users need no preinstalled .NET:

```
dotnet publish Phantom.Workspaces\Phantom.Workspaces.csproj `
  -c Release `
  -r win-x64 `            # and a second pass for win-arm64
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true
```

#### How a multi-dependency app becomes one self-contained `.exe`

The app is **not** statically linked into a single native binary. .NET produces a **bundle**:
a host executable with all the managed assemblies, the runtime, and native libraries packed
into it. The relevant mechanisms:

1. **Self-contained publish** (`--self-contained true -r win-x64`). The publish output
   includes the entire **.NET runtime** (CoreCLR + the BCL assemblies) for the target RID, so
   the user needs nothing preinstalled. This is what makes it "self-hosting": the runtime
   travels with the app rather than being resolved from a machine-wide install.
2. **Dependency resolution at publish.** `dotnet publish` walks the project's transitive
   closure — every `PackageReference` (Avalonia, Skia, LibGit2Sharp, Dock, MongoDB driver,
   etc.) and every `ProjectReference` (`Phantom.Workspaces.Data.*`, `Llm.*`, `Web.Server`, …)
   — and copies the resulting managed DLLs and their **native** assets (per-RID
   `runtimes/win-x64/native/*.dll`) into the publish folder. NuGet's RID graph picks the
   correct native bits for the target architecture.
3. **Single-file bundling** (`PublishSingleFile=true`). The SDK's bundler then packs that
   publish folder into one `Phantom.Workspaces.exe`: a small native **AppHost** with all
   managed assemblies appended as an embedded bundle. The managed DLLs are read from inside the
   exe by the runtime; no extraction is needed for them.
4. **Native libraries** (`IncludeNativeLibrariesForSelfExtract=true`). Native dependencies
   (SkiaSharp's `libSkiaSharp.dll`, ANGLE/HarfBuzz, `git2-*.dll` for LibGit2Sharp, the
   Avalonia native bits) can't be loaded directly from inside the bundle on Windows, so at
   first run the AppHost **self-extracts** them to a per-user temp/cache directory and loads
   them from there (subsequent runs reuse the cache). This flag opts the native assets into the
   single file rather than leaving loose `.dll`s beside the exe.
5. **Compression** (`EnableCompressionInSingleFile=true`). The embedded bundle is compressed to
   shrink the ~70–100 MB self-contained payload; it is decompressed in memory at load. Trades a
   little startup CPU for a much smaller download.
6. **ReadyToRun** (`PublishReadyToRun=true`). Assemblies are AOT-precompiled to native code
   ahead of time (alongside IL) to cut JIT cost and improve cold start; the IL remains as
   fallback. Increases size modestly.

The net result per architecture is effectively **one `Phantom.Workspaces.exe`** (plus a PDB
produced beside it). That single exe is what the zip ships, what `versions\<v>\` holds, and
what `current` points at.

#### What ends up in the release payload

- `Phantom.Workspaces.exe` — the bundled GUI (runtime + all managed + native deps inside).
- `runtimes\<rid>\native\copilot.exe` — the GitHub Copilot CLI, shipped as a **loose file** beside
  the single-file exe, plus `copilot_runtime.dll` and the CLI `LICENSE.md` in the same folder.
  `GitHub.Copilot.SDK` resolves the CLI strictly from
  `AppContext.BaseDirectory\runtimes\<rid>\native\copilot.exe` and does **not** search PATH, and
  single-file publish drops the SDK's Content-registered binary from the bundle, so the GUI csproj
  target `PublishCopilotRuntimeLoose` copies it (and the license) from the build output into the
  publish output. The CLI is redistributed **unmodified** under the GitHub Copilot CLI License; that
  license (added as content by `Phantom.Workspaces.Llm.Core.csproj`) ships beside the binary to
  satisfy the redistribution conditions (issue #1376).
- A few assets the bundler intentionally leaves on disk if any (e.g. the WebView2 loader, if
  `Avalonia.Controls.WebView` requires a loose native loader) — verified per release and added
  to the zip staging.
- **Not in the user zip:** `*.pdb` symbol files are **not shipped** for now; they are retained
  as a **separate CI artifact** (and a future symbol server) for crash diagnostics.
- **Not shipped for now:** `Phantom.Workspaces.Agent.Cli.exe` and the web server — GUI only.

#### Caveats this design accounts for

- **Trimming is off** (`PublishTrimmed=false`) initially: Avalonia XAML, reflection-heavy
  schema/DI paths, and the `x-field-editor` `Type.GetType` activation can break under the
  trimmer. Size is controlled via compression instead; a trim/`TrimmerRootDescriptor` model is
  a later optimization.
- **Per-RID publish**: single-file bundles are RID-specific, so we run publish once per
  `win-x64` and `win-arm64` (no "AnyCPU" single file). `RuntimeIdentifiers` lists both.
- **Native self-extract dir**: the first-run extraction location is a per-user cache; it must
  be writable without elevation (it is, under the user profile) — consistent with the per-user
  install model.
- **WebView2 runtime**: `Avalonia.Controls.WebView` relies on the system **WebView2 Runtime**
  (Evergreen), which is *not* bundled. It is preinstalled on current Windows; the app should
  detect its absence and point the user to install it (a first-run concern, not a build one).

Notes / proposed project settings (added to `Phantom.Workspaces.csproj` or a
`Directory.Build.props` publish section):

- `RuntimeIdentifiers` = `win-x64;win-arm64`.
- `PublishTrimmed=false` initially (see caveat above; revisit with a trim model later).
- `PublishReadyToRun=true` for faster cold start.
- `PublishSingleFile=true`, `IncludeNativeLibrariesForSelfExtract=true`,
  `EnableCompressionInSingleFile=true` — applied for **publish only** (guarded so normal
  `dotnet build`/F5 debugging is unaffected).
- `SelfContained=true` with the runtime included.
- Only the GUI app is published/packaged for now (no CLI/server in the release payload).

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
      0.1.0\           Phantom.Workspaces.exe + all assets
      0.2.0\           Phantom.Workspaces.exe + all assets
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

## The `Phantom.Workspaces.exe` executable

A **single** self-contained executable serves every role — first-run installer/bootstrapper,
normal GUI, updater, and the target of shortcuts/startup task — selected by command-line mode.
There is no separate installer binary; the same exe that ships in the zip installs and updates
itself.

### Command-line surface

`Program.Main` parses arguments and dispatches to a mode. Every mode is a **GUI** mode — there
is no console/headless path; install/update modes simply show a lightweight progress window
instead of the main window. All modes share the same composition root (configuration, logging,
services); only the entry behavior and which window is shown differ.

- *(no args)* — **normal GUI launch.** If running from an *unmanaged* location (e.g. an
  extracted zip not under `app\versions\`), perform first-run bootstrap with a progress window
  (see below); otherwise start the main window.
- `--install [--silent]` — **bootstrap** into the managed layout, then launch the installed
  app. Interactive shows an installation **progress window**; `--silent` runs without UI and
  returns an `ExitCode` (for scripted installs).
- `--startup` — normal launch honoring startup preferences (start minimized to tray, skip
  splash). This is the argument the logon scheduled task passes.
- `--minimized` — start hidden/minimized to the tray.
- `--apply-update <stagedVersionDir> [--relaunch]` — internal mode used by the updater to
  repoint `current` after the previous process exits, showing a small "Updating…" progress
  window, then relaunching (see *Update application & relaunch*). Logs to a **file** and
  returns an `ExitCode`.
- `--uninstall [--purge]` — remove shortcuts, the startup task, and (with confirmation) the
  managed `app\` tree via a small GUI; leave `%APPDATA%` config unless `--purge` is given.
- `--help` / `-h` — show usage (a dialog; the app is GUI-only).

Argument parsing is centralized in a `CommandLineOptions` parser (testable in isolation; no
side effects from parsing). Unknown arguments are reported (dialog) with a non-zero exit code,
except that a single positional path may be accepted later for "open entity/file" associations.

### Everything is GUI — no console mode needed

`Phantom.Workspaces` is `OutputType=WinExe` → **GUI subsystem**, which is exactly what we want:
launched from a shortcut/Explorer it never flashes a console window. We deliberately do **not**
add any console/stdout behavior (no `AttachConsole`, no hybrid console attach):

- **Install/update progress is shown in the GUI**, not printed to a terminal. The `--install`
  and `--apply-update` modes spin up a minimal Avalonia progress window (the full main window,
  tray, and update scheduler are only created for the actual app launch). There is no
  requirement to "avoid instantiating Avalonia" — every mode may use the UI toolkit freely.
- **Inter-process steps use exit codes, not text.** The updater spawns `--apply-update` and
  **waits on the process handle directly**, reading the returned `ExitCode`; nothing depends on
  stdout or on a shell waiting for the process.
- **CI reads the version from file metadata.** The publish smoke check reads the published
  exe's `FileVersionInfo` (deterministic, no process launch) rather than running a `--version`
  command, so no console output is ever needed. (There is therefore no `--version`/stdout mode.)

This keeps the binary a clean single-purpose GUI app and removes the entire console-subsystem
problem from the design.

### First-run bootstrap (`--install` / run-from-zip)

When the exe runs from outside the managed layout, `InstallLayout.BootstrapAsync`:

1. Resolves the install root `%LOCALAPPDATA%\Phantom.Workspaces\app\`.
2. Copies the current published payload (the exe + assets sitting next to it) into
   `versions\<thisVersion>\`. If the source is still a zip, extract it; if it is an unpacked
   folder, copy it.
3. Creates/repoints the `current` junction (preferred) or symlink to that version.
4. Registers per-user integration: Start-menu shortcut to `app\current\Phantom.Workspaces.exe`,
   an optional PATH/App-Execution-Alias entry, and (if the user opted in) the logon startup
   task — all pointing at the stable `current` path.
5. Writes a small install marker/metadata (installed version, channel, install timestamp) so
   subsequent launches know they are "managed".
6. Relaunches the managed copy (showing/closing a progress window as appropriate) and exits;
   in `--silent` mode it skips UI and exits `0` for installer/script callers.

Bootstrap is **idempotent** and elevation-free (per-user). Re-running it repairs the
`current` link and shortcuts.

### Single-instance behavior

- GUI launches acquire a **named mutex** (per-user) so a second launch (e.g. double-clicking
  the tray target while running) **activates the existing window** instead of starting a
  second process, then exits. The activation is signalled to the running instance (named pipe
  / event) so it can restore from tray.
- The `--apply-update` mode does **not** take the single-instance lock (it must run while it
  waits for the previous instance to release it); its progress window is a standalone
  lightweight window, not the main app.

### Update application & relaunch (interaction with the updater)

The running GUI cannot replace files it has locked, so the `current` repoint is performed by a
short-lived process, not the live instance:

1. The GUI's `UpdateService` stages the new version into `versions\<new>\` (download → verify
   SHA256 → extract).
2. To apply, it spawns `app\versions\<new>\Phantom.Workspaces.exe --apply-update
   <newVersionDir> --relaunch`, then begins shutting itself down.
3. The spawned **apply** process waits for the previous instance to release its
   single-instance lock (bounded wait), atomically repoints `current` to `<newVersionDir>`,
   prunes superseded `versions\*` (retaining the immediately previous one for rollback), and —
   with `--relaunch` — starts `app\current\Phantom.Workspaces.exe` (which is now the new
   version) and exits.
4. On launch, the new version verifies it started successfully; a **launcher/health gate**
   records "this version booted OK". If a freshly-applied version fails to reach the ready
   state, the next launch (or the apply shim) repoints `current` back to the retained previous
   version — automatic rollback.

This keeps the only mutating step (the link repoint) in a process that holds no locks on the
files being swapped, and never touches the directory of the version that is currently running.

### Exit codes

Well-defined exit codes (used by `--silent` install and by the updater, which waits on the
`--apply-update` process handle): `0` success, `1` generic failure, `2` bad arguments,
`3` bootstrap/IO failure, `4` update-apply failure (left `current` untouched). Enumerated in
code as `ExitCode`.

### Code shape

- `Program.Main` → `CommandLineOptions.Parse` → `switch` over mode to handlers:
  `RunGui`, `RunInstall`, `RunApplyUpdate`, `RunUninstall` (each builds whatever Avalonia
  window — main or progress — its mode needs).
- `InstallLayout` (shared with the updater) owns root/`current`/`versions`/`updates`
  resolution, junction/symlink creation, and bootstrap. The root is resolvable via an
  override (`PHANTOM_WORKSPACES_INSTALL_ROOT` / `--install-root`) ahead of the default
  `%LOCALAPPDATA%` path, so tests and `scripts\test-install.ps1` can run in a sandbox.
- `IReleaseSource` resolves the update feed, with an override
  (`PHANTOM_WORKSPACES_UPDATE_FEED`) so the updater can be pointed at a local fake release.
- `SingleInstanceGuard` owns the mutex + activation signalling.
- `ExitCode` enum centralizes process results.

## Windows packaging options

We package the published output as one of the following. winget `installerType` mapping for
each is deferred to `docs/design/winget.md`.

### Option A — Standalone EXE / portable zip (lead)

- Publish the self-contained single-file `Phantom.Workspaces.exe` and ship as a
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
  - Steps: checkout → setup .NET 10 → restore → build `-c Release` →
    `.\scripts\run-tests.ps1 -Mode fast` → upload `scripts\test-results.log` as a workflow
    artifact (always) and **fail the job on a non-zero script exit** (red log).
  - **Test execution detail:** the pipeline runs the *same* approved script the design uses
    locally (`.\scripts\run-tests.ps1`), not raw `dotnet test`. `-Mode fast` excludes
    `Category=SlowGit` and `Category=SlowDocker` tests, so every PR gets quick, deterministic
    feedback without requiring Docker on the runner.
  - Concurrency group per ref to cancel superseded runs.
- **`ci-full.yml`** (or a scheduled/`workflow_dispatch` job) — runs the **full** suite
  (`.\scripts\run-tests.ps1 -Mode full`) including the `SlowGit` and `SlowDocker` integration
  tests. **Docker caveat:** the `SlowDocker` tests start **MongoDB Atlas Local**, a *Linux*
  container, which **cannot** run on hosted `windows-latest` (no Linux-container support there).
  Because these data-layer tests are platform-agnostic .NET (only the app is Windows-only),
  this job runs on **`ubuntu-latest`**, where Docker + Linux containers work natively. So the
  matrix is: `windows-latest` for build/GUI + fast tests, `ubuntu-latest` for the Docker/Git
  integration suite. Scheduled (e.g. nightly), on-demand, and **gated before release**. A
  self-hosted Docker-capable Windows runner is only needed if a test ever requires *Windows*
  containers specifically.
  - **Targeting on Linux:** the full suite also contains **Windows-only** tests (Avalonia GUI,
    the `StartupTaskService`/scheduled-task and junction/symlink integration tests). The
    `ubuntu-latest` job therefore runs the **cross-platform data-layer Docker tests** (e.g. via
    `-TestNames Phantom.Workspaces.Data.MongoDB.Tests` or a dedicated category), while the
    Windows-only integration tests run in a Windows job. This keeps each platform running only
    the tests valid for it; combined, the two jobs cover the whole `-Mode full` set.
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

### Agent skills (`.github/skills/`)

- **`create-release/SKILL.md`**, **`check-release-status/SKILL.md`**,
  **`draft-release-notes/SKILL.md`**, **`rollback-release/SKILL.md`** — release skills that let
  the AI drive the pipeline safely (detailed in *Release skills (AI-assisted releases)* below).
  Same `SKILL.md` format as the existing `run-tests` skill.

### Secrets and variables

- **Actions secrets (repo or org):** `AVALONIA_UI_LICENSE_KEY` (compile-time Avalonia
  license), `CODE_SIGN_CERT` (base64 PFX) + `CODE_SIGN_PASSWORD` (signing), and **(deferred)**
  `WINGET_TOKEN`. All stored as GitHub secrets — never in source.
- **Environment `release`:** scopes signing/release secrets to the release workflow and can
  require a manual approval before publishing.
- **Variables:** non-secret config such as the package identifier and asset-name prefix.

### Branch protection / rulesets

- Protect `main`: require status checks to pass, require review + `CODEOWNERS`, linear
  history; restrict `v*` tag creation to maintainers (drives releases). Full policy in
  *Pull request acceptance* below.

### Packaging assets in-repo (`build/` or `packaging/`)

- **`packaging/zip/`** — scripts that assemble the portable zip from `dotnet publish` output
  and emit the per-asset `.sha256`.
- **`packaging/inno/Phantom.Workspaces.iss`** or **`packaging/wix/`** — Inno Setup script / WiX
  project for the "installed" experience (Option C), added at that milestone.
- **`install.ps1`** (repo root or `packaging/`) — `irm … | iex` bootstrap that downloads
  `releases/latest`, verifies the checksum, and performs the managed-layout install.
- **`scripts\test-install.ps1`** — developer script that runs the full publish → package →
  install → update → uninstall flow in a sandbox against a local fake release (see *Manual
  build/install verification* above); reuses `packaging/zip` and the install-root/update-feed
  overrides.
- These are tracked, secret-free build inputs; no certificates or tokens are committed.

### Release outputs (per `vX.Y.Z` GitHub Release)

- `Phantom.Workspaces-<version>-win-x64.zip` + `.sha256`
- `Phantom.Workspaces-<version>-win-arm64.zip` + `.sha256`
- **(later)** `Phantom.Workspaces-<version>-win-x64-setup.exe` + `.sha256` (Option C installer)
- Auto-generated release notes (from `.github/release.yml` categories).
- These stable-named, hashed assets are what the in-app updater, the tray notifier, the
  README "latest" link, and a future winget manifest all consume.

## Pull request acceptance

How changes get from a branch/fork into `main`. The goal is a small, automated,
convention-enforcing gate so the AI agent and human contributors follow the same path, and so
every commit on `main` is releasable.

### Contribution flow

1. **Branch / fork.** Work happens on a feature branch (or fork). Recommended: prefix branch
   names with the author's username, e.g. `jrowe/entity-editor`.
2. **Open a PR into `main`** using the PR template. The description states intent, links any
   issue, and notes whether changes touch the filesystem/Git/data layers (which decides whether
   the full/Docker suite must run — see *Required checks*).
3. **Automated checks run** (below). The author iterates until green.
4. **Review.** At least one approving review is required; `CODEOWNERS` auto-requests the right
   reviewers for touched areas (e.g. `docs/design/`, workflows, packaging, data layer).
5. **Address feedback**, keeping the branch up to date with `main` (rebase/merge per the
   linear-history rule — never rewrite already-pushed shared history without agreement).
6. **Merge** once all required checks pass and approvals are in (below).
7. **Releases are separate.** Merging does **not** publish; a maintainer cuts a release later
   via the `create-release` skill (tag → `release.yml`). `main` is always in a releasable state.

### Required status checks (branch protection on `main`)

A PR is mergeable only when these pass:

- **`ci.yml` (build + fast tests)** on `windows-latest` — `dotnet build -c Release` and
  `.\scripts\run-tests.ps1 -Mode fast` green (`scripts\test-results.log`). Required on every PR.
- **Data-layer Docker tests** on `ubuntu-latest` — required **when the PR touches the data
  layers** (MongoDB/offline/data-core) or their tests; runs the cross-platform `SlowDocker`
  suite. For PRs that don't touch those areas it may be skipped/non-blocking to keep feedback
  fast. (Path-filtered trigger.)
- **Windows-only integration tests** — required when the PR touches install/update/tray/startup
  code (junction/symlink, `StartupTaskService`, single-instance, GUI).
- Optional advisory checks (non-blocking): **CodeQL**, **dependency-review**. These inform
  review but don't hard-block unless they flag a security issue policy says must be fixed.

The build is **failed on a red `scripts\test-results.log`** (non-zero script exit); the log is
uploaded as a workflow artifact for inspection.

### Review and ownership rules

- **≥1 approving review** from a code owner of the touched area; **0 unresolved
  "request-changes"** reviews.
- **`CODEOWNERS`** maps areas → reviewers so the right people are pulled in automatically.
- **Stale-approval dismissal:** new commits after an approval re-request review (so approvals
  always reflect the merged code).
- **No self-approval** of one's own PR for the required review.

### Merge policy

- **Linear history** required → **squash-merge** is the default (one tidy commit per PR on
  `main`); the squash commit message is derived from the PR title/description and feeds the
  auto-generated release notes categorized by `.github/release.yml`.
- **No direct pushes to `main`** (including maintainers) — everything goes through a PR.
- **Up-to-date with `main`** required before merge (so checks ran against the final tree).
- **Conversation resolution** required (all review threads resolved).
- The Copilot co-author trailer is included on agent-made commits per repo convention.

### Agent-specific rules

- The AI follows the **same** gate: it must get `ci.yml` green via `.\scripts\run-tests.ps1`
  before requesting merge, and it **never commits, merges, or tags without explicit user
  instruction**.
- The agent uses `gh` for PR operations (open/status/merge) consistent with the repo's
  `gh`-CLI preference, and never force-pushes shared history.
- Merging and releasing are distinct: the agent may prepare and merge a PR when asked, but
  releases go through the `create-release` skill and an explicit instruction.

### Enforcement artifacts

- A **branch ruleset** on `main` encodes: required checks (the jobs above), required review +
  code-owner review, linear history, conversation resolution, up-to-date-before-merge, and
  no-direct-push.
- **`CODEOWNERS`**, the **PR template**, and `.github/release.yml` (notes categories) are the
  tracked files that make the policy concrete; all are secret-free.

## Release automation (CI/CD)

The **`release.yml`** workflow, triggered on a `v*` tag (or `workflow_dispatch`), running on
`windows-latest` under the `release` environment:

1. **Setup** — `setup-build` composite action (checkout, .NET 10, cache, license-key env).
2. **Build/test** — restore, build `-c Release`, run `.\scripts\run-tests.ps1`; fail on
   non-zero / failing log (upload the log artifact on failure).
3. **Derive version** — read the single version source (tag → `Version`/`InformationalVersion`)
   so every artifact and asset name is consistent.
4. **Publish** — `dotnet publish` the GUI for `win-x64` and `win-arm64` (self-contained
   single-file, ReadyToRun) into per-arch folders.
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

## Release skills (AI-assisted releases)

We add repository **skills** (the same `.github/skills/<name>/SKILL.md` mechanism as the
existing `run-tests` skill) so the AI agent can cut releases through a single, codified,
guard-railed workflow rather than ad-hoc commands. Each skill is a `SKILL.md` with YAML
frontmatter (`name`, `description`) plus **Commands** and **Rules** sections, and is invoked by
name.

### Skills to create (under `.github/skills/`)

- **`create-release`** — the orchestrator skill the agent uses to ship a version. It codifies
  the full sequence and the preconditions:
  1. Verify the working tree is clean and on the release branch (e.g. `main`), pulled
     up-to-date.
  2. Run the test suite via the `run-tests` skill (`.\scripts\run-tests.ps1`) and confirm
     `scripts\test-results.log` is green — **never** release on a failing/again-untested tree.
  3. Determine the next version from the single version source / SemVer bump (major/minor/patch
     chosen by the user), and confirm it is strictly greater than the latest release tag.
  4. Create and push the annotated `vX.Y.Z` **tag** (tagging is what triggers `release.yml`).
  5. Watch the `release.yml` run to completion (`gh run watch`) and report status.
  6. Verify the resulting GitHub Release has the expected per-arch zip + `.sha256` assets and
     auto-generated notes.
- **`check-release-status`** — a read-only skill to inspect the latest release(s) and the most
  recent `release.yml` run: `gh release view`, `gh run list --workflow release.yml`, asset
  presence, and whether the in-app updater would see it (published, non-prerelease). Useful for
  "did my release succeed?" without re-cutting.
- **`draft-release-notes`** — generate/preview release notes for the pending range (from the
  last tag to `HEAD`) so the human can review before tagging; complements GitHub's
  auto-generated notes configured in `.github/release.yml`.
- **`rollback-release`** — guarded skill to mark a bad release as pre-release/draft (so the
  stable-channel updater stops offering it) and, if needed, publish the previous version as
  latest. Deliberately conservative: it never force-pushes or rewrites history (per the
  no-rewrite-history convention) and asks for explicit confirmation.

### Skill content and guardrails

Each skill's **Rules** section encodes the repository conventions so the agent cannot drift:

- Always gate a release on a green `.\scripts\run-tests.ps1` (reuse the `run-tests` skill).
- Never commit/tag without explicit user instruction; never push to `microsoft/winget-pkgs`
  (winget is deferred — see `docs/design/winget.md`).
- Use `gh` for all GitHub operations (releases, runs, tags) — consistent with the repo's
  `gh`-CLI preference.
- Never put secrets in commands or logs; signing/license secrets live only in Actions secrets.
- Versions are SemVer and strictly increasing; the agent confirms the bump with the user.
- Small, reviewable steps; the agent surfaces what it is about to do (tag name, version,
  workflow run URL) before destructive actions.

### Relationship to the pipeline

These skills are the **human/agent front-end** to the `release.yml` pipeline described above:
the skill prepares and triggers (tag + watch), while GitHub Actions does the build, sign,
package, and publish. The skills add no new build capability — they make the existing pipeline
safely drivable by the AI, with the conventions baked into `SKILL.md` so behavior is
reproducible across sessions.

### Test / validation tasks for the skills

- A docs/skill lint test (extend the existing docs tests) asserts each release skill's
  `SKILL.md` has valid frontmatter (`name`, `description`) and the required **Commands**/
  **Rules** sections.
- A dry-run check that `create-release` references the `run-tests` gate and uses `gh` (not raw
  git pushes to third-party repos), guarding against convention drift.

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

## Testing strategy

Most of this feature touches the OS (filesystem links, scheduled tasks, processes, the console
subsystem, GitHub network, and an Avalonia GUI), so the strategy is to **push logic behind
narrow interfaces** and test the logic with fakes, reserving a small number of
opt-in/integration tests for the genuinely OS-specific seams. Three tiers:

### 1. Unit tests (the bulk — fast, deterministic, no OS/network)

Everything that can be made a pure-ish function or a service over an interface is unit-tested
with in-memory fakes. Key seams to introduce so this is possible:

- **`IFileSystem`** (or abstract `InstallLayout` over `System.IO.Abstractions`) — so
  `InstallLayout`/bootstrap/`--apply-update` run against an **in-memory filesystem**. The
  `current` link is modelled as an indirection the fake can represent, so "repoint" and
  "resolve `current\Phantom.Workspaces.exe`" are assertable without real symlinks/junctions.
- **`IReleaseSource`** — wraps the GitHub Releases API; tests inject canned release lists
  (newer/older/draft/pre-release) so `UpdateService.CheckAsync` and latest-release selection
  are deterministic with **no network**.
- **`IClock` + `IUpdateCheckScheduler`** — virtual time. The 6-hour cadence is tested by
  **advancing the clock**, never by `Task.Delay`/wall-clock waits (matches the
  deterministic-tests convention).
- **`IProcessLauncher`** — wraps spawning `--apply-update` and relaunch, so the
  update→apply→relaunch handshake is verified by asserting *what* would be launched and the
  wait/exit-code contract, without starting real processes.
- **`IScheduledTasks`** — wraps Task Scheduler; `StartupTaskService` is unit-tested against a
  fake (register/unregister/idempotent/targets `current`).
- **`INotifier`** — wraps tray toasts; tray-notification tests assert an `UpdateAvailable`
  event raises a toast and flips menu state against a fake sink.
- **`CommandLineOptions.Parse`** — pure function, exhaustively unit-tested (every mode, unknown
  args → exit `2`, no side effects).
- **`UpdateService`** download/verify/apply orchestration — tested with the fakes above:
  hash-mismatch is rejected and `current` is untouched; previous version retained for rollback.
- **ViewModels** (`UpdateSettingsViewModel`, `TrayIconViewModel`, `StartupSettingsViewModel`) —
  standard Avalonia VM tests: command `CanExecute`/enable state, "Update now" only when
  available, no view required.

These run under the existing harness — `.\scripts\run-tests.ps1` (results in
`scripts\test-results.log`) — in a new `Phantom.Workspaces.Tests` area (or a dedicated
`Phantom.Workspaces.Install.Tests`).

### 2. Integration tests (opt-in, Windows-only, real OS seams)

A small, explicitly-categorized set (e.g. `[Trait("Category","WindowsIntegration")]`) that
exercises the real behavior the fakes stand in for, run on the Windows CI runner but skippable
locally:

- **Junction/symlink reality** — create a real junction in a temp dir, repoint it, resolve
  through it; assert the fallback from symlink→junction when symlink privilege is absent.
- **Scheduled task reality** — register/query/delete a real per-user logon task in a temp name,
  assert it targets `current`, then clean up.
- **Single-instance** — launch two real processes; assert the second activates the first and
  exits.
- **Apply-update end-to-end** — stage two fake "version" folders containing tiny exes, run the
  real `--apply-update`, assert `current` moved and relaunch happened.

These have real side effects, so each uses a unique temp root/task name and cleans up; none
touch the developer's actual install or processes (respecting "don't kill my processes").

### 3. CI / packaging checks (in the workflows, not xUnit)

- **Publish smoke** (per RID) — `dotnet publish` the GUI, then read its `FileVersionInfo` (no
  process launch, no console needed) and assert the bundle is present (see Test task 1).
- **Copilot runtime bundled** (per RID) — `packaging\validate\Assert-CopilotRuntimePayload.ps1`
  asserts the published payload contains the loose file `runtimes\<rid>\native\copilot.exe`
  (`Publish_IncludesCopilotRuntime_ForEachRid`) and the GitHub Copilot CLI `LICENSE.md` beside it
  (`Distribution_IncludesCopilotCliLicense`), and — for the host-arch RID — runs the bundled
  `copilot.exe --version` to confirm the runtime launches (`InstalledPayload_StartsCopilotProvider_Smoke`).
  Wired into `release.yml` (release gate) and `publish-validation.yml`. Rationale: the SDK resolves
  the CLI strictly from `AppContext.BaseDirectory\runtimes\<rid>\native\copilot.exe` (no PATH
  search); single-file publish drops that Content-registered binary, so it must be re-added as a
  loose file (issue #1376). The `--version` smoke is skipped for the cross-arch payload because the
  bundled binary only executes on a matching CPU.
- **Copilot SDK version pin** — `packaging\validate\Assert-CopilotSdkVersion.ps1` asserts
  `GitHub.Copilot.SDK` is pinned to the reviewed version in `Directory.Packages.props`
  (`PackageVersions_CopilotSdk_IsExpectedPinnedVersion`). The redistributed CLI version is fixed 1:1
  by the SDK version (SDK `1.0.11` -> CLI `1.0.79`), so an unreviewed bump would silently change the
  bundled binary.
- **Version consistency** — assembly `InformationalVersion` == `app.manifest` == release tag.
- **Release-artifact hash** — uploaded asset SHA256 matches the published `.sha256`.
- **Subsystem assertion** — verify the published exe's PE header is GUI-subsystem (no console
  flash) — a tiny header check in CI.

### Cross-cutting principles

- **Determinism:** no timing-based waits anywhere — virtual clocks and event-driven
  synchronization only.
- **No real network in unit tests:** all GitHub access is behind `IReleaseSource`.
- **Reproduce-bug-first:** when a packaging/update bug is found, add a failing test (usually at
  the unit tier via the relevant fake) before fixing.
- **GUI never blocks:** updater/check paths are async; VM tests assert no synchronous blocking.

## Test tasks

1. **Publish smoke test** — a CI step asserts `dotnet publish` of the GUI for
   `win-x64`/`win-arm64` produces a single-file exe and reads its version from `FileVersionInfo`
   (no process launch, no console output needed).
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
13. **Command-line parsing test** — `CommandLineOptions.Parse` maps each argument
    (`--install`/`--silent`, `--startup`, `--minimized`, `--apply-update`, `--uninstall`/
    `--purge`, none) to the correct mode; unknown args yield exit code `2` and no side effects.
14. **Progress-mode test** — `--install` and `--apply-update` select the lightweight progress
    window (not the main window); `--silent` install runs without UI and returns the documented
    `ExitCode`.
15. **Apply-update/relaunch test** — `--apply-update <dir>` waits for the lock, repoints
    `current`, retains the previous version, and exits with the defined code; failure leaves
    `current` untouched (exit `4`).
16. **Rollback test** — a freshly-applied version that never reaches "ready" causes the next
    launch/apply to repoint `current` back to the retained previous version.
17. **Single-instance test** — a second GUI launch activates the existing instance (signals
    restore-from-tray) and exits; `--apply-update` does not take the single-instance lock.
18. **Subsystem/exit-code test** — the published exe's PE header is GUI-subsystem (no console
    window on a normal launch), and `--silent` install / `--apply-update` return the documented
    `ExitCode`, observed via a direct process-handle wait.

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
