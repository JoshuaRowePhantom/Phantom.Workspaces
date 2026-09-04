# Remote chat client session (topology reference)

> Master design for the `[remote-copilot-sdk]` split-executor topology (issue #1313).

## Purpose

Describe the runtime topology in which a **Phantom.Workspaces source instance** owns
the `AgentChat` router, the persistence store, and the initiating GUI, while the
**Copilot SDK chat client** (`CopilotSdkChatClient`) and its built-in tools run on a
**remote `user-computer-profile`** reached over the reverse-tunnel transport.

The topology is opt-in per session and is expressed on the manifest by resolving a
`trust-profile` whose runtime `TrustProfile.HostingWorkspacesClientInstances` names a
non-`"."` client instance.

## Roles

| Role | Location |
|---|---|
| `AgentChat` router, steering middleware, persistence writes | **Source** instance (initiating machine) |
| Persisted `agent-session` entity, chat history rows | **Source** instance |
| `CopilotSdkChatClient` + Copilot CLI process | **Remote** `user-computer-profile` |
| Copilot SDK built-in tools (shell, filesystem, ΓÇª) | **Remote** profile (self-invoked inside the CLI) |
| `workspace-gui` / `workspace-entity` tool calls | **Source** instance (`ExecutorTarget.GuiLocal`) |
| Source-targeted `agent-session` / `current-session` tool calls | **Source** instance (see resolver rule below) |
| Other `mcp` / `function` tools | **Remote** profile (`ExecutorTarget.AgentExecutor`) |

## Executor target model (as implemented)

Each tool is tagged at construction time with one of three `ExecutorTarget` values
(`Phantom.Workspaces.Llm.Core/Transport/ExecutorTarget.cs`):

| `ExecutorTarget` | Meaning |
|---|---|
| `AgentExecutor` | Executor instance E ΓÇö default for `mcp`, `function`, and any unknown tool `kind`. |
| `GuiLocal` | GUI / initiating machine G ΓÇö for `workspace-gui` and `workspace-entity`. |
| `HostingInstance` | Hosting instance H (owner of the workspace agent session) ΓÇö for `agent-session` / `workspace-agent-session`. |

`ExecutorTopology`
(`Phantom.Workspaces.Llm.Core/Transport/ExecutorTopology.cs`) maps each target to a
client-instance string. In the single-machine topology every target resolves to
`TrustProfile.LocalClientInstance` (`"."`); in a remote-hosted session
`AgentExecutorClientInstance` becomes the remote profile's client-instance id.

`ExecutorTargetResolver`
(`Phantom.Workspaces.Llm.Core/Transport/ExecutorTargetResolver.cs`) maps tool `kind`
strings to targets. Key rules that landed in #1317:

- `workspace-gui` / `workspace-entity` ΓåÆ `GuiLocal`.
- `agent-session` / `workspace-agent-session` ΓåÆ `HostingInstance`.
- `ForKindWithTargetSession(kind, sourceSessionId, targetSessionId)`: when an
  `agent-session` / `workspace-agent-session` tool call targets **the same session it
  originates from** (source id == target id), the resolver reclassifies it as
  `GuiLocal` so it runs on the initiating agent rather than crossing back to the
  hosting instance. This is the "source-targeted current-session" fast path.
- Everything else ΓÇö `mcp`, `function`, `filesystem`, `web_request`, `chat-history`,
  `github-cli-builtin-tools`, and unknown kinds ΓÇö falls through to `AgentExecutor`.

## Trust-profile selection

`TrustProfile` (`Phantom.Workspaces.Llm.Core/Trust/TrustProfile.cs`):

- `LocalClientInstance = "."` ΓÇö the source instance.
- `WildcardClientInstance = "*"` ΓÇö matches any client instance during
  `AllowsClientInstance`; used by permissive base profiles.
- `HostingWorkspacesClientInstances` ΓÇö the effective list of client instances this
  profile may run on. Populating it with a non-`"."` id opts the session into remote
  hosting.
- `DefaultExecutionTarget` ΓÇö a `JsonElement?` connection descriptor used as the
  default target when the manifest does not override it.

At session launch, the wrapping agent-manifest declares a `trust-profile` parameter.
The Launchpad UI supplies its value; the resolver looks up the `llm-trust-profile`
entity, composes it, and reads `HostingWorkspacesClientInstances` to build the
`ExecutorTopology.AgentExecutorClientInstance`. The composed `DefaultExecutionTarget`
supplies the connection descriptor used to reach that remote instance.

## `agent-session` `host-profile-entity-id`

The persisted `agent-session` entity carries `host-profile-entity-id`
(`Phantom.Workspaces.Data.Core/JsonSchemas/agent-session.json:24`) ΓÇö the entity id of
the `user-computer-profile` hosting the session. On resume, the router reads the
field to reconstruct the topology.

**Important:** `host-profile-entity-id` records *where the session was last hosted*.
It is **not** the source of truth for the current run ΓÇö see
`docs/design/session-context-tools.md` for the rule that the live host's
profile/user is used at runtime. The stored value is a hint for reconstruction; the
live host context wins.

## Persistence

Persistence (`AgentPersistenceStoreFactory` / `chat-history`) stays on the source. Only
the chat-client transport crosses to the remote instance:

1. Source router receives a user message and updates persistence.
2. Router sends the request to the remote `CopilotSdkChatClient` via the reverse
   transport listener registered in #1314.
3. Remote CLI runs the agentic loop, self-invoking its built-in tools locally.
4. Non-source-targeted `agent-session` / `current-session` tool calls, plus every
   `mcp` / `function` / `filesystem` call, execute on the remote host per the
   topology.
5. `workspace-gui` / `workspace-entity` calls and source-targeted
   `current-session` calls route back to the source over the reverse transport.
6. Streaming response deltas flow back to the source; the source router writes them
   to persistence and forwards them to the initiating GUI.

## Related design docs

- `docs/design/github-copilot-provider-support.md` ΓÇö `CopilotSdkChatClient` details.
- `docs/design/llm-trust-profile.md` ΓÇö trust-profile entities and composition.
- `docs/design/session-context-tools.md` ΓÇö the "host follows the current run"
  rule for `agent-session` / `current-session` context.
- `docs/design/reverse-tunnel-trust-execution.md` ΓÇö reverse-tunnel transport.

## Related documentation entities

- `["documentation", "agent-options", "providers"]` ΓÇö the `github-copilot` "Remote
  hosting" subsection.
- `["documentation", "agent-options", "tools"]` ΓÇö the "Execution target of tool
  kinds" table.
- `["documentation", "agent-options", "parameters"]` ΓÇö the `trust-profile`
  parameter reference.
- `["documentation", "agent-configuration"]` ΓÇö remote-hosting worked example for
  the `agent-session` entity.

## Example manifest

See `docs/examples/github-copilot-remote-chat.json` and the
`docs/examples/README.md` entry for a valid AgentDefinition that emits the split
topology (workspace-gui + workspace-entity + current-session locally; filesystem +
github-cli-builtin-tools + mcp remotely).
