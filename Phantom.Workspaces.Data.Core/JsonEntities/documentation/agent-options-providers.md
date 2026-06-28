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

**Connection:** `ApiKeyConnection` is optional. When `apiKey` is provided it is used as the GitHub token (supports `${GITHUB_TOKEN}` env-var reference). When omitted the SDK authenticates as the logged-in Copilot user.

**Example `model.id` values:** `gpt-5`, `claude-sonnet-4.5`, `claude-opus-4.5`, `gemini-2.5-pro`.

**Notable `additionalProperties` keys (in `model.options`):**
- `thinking` — enables reasoning/thinking mode. See `["documentation", "agent-options", "model-options"]`.
- `working-directory` — used as the parameter substitution placeholder for the Copilot CLI working directory.

**Working directory:** Supplied via the `workingDirectory` top-level field (static default) or the `agent-session` entity `cwd` field (runtime override). Both `CopilotClientOptions.Cwd` and `SessionConfig.WorkingDirectory` are set to the resolved value.

**Session behavior:** A single `CopilotClient` and `CopilotSession` are created lazily on first use and reused across turns. Changing the tool set or working directory after the first turn requires a new session (detected via `ComputeSessionSignature`).

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

When `model.id` is `"test"` (case-insensitive), provider dispatch is bypassed and a `TestProviderChatClient` is returned regardless of `model.provider`. Intended for unit tests only.

---

## Unimplemented providers

`openai` and `azure` are recognized provider names but throw `NotImplementedException`. Do not use them.
