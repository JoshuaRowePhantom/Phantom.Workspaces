# winget packaging (future)

> **Status: not yet implemented / deferred.** We are **not** shipping a winget package in
> the current iteration. This document captures the design so it can be picked up later. The
> active build/distribution design — local self-managed install, in-app auto-update, tray
> icon, GitHub Releases — lives in `docs/design/build-and-installation.md`.

## Purpose

Describe how Phantom.Workspaces would be published to and updated through **winget** (the
Windows Package Manager) once we decide to support it. winget is **complementary** to the
in-app updater described in `build-and-installation.md`; both can coexist because winget and
the updater operate on the same managed install layout and the same GitHub Release assets.

## Prerequisites (from build-and-installation.md)

winget publishing builds on artifacts the release pipeline already produces:

- Per-architecture release assets (zip and/or installer) hosted as **GitHub Release** assets
  with stable URLs (`releases/download/<tag>/<name>`) and published `.sha256` checksums.
- A single version source (`vX.Y.Z`) shared by the assembly version, `app.manifest`, and the
  winget `PackageVersion`.
- Code signing (Authenticode/EV). Optional for portable zips (SmartScreen caveat),
  **mandatory** for an MSIX installer.

## How winget integration works

winget installs from the **community repository** `microsoft/winget-pkgs` on GitHub.
"Integrating" means publishing a manifest for our package into that repo; there is no service
to register with — distribution is a pull request of YAML manifests that point at our hosted
installer assets.

### 1. Package identifier

- Choose a stable `PackageIdentifier` of the form `Publisher.Package`, e.g.
  `Phantom.Workspaces` (publisher segment + package segment). It is permanent and
  case-preserving; reuse it for every version.

### 2. Manifest layout (multi-file, schema 1.6+)

A version lives at `manifests/<first-letter>/<Publisher>/<Package>/<Version>/` and is a set
of YAML files:

- `Phantom.Workspaces.yaml` — **version** manifest (`ManifestType: version`,
  `PackageIdentifier`, `PackageVersion`, `DefaultLocale`).
- `Phantom.Workspaces.locale.en-US.yaml` — **defaultLocale** manifest (PackageName,
  Publisher, license, short/long description, homepage, tags).
- `Phantom.Workspaces.installer.yaml` — **installer** manifest: the important one. Contains:
  - `InstallerType` (`zip`+nested `portable`, or `inno`/`wix`/`msix` per the chosen option),
  - per-architecture `Installers` entries (`Architecture: x64` / `arm64`) each with
    `InstallerUrl` (the GitHub Release asset URL) and `InstallerSha256`,
  - for portable/zip: `NestedInstallerType: portable`, `NestedInstallerFiles` listing the
    `RelativeFilePath` to `Phantom.Workspaces.exe` and a `PortableCommandAlias` (e.g.
    `phantom-workspaces`),
  - for installer types: `InstallerSwitches.Silent` so winget can install unattended,
  - `ProductCode`/`AppsAndFeaturesEntries` for upgrade detection (MSI/Inno),
  - optional `InstallerSuccessCodes`, `Scope` (user/machine), `UpgradeBehavior: install`.

### 3. Authoring and validating manifests

- Generate with **`wingetcreate`** (`winget install wingetcreate`):
  `wingetcreate new <InstallerUrl>` interrogates the installer and scaffolds the three YAML
  files, computing SHA256 automatically.
- Validate locally: `winget validate --manifest <folder>` and install-test with
  `winget install --manifest <folder>` (manifests must pass the repo's schema + automated
  smoke tests).

### 4. Submission and updates

- Submit the manifest folder as a PR to `microsoft/winget-pkgs`. Automated validation
  (sandbox install, schema, SmartScreen/installer checks) runs on the PR; a moderator merges.
- For each new release, **bump the version folder**. `wingetcreate update Phantom.Workspaces
  -u <new InstallerUrl> -v <new version> --submit` updates URLs + hashes and opens the PR
  automatically — this is the mechanism to wire into CI.

### 5. End-user experience

```
winget install Phantom.Workspaces
winget upgrade Phantom.Workspaces
winget uninstall Phantom.Workspaces
```

`winget upgrade --all` picks up new versions once the manifest is merged.

## Installer-type mapping

The chosen Windows packaging artifact maps to a winget `InstallerType`:

- **Portable zip** → `installerType: zip` with a nested `portable` installer (registers the
  exe via App Execution Alias; tracked for `winget uninstall`). Easiest first target.
- **Inno Setup / WiX MSI** → `installerType: inno` / `wix` with declared **silent** switches.
- **MSIX** → `installerType: msix` (requires signing; OS-managed install/uninstall).

All of these reference the same GitHub Release assets, so the manifest structure is the
stable contract regardless of which installer we ship.

## Release-pipeline hook (when enabled)

Add a final step to the release workflow (`.github/workflows/release.yml`):

- **winget submit** — run `wingetcreate update Phantom.Workspaces -u <urls> -v <version>
  --submit` using a `WINGET_TOKEN` (a PAT with fork/PR rights to `microsoft/winget-pkgs`) to
  open the PR automatically. The version comes from the central version source; `WINGET_TOKEN`
  is a GitHub Actions secret, never in source.

## Relationship to the in-app updater

- winget and the in-app updater are not mutually exclusive: winget installs/updates the
  managed layout, and the in-app updater repoints `current` for users who installed via the
  zip. Both consume the same GitHub Release assets + SHA256.
- MSIX (if chosen) would add a third, OS-managed/Store auto-update path.

## Test tasks (when enabled)

1. **Manifest validation** — CI runs `winget validate` against the generated manifest folder
   (schema correctness) before submission.
2. **Installer silent-switch test** — verify the installer accepts the silent switches
   declared in the winget installer manifest.
3. **Release-artifact hash test** — verify the SHA256 recorded in the winget manifest matches
   the uploaded release asset.
4. **Identifier/version consistency** — the winget `PackageVersion` matches the central
   version source and the release tag.

## macOS and Linux package managers (placeholder)

The non-Windows analogs to winget are out of scope here and will get their own design when we
ship those platforms (see the placeholders in `build-and-installation.md`):

- **macOS — Homebrew cask (placeholder).** Publish a cask (tap or homebrew-cask) pointing at
  the signed/notarized `.dmg`/`.pkg` GitHub Release asset; `brew install --cask
  phantom-workspaces`. TODO when macOS packaging is designed.
- **Linux — distro package managers (placeholder).** `.deb`/`.rpm` repositories, Flatpak
  (Flathub), and/or Snap referencing the Linux release artifacts. TODO when Linux packaging
  is designed.
