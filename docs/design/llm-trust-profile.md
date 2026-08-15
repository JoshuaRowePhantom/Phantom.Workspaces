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

## Hosting client instances

The runtime `TrustProfile` (`Phantom.Workspaces.Llm.Core/Trust/TrustProfile.cs`) exposes two
fields that drive the `[remote-copilot-sdk]` split topology described in
`docs/design/remote-chat-client-session.md`:

- `HostingWorkspacesClientInstances` (line 137) — the effective set of client
  instances this profile may run on. Entries use `TrustProfile.LocalClientInstance`
  (`"."`, line 131) for the source, `TrustProfile.WildcardClientInstance` (`"*"`,
  line 134) for permissive "any" matching, or a concrete client-instance id for a
  remote `user-computer-profile`. A non-`"."` entry is what opts a session into
  remote hosting; it becomes `ExecutorTopology.AgentExecutorClientInstance`.
- `DefaultExecutionTarget` (line 143) — a `JsonElement?` connection descriptor
  used as the default target when the manifest does not override it.

Both are composed from the persisted `TrustProfileDefinition`
(`HostingWorkspacesClientInstances` at line 99, `DefaultExecutionTarget` at line 105)
during trust-profile resolution.

See also:
- `docs/design/remote-chat-client-session.md` — master topology design.
- `["documentation", "agent-options", "parameters"]` § `trust-profile` — how the
  manifest parameter selects the profile.
- `["documentation", "agent-options", "providers"]` § "Remote hosting" — the
  provider-side view.
