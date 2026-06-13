# Installation and configuration

## Purpose

Describe user-facing installation/configuration behavior and implementation expectations for local, web, and dev tunnel access.

## User-facing setup flow

1. Build/install guidance remains in repository `README.md`.
2. On first launch, if config is missing, show setup wizard before normal main window load.
3. Wizard asks user to choose repository mode:
   - Local MongoDB container (Docker Desktop).
   - Remote MongoDB connection.
   - Remote web endpoint.
   - Remote dev tunnel endpoint to another Phantom.Workspaces host.
4. Wizard can also enable remote hosting mode for the current instance.
5. After completion, persist config and continue startup.

### Local MongoDB container install flow

For MongoDB-container mode, Workspaces runs a PowerShell installer flow with logged output:

1. Launch installer script in an elevated prompt when required.
2. Install Docker Desktop if missing (for example via `winget`) and verify Docker daemon availability.
3. Create/start required MongoDB container and data directory mapping.
4. Persist resulting runtime values into `WorkspacesConfiguration`.
5. Save installer log output for diagnostics.

## Configuration model

Config is JSON-backed and stored in default user profile location unless an explicit config path is provided.

Expected config sections:

1. Data access connection profile (local mongo, remote mongo, web, dev tunnel-web).
2. Remote-hosting settings (whether this instance exposes a web DAL endpoint).
3. Dev tunnel host configuration:
   - tunnel name/id (if persistent),
   - hosted ports/protocol metadata,
   - access mode (private by default),
   - token source (not raw token value in tracked files).
4. Visual/application settings (theme, window preferences, etc.).

Multiple local instances are supported via distinct config files.

## Settings dialog behavior

The setup wizard options are also editable later through Settings categories:

1. Entity repository.
2. Remote access / hosting.
3. Visual styles.

Settings pages edit the same underlying persisted configuration model.

## Implementation details

1. Remote access enablement starts `Phantom.Workspaces.Web.Server` hosting the web DAL.
2. Web DAL requests are handled by server-side validation-enabled DAL composition.
3. Dev tunnel integration is encapsulated in a manager service:
   - installation/discovery of `devtunnel` CLI,
   - login status checks,
   - create/start/stop/list tunnel operations.
4. Secret handling:
   - no sensitive values in repository files,
   - use environment variables or local secure storage.

## Implementation status

Implemented so far (`Phantom.Workspaces/Configuration/`):

1. `WorkspacesConfiguration` — root persisted JSON model with `DataAccess`
   (`DataAccessConnectionProfile`), `RemoteHosting` (`RemoteHostingSettings`), `DevTunnel`
   (`DevTunnelConfiguration`), and `Visual` (`VisualSettings`) sections. Includes
   `ToRepositorySource()` to project the data-access profile onto the existing
   `RepositorySource` (MongoDB container and web/dev-tunnel modes).
2. `ConfigurationPersistenceService` — async load/save with defaults, default user-profile
   path (`%APPDATA%/Phantom.Workspaces/config.json`), camelCase + string-enum JSON, and
   directory creation on save.
3. Setup wizard / settings view models (`Phantom.Workspaces/ViewModels/Configuration/`):
   `RepositoryConnectionSettingsViewModel` and `RemoteAccessSettingsViewModel` (per-mode
   validation, secret-source-only fields, anonymous-access warning), composed by
   `InstallationWizardViewModel` (`CanComplete`/`CompleteAsync`) and `SettingsDialogViewModel`
   (`CanSave`/`SaveAsync`). Covered by `InstallationWizardViewModelTests`.

Secret-safety is structural: the model stores only secret *sources* (for example
`DevTunnelTokenSource`, `AccessTokenSource`, `MongoConnectionStringSource` — environment
variable names), never raw token or connection-string values, so no tracked configuration
artifact can contain a raw secret.

Not yet implemented: the Avalonia setup-wizard / settings dialog **views** (AXAML) and their
host windows, live service reconfiguration on settings change, and the elevated Mongo installer
service wrapper (the installer script `scripts/install-mongodb-container.ps1` exists). The
wizard/settings **view models** are implemented and tested.

## New classes

1. `WorkspacesConfiguration`
   - Root persisted JSON configuration model for install/runtime settings.
2. `InstallationWizardViewModel`
   - Orchestrates first-run setup steps and validation.
3. `RepositoryConnectionSettingsViewModel`
   - Edits data repository mode and endpoint/connection settings.
4. `RemoteAccessSettingsViewModel`
   - Controls web hosting and dev tunnel runtime options.
5. `SettingsDialogViewModel`
   - Hosts category navigation and binds category viewmodels.
6. `ConfigurationPersistenceService`
   - Reads/writes configuration file and handles migration/defaults.
7. `MongoDbContainerInstallService`
   - Invokes elevated PowerShell installation script and captures installer log paths/results.

## Key integration points

1. App startup bootstrap
   - Loads `WorkspacesConfiguration`; opens installation wizard when missing/invalid.
2. DAL composition
   - Repository mode selects offline DAL or web client DAL implementation.
3. Remote hosting
   - Remote access settings start/stop `Phantom.Workspaces.Web.Server` and dev tunnel services.
4. Settings persistence
   - Any settings update flows through `ConfigurationPersistenceService` and triggers live service reconfiguration.
5. Mongo installer script integration
   - Workspaces invokes `scripts/install-mongodb-container.ps1`, relaunching elevated when needed, and surfaces script output/log path in setup UI.

## Dev tunnel user experience

1. Private tunnel is default.
2. If tunnel requires token-based non-interactive access, configuration supports adding `X-Tunnel-Authorization` header material via local secret source.
3. UI should clearly indicate when anonymous tunnel access is enabled and warn user.

## Related architecture docs

1. `docs/design/devtunnels-web-access.md`
2. `docs/design/web-server-client-data-access.md`

## Test tasks

1. Add setup wizard tests for each repository mode path (local mongo, remote mongo, web endpoint, dev tunnel endpoint). ✅ `InstallationWizardViewModelTests` (mode validation + completion).
2. Add settings round-trip tests for configuration persistence and reload behavior. ✅ `ConfigurationPersistenceServiceTests`, `InstallationWizardViewModelTests` (wizard/settings save + reload).
3. Add tests verifying remote hosting enablement starts/stops web server components correctly. (Future — requires live service host)
4. Add tests ensuring sensitive values are not written to tracked configuration artifacts. ✅ `ConfigurationPersistenceServiceTests.SaveAsync_DoesNotPersistRawSecrets`.
5. Add installer integration tests for elevated-script invocation contract and log-path/result handling. (Future)
