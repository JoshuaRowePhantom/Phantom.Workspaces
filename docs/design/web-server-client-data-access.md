# Web server / client data access architecture

## Purpose

Define the architecture for `IDataAccessLayer` over web transport, with validation and referential integrity enforced server-side.

## Components

1. `Phantom.Workspaces.Data.Web.Client`
   - `IDataAccessLayer` client adapter that sends DAL requests over HTTP.
2. `Phantom.Workspaces.Data.Web.Server`
   - Server-side DAL facade and request handling that executes DAL operations.
3. `Phantom.Workspaces.Web.Server`
   - Host process for server endpoints and auth integration.
4. Core validation/enforcement layers from `Phantom.Workspaces.Data.Core`
   - Schema validation.
   - Referential integrity checks.
   - Shared merge/security behavior.

## Contract requirements

1. Web DAL behavior must match offline DAL semantics for request/response contracts.
2. Server-side execution must include schema and referential integrity enforcement.
3. Client-side web DAL construction must not wrap additional local validation layers when using the web endpoint.

## Layering model

1. **Client process**
   - App code -> `WebClientDataAccessLayer` (transport adapter) -> HTTP.
2. **Server process**
   - HTTP endpoint -> DAL request mapper -> validated DAL stack -> backing storage DAL.

## Validation strategy

1. Perform canonical validation/enforcement on server pipeline.
2. Return structured errors to clients with sufficient diagnostics.
3. Keep client thin: transport concerns, auth headers, serialization, retries.

## Authentication and authorization

1. Transport-level auth (direct web auth or dev tunnel gate) identifies caller.
2. DAL-level authorization still enforced by server-side data access layers.
3. Do not trust client assertions for identity or access rights.

## Rollout plan

1. Define/lock web request and response DTOs for DAL operations.
2. Implement server endpoint pipeline with validation-enabled DAL composition.
3. Implement client DAL transport adapter.
4. Remove/avoid local validation wrapper composition when client mode is web.
5. Add parity tests comparing offline vs web DAL behavior for representative scenarios.

## Test tasks

1. Add parity tests for `Get`, `Query`, `Update`, and other supported DAL calls (offline vs web).
2. Add tests that verify schema validation failures are produced server-side with stable error payloads.
3. Add tests that verify referential integrity failures are enforced server-side in web mode.
4. Add tests that ensure web client DAL composition does not add local validation wrappers.
