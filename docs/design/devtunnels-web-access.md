# Dev tunnels web access architecture

## Purpose

Define how Phantom.Workspaces web access is exposed through Microsoft Dev Tunnels for development and controlled remote usage, including authentication behavior.

## Scope

1. Exposing `Phantom.Workspaces.Web.Server` endpoints over a dev tunnel.
2. Connecting `Phantom.Workspaces.Data.Web.Client` through a tunnel URL.
3. Authentication and access-control strategy for tunnel usage.

## High-level architecture

1. **Host node**
   - Runs `Phantom.Workspaces.Web.Server`.
   - Runs `devtunnel host` for the server port(s).
2. **Relay/service**
   - `*.devtunnels.ms` relay URI terminates external TLS and forwards to host.
3. **Client node**
   - Uses web client DAL against relay URI, or local `devtunnel connect` forwarding.

## Authentication model

Based on Microsoft Dev Tunnels documentation:

1. Tunnels are private by default and require authenticated identity (Microsoft account, Entra ID, or GitHub).
2. Anonymous access can be explicitly enabled but should not be default for workspace data APIs.
3. Non-interactive clients should use a tunnel access token and send:
   - `X-Tunnel-Authorization: tunnel <TOKEN>`
4. Access tokens are tunnel-scoped and time-limited; token lifecycle must be treated as short-lived credential material.

## Recommended Phantom.Workspaces design

1. **Default policy**
   - Keep tunnel private by default.
   - Require either interactive login or `X-Tunnel-Authorization` token header for non-interactive clients.

2. **Configuration model**
   - Persist tunnel endpoint and access mode in local configuration.
   - Never persist raw tokens in tracked files.
   - Prefer environment variable or OS keychain-backed secret retrieval.

3. **Client behavior**
   - Web DAL should support optional tunnel token header injection.
   - Support token rotation without process restart where possible.

4. **Server behavior**
   - Keep application auth/authorization checks independent from tunnel auth.
   - Treat tunnel auth as transport gate, not business authorization.

## Operational flows

1. **Interactive development**
   - `devtunnel user login`
   - `devtunnel host -p <web-port>`
   - Access tunnel URI in browser.

2. **Service-style client**
   - Obtain token (`devtunnel token` workflow).
   - Configure web DAL tunnel header injection.
   - Call web endpoints through tunnel URI.

## Security notes

1. Dev tunnels are preview and are not production workload infrastructure.
2. Tunnel access does not replace workspace-level authorization.
3. Anonymous tunnel mode should be opt-in and visibly warned.
4. Use least-privilege, short-lived token scopes.

## Test tasks

1. Add integration tests for web DAL over tunnel-style base URI configuration.
2. Add tests validating optional `X-Tunnel-Authorization` header injection behavior.
3. Add tests ensuring private-default access configuration is preserved in setup/config models.
4. Add tests for tunnel configuration validation failures (missing endpoint, missing token source for non-interactive mode).

## Source references

1. https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/overview
2. https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/security
3. https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/cli-commands
