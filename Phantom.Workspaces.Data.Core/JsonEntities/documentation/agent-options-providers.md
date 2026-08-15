# Agent Options — Providers

Set `model.provider` to one of the values below. The provider drives which `IChatClient` implementation is constructed by `AgentFactory.CreateChatClient`.

<!-- When adding or changing a provider, model options, or connection kinds, update ["documentation", "agent-options", "providers"] and ["documentation", "agent-options", "model-options"]. -->

See also:
- `["documentation", "agent-options", "connections"]` — connection kind details
- `["documentation", "agent-options", "model-options"]` — provider-specific `additionalProperties` keys

---

## `github-copilot`

**Client:** `CopilotSdkChatClient` (wraps the GitHub Copilot SDK).

**Self-invoking:** Yes — the Copilot CLI drives its own agentic tool loop. The framework does _not_ wrap this client with `ToolResultSteeringMiddleware`. One Copilot "turn" spans the entire agentic loop (potentially many tool calls).

**Connection:** `ApiKeyConnection` is optional. When `apiKey` is provided it is used as the GitHub token (supports `${GITHUB_TOKEN}` env-var reference). When omitted the SDK authenticates as the logged-in Copilot user. A connection `endpoint` is rejected — custom (BYOK) endpoints use the `openai` / `azure-openai` provider strings instead.

**Example `model.id` values:** `gpt-5`, `claude-sonnet-4.5`, `claude-opus-4.5`, `gemini-2.5-pro`.

**Notable `additionalProperties` keys (in `model.options`):**
- `thinking` — enables reasoning/thinking mode. See `["documentation", "agent-options", "model-options"]`.
- `working-directory` — used as the parameter substitution placeholder for the Copilot CLI working directory.

**Working directory:** Supplied via the `workingDirectory` top-level field (static default) or the `agent-session` entity `cwd` field (runtime override). Both `CopilotClientOptions.Cwd` and `SessionConfig.WorkingDirectory` are set to the resolved value.

**Session behavior:** A single `CopilotClient` and `CopilotSession` are created lazily on first use and reused across turns. Changing the tool set, working directory, or the call-time `ChatOptions.ModelId` after the first turn requires a new session (detected via `ComputeSessionSignature`).

### Remote hosting

`github-copilot` (and its BYOK cousins `openai` / `azure-openai`) support the `[remote-copilot-sdk]` topology: the router and persistence stay on the source instance while `CopilotSdkChatClient` runs on a remote `user-computer-profile` reached over the reverse-tunnel transport.

**How a session becomes remote-hosted.** The wrapping agent-manifest declares a `trust-profile` parameter (see `["documentation", "agent-options", "parameters"]`). At launch, the resolver composes the referenced `llm-trust-profile` entity and reads `TrustProfile.HostingWorkspacesClientInstances` (`Phantom.Workspaces.Llm.Core/Trust/TrustProfile.cs:137`). Any value other than `TrustProfile.LocalClientInstance` (`"."`, line 131) opts the session into remote hosting; the resolver populates `ExecutorTopology.AgentExecutorClientInstance` with that value. `TrustProfile.DefaultExecutionTarget` (line 143) supplies the connection descriptor used to reach the remote instance. `TrustProfile.WildcardClientInstance` (`"*"`, line 134) matches any client instance and is used only in permissive base profiles — it is not a valid concrete host selection.

**Persisted host record.** `AgentFactory` writes the selected host onto the resulting `agent-session` entity's `host-profile-entity-id` field (`Phantom.Workspaces.Data.Core/JsonSchemas/agent-session.json:24`). On resume the router reads that field to reconstruct the topology; per `docs/design/session-context-tools.md` the field is a hint, not the runtime source of truth — the live host context wins if the two disagree.

**What runs where.**

| Component | Source (local) | Remote user-computer-profile |
|---|---|---|
| `AgentChat` router, steering middleware, persistence | ✔ | |
| `CopilotSdkChatClient` + Copilot CLI process | | ✔ |
| Copilot SDK built-in tools (shell, filesystem) | | ✔ (SDK self-invokes) |
| `workspace-gui` / `workspace-entity` tool calls | ✔ (`ExecutorTarget.GuiLocal`) | |
| Source-targeted `agent-session` / `current-session` (target session id == source session id) | ✔ (reclassified to `GuiLocal`) | |
| Non-source-targeted `agent-session` / `current-session` | | ✔ (`ExecutorTarget.HostingInstance` → the remote host of the target session) |
| Other `mcp` / `function` / `filesystem` / `web_request` / `github-cli-builtin-tools` | | ✔ (`ExecutorTarget.AgentExecutor`) |

Split topology source: `Phantom.Workspaces.Llm.Core/Transport/ExecutorTarget.cs`, `Phantom.Workspaces.Llm.Core/Transport/ExecutorTopology.cs`, `Phantom.Workspaces.Llm.Core/Transport/ExecutorTargetResolver.cs` (including `ForKindWithTargetSession`, which reclassifies source-targeted agent-session calls as `GuiLocal`).

**BYOK variant.** `openai` and `azure-openai` follow the same rules; `CopilotByokOptions` fields (`Provider`, `BaseUrl`, `ApiKey`) come from the model connection and are honored on the remote host. See `["documentation", "agent-options", "connections"]`.

**See also:**
- Example manifest: `docs/examples/github-copilot-remote-chat.json` (registered in `docs/examples/README.md`).
- Master design: `docs/design/remote-chat-client-session.md`.
- Split topology reference: `["documentation", "agent-options", "tools"]` § "Execution target of tool kinds".
- `trust-profile` parameter: `["documentation", "agent-options", "parameters"]`.
- Worked `agent-session` example: `["documentation", "agent-configuration"]` § "Remote-hosted `agent-session`".

---

## `openai` and `azure-openai` (BYOK via the Copilot SDK)

**Client:** `CopilotSdkChatClient`, same as `github-copilot`, but pointed at a custom OpenAI-compatible endpoint (bring-your-own-key). The provider string is the sole BYOK discriminator (issue #896): `openai` maps to the Copilot runtime provider type `openai`, `azure-openai` maps to `azure`.

**Self-invoking:** Yes — same as `github-copilot`.

**Connection:** `ApiKeyConnection` required. `endpoint` (required) is the base URL of the OpenAI-compatible endpoint; `apiKey` (optional) authenticates to that endpoint (supports `${ENV_VAR}` references). No GitHub token is used in BYOK mode.

**Notable `additionalProperties` keys (in `model.options`, interpreted by `CopilotSdkChatClient.CreateProviderConfig`, not by `AgentFactory`):**
- `wireApi` — wire API the endpoint speaks (default `chat-completions`).
- `wireModel` — wire model name when it differs from `model.id`.
- `headers` — extra request headers (object of string values).
- `cliPath` — explicit path to the Copilot CLI executable.

---

## `github-models`

**Client:** OpenAI-compatible `IChatClient` via `OpenAIClient.GetChatClient`.

**Self-invoking:** No — the framework drives the tool loop.

**Connection:** `ApiKeyConnection` required. `apiKey` is the GitHub token (supports `${GITHUB_TOKEN}`). `endpoint` defaults to `https://models.github.ai/inference` when omitted.

**Example `model.id` values:** `gpt-4.1`, `gpt-4.1-mini`, `gpt-4o`, `Meta-Llama-3.1-405B-Instruct`.

---

## `ollama`

**Client:** `OllamaApiClient`.

**Self-invoking:** No.

**Connection:** `AnonymousConnection` required. `endpoint` is the Ollama base URL (e.g. `http://localhost:11434`).

**Example `model.id` values:** `qwen3:latest`, `llama3.2`, `phi4-mini`.

**Provider-specific `additionalProperties` keys:** `num_ctx` (context window size), `keep_alive` (model keep-alive duration, e.g. `"15m"`).

---

## `echo`

**Client:** `EchoChatClient` — reflects the user message back as the assistant response.

**Self-invoking:** No.

**Connection:** None required.

**Use case:** Local development and UI testing without an external model host or API key.

---

## `test` (via `model.id`)

When `model.id` is `"test"` (case-insensitive), provider dispatch is bypassed and a `TestProviderChatClient` is returned regardless of `model.provider`. Intended for unit tests only. This is the sole exception to the rule that `model.id` is a value forwarded to the chat client and never inspected to route provider selection.
