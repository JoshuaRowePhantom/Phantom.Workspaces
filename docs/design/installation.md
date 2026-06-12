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

## Dev tunnel user experience

1. Private tunnel is default.
2. If tunnel requires token-based non-interactive access, configuration supports adding `X-Tunnel-Authorization` header material via local secret source.
3. UI should clearly indicate when anonymous tunnel access is enabled and warn user.

## Related architecture docs

1. `docs/design/devtunnels-web-access.md`
2. `docs/design/web-server-client-data-access.md`

## Test tasks

1. Add setup wizard tests for each repository mode path (local mongo, remote mongo, web endpoint, dev tunnel endpoint).
2. Add settings round-trip tests for configuration persistence and reload behavior.
3. Add tests verifying remote hosting enablement starts/stops web server components correctly.
4. Add tests ensuring sensitive values are not written to tracked configuration artifacts.
