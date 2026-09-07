# Phantom.Workspaces

WPF application for running LLM agents and accessing user data.

## Install

### Download the latest release (recommended)

Download the newest x64 release from the
[Releases page](https://github.com/JoshuaRowePhantom/Phantom.Workspaces/releases/latest):

- `Phantom.Workspaces-<version>-win-x64.zip` (Intel/AMD 64-bit)

ARM64 is temporarily unsupported while the MXC native unit is independently validated for that
architecture. The installer and updater reject ARM64 rather than selecting an incompatible asset.

Each asset has a matching `.sha256` checksum file. Unzip and run
`Phantom.Workspaces.exe`; the first launch bootstraps a managed, auto-updatable install under
`%LOCALAPPDATA%\Phantom.Workspaces\app` (no elevation required). Runtime configuration lives
separately at `%APPDATA%\Phantom.Workspaces\config.json`.

### One-line install (PowerShell)

```powershell
irm https://raw.githubusercontent.com/JoshuaRowePhantom/Phantom.Workspaces/main/install.ps1 | iex
```

This downloads the latest release for your architecture, verifies its SHA256, and performs the
per-user managed-layout install.

### Build from source

```powershell
dotnet publish Phantom.Workspaces/Phantom.Workspaces.csproj -c Release -r win-x64
```

Install the public Rust 1.93 toolchain before building. The pinned `microsoft/mxc` source dependency
builds `Microsoft.Mxc.Sdk`, `mxc_ffi.dll`, and `plm.exe` through the normal solution build. Published
payloads keep the MXC native unit and its MIT license loose under `runtimes\win-x64\native`. MXC may
temporarily change filesystem DACLs while applying policy and is expected to restore them.

## Running tests

Use the repository test script instead of invoking `dotnet test` directly.

```powershell
.\scripts\run-tests.ps1
```

Run only fast tests (excludes tests marked `Category=SlowGit`):

```powershell
.\scripts\run-tests.ps1 -Mode fast
```

Run the complete suite including slow Git tests:

```powershell
.\scripts\run-tests.ps1 -Mode full
```

Run specific tests by name (matched against `FullyQualifiedName`):

```powershell
.\scripts\run-tests.ps1 -TestNames SchemaPopulatorTests
.\scripts\run-tests.ps1 -TestNames SchemaPopulatorTests,SchemaValidatingDataAccessLayerTests.Update_IsRejected_WhenEntityFailsValidation
```

The script writes output to `scripts\test-results.log` and omits timing information.
