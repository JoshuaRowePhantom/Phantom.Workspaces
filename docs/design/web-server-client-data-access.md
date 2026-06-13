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

## New classes

1. `WebClientDataAccessLayer`
   - HTTP-backed `IDataAccessLayer` implementation in `Phantom.Workspaces.Data.Web.Client`.
2. `WebDataAccessRequestSerializer` / `WebDataAccessResponseSerializer`
   - Canonical request/response serialization layer for DAL DTOs.
3. `WebDataAccessController` (or equivalent endpoint surface)
   - Server endpoint handler that maps HTTP payloads to DAL requests.
4. `WebServerDataAccessPipelineFactory`
   - Composes server-side DAL stack with schema + referential integrity enforcement.
5. `WebDataAccessErrorMapper`
   - Converts DAL exceptions/results to stable web error payloads.

## Implemented classes

The initial implementation favors a thin, direct surface over the speculative class list
above:

1. `WebClientDataAccessLayer` (`Phantom.Workspaces.Data.Web.Client`)
   - HTTP-backed `IDataAccessLayer`. Posts JSON DTOs to `/data/*` endpoints and deserializes
     results. Supports an optional dev tunnel access token via the `X-Tunnel-Authorization`
     header. Owns its `HttpClient` unless one is injected (testability).
2. `WebDataAccessEndpointRouteBuilderExtensions.MapWebDataAccessEndpoints`
   (`Phantom.Workspaces.Data.Web.Server`)
   - Maps `POST /data/update`, `/data/get`, `/data/query`, `/data/get-history`,
     `/data/export`, and `/data/get-changed-entities` to the registered `IDataAccessLayer`.
3. `WebServerDataAccessLayerFactory.CreateDefaultAsync`
   (`Phantom.Workspaces.Data.Web.Server`)
   - Composes the server-side validated stack:
     `MergeProcessingDataAccessLayer` -> `ReferentialIntegrityDataAccessLayer` ->
     `SchemaValidatingDataAccessLayer` -> `InMemoryDataAccessLayer`, then runs
     `SchemaPopulator` to seed schemas. This mirrors the desktop `EntityRepository`
     composition.
4. `Phantom.Workspaces.Web.Server/Program.cs`
   - Registers the server DAL singleton and calls `MapWebDataAccessEndpoints`.

JSON request/response (de)serialization uses `System.Net.Http.Json` directly rather than a
dedicated serializer class; an explicit error-mapper class was not required because failed
responses surface status code and body in the thrown `InvalidOperationException`. These can
be promoted to dedicated classes if/when error-contract requirements grow.

## Speculative / future classes

1. `WebDataAccessRequestSerializer` / `WebDataAccessResponseSerializer`
   - Only if canonical serialization needs to diverge from `System.Net.Http.Json`.
2. `WebDataAccessErrorMapper`
   - Only if a stable structured error payload contract is introduced.

## Key integration points

1. `Phantom.Workspaces.Web.Server` host startup
   - Registers web DAL endpoints and authenticated request pipeline.
2. `Phantom.Workspaces.Data.Core` validation layers
   - Reused in server DAL composition; no duplicate client-side validation wrappers in web mode.
3. GUI/settings connection mode switch
   - Chooses `WebClientDataAccessLayer` when repository mode is remote web/dev tunnel.
4. Authentication context propagation
   - Request identity propagated into DAL authorization checks on server execution path.

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

1. Add parity tests for `Get`, `Query`, `Update`, and other supported DAL calls (offline vs web). (Future)
2. Add tests that verify schema validation failures are produced server-side with stable error payloads. (Future)
3. Add tests that verify referential integrity failures are enforced server-side in web mode. (Future)
4. Add tests that ensure web client DAL composition does not add local validation wrappers. (Covered by `EntityRepository` web-mode path; web client constructs no validation wrappers.) ✅

## Implemented test coverage

1. `WebClientDataAccessLayerTests` — client posts to the expected `/data/*` routes and
   deserializes responses (using an injected `HttpClient`). ✅
2. `WebServerDataAccessLayerFactoryTests`:
   - `CreateDefaultAsync_ComposesServerValidationPipeline` verifies the Merge ->
     Referential -> Schema -> InMemory composition. ✅
   - `MapWebDataAccessEndpoints_MapsAllExpectedRoutes` verifies all six `/data/*` routes
     are mapped. ✅
