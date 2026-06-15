# LLM trust profile entities

`LlmTrustProfileEntity` is the persisted/user-semantic type used for authoring trust policy.

`LlmTrustProfile` is the runtime/composed type used for execution and strips user semantics such as:

- `name`
- `base-trust-profiles`

Base profiles are inherited either **restrictively** (narrowing) or **permissively** (widening)
per each `base-trust-profiles` entry's `inheritance-mode`; see `docs/design/trust-models.md`.

## Entity type definition location

The entity type JSON definition is implemented in code at:

- `Phantom.Workspaces.Data.Core/JsonSchemas/llm-trust-profile.json`

Its markdown/schema guidance is embedded at:

- `Phantom.Workspaces.Data.Core/JsonEntities/documentation/llm-trust-profile-schema.md`
