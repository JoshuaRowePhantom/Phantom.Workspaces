# Agent Options — Connections

The `model.connection` field (and MCP tool `connection` field) selects the authentication strategy for the provider or MCP server. The connection object is polymorphic, discriminated by `kind`.

<!-- When adding BYOK fields or new connection kinds, update ["documentation", "agent-options", "connections"] and ["documentation", "agent-options", "providers"]. -->

---

## `ApiKeyConnection` (`kind: "key"` in JSON / `ApiKeyConnection` in C#)

Used when an API key or bearer token is required.

| Field | Type | Required | Description |
|---|---|---|---|
| `kind` | string | Yes | `"key"` |
| `apiKey` | string | Yes (for most providers) | The API key value. Supports `${ENV_VAR}` environment-variable references. Special case: `${GITHUB_TOKEN}` falls back to `gh auth token` when the env var is unset. |
| `endpoint` | string | No | Override endpoint URL. For `github-models` defaults to `https://models.github.ai/inference` when omitted. |

### Provider usage

| Provider | `apiKey` | `endpoint` |
|---|---|---|
| `github-models` | GitHub token (required) | Custom inference endpoint (optional) |
| `github-copilot` | GitHub token (optional) | Rejected — use `openai`/`azure-openai` for BYOK endpoints |
| `openai`, `azure-openai` | Endpoint API key (optional) | BYOK endpoint base URL (required) |
| MCP tools | API key for the MCP server (required when using `ApiKeyConnection`) | MCP server base URL |

### `${ENV_VAR}` resolution

When `apiKey` is a `${VAR_NAME}` reference:
1. `Environment.GetEnvironmentVariable(VAR_NAME)` is checked first.
2. For `${GITHUB_TOKEN}` specifically, if the env var is empty, `gh auth token` is invoked as a fallback.
3. If resolution fails, an `InvalidOperationException` is thrown.

---

## `AnonymousConnection` (`kind: "Anonymous"` in JSON / `AnonymousConnection` in C#)

Used when no authentication is needed.

| Field | Type | Required | Description |
|---|---|---|---|
| `kind` | string | Yes | `"Anonymous"` |
| `endpoint` | string | Yes (for ollama and MCP) | The base URL of the service. |

### Provider usage

| Provider | `endpoint` |
|---|---|
| `ollama` | Ollama base URL, e.g. `http://localhost:11434` |
| MCP tools (public/unauthenticated) | MCP server URL |

---

## MCP stdio transport (via `AnonymousConnection` or `ApiKeyConnection`)

When the connection `endpoint` uses the `stdio://` scheme, a local process is spawned instead of an HTTP connection:

```
stdio://?command=<executable>&arg=<arg1>&arg=<arg2>&cwd=<working-dir>
```

- `command` (or URI host): the executable to run.
- `arg` (repeatable): command-line arguments.
- `cwd`: working directory for the spawned process.
- `ApiKeyConnection` with `stdio://` is rejected (stdio does not support authorization headers).

---

## `CopilotByokOptions` (BYOK)

The `CopilotByokOptions` record carries the factory-resolved connection facts for bring-your-own-key mode (`openai` / `azure-openai` providers), pointing the Copilot SDK at a custom OpenAI-compatible endpoint instead of GitHub's hosted models. `AgentFactory` populates it from the provider string and the model connection only; the remaining wire knobs (`wireApi`, `wireModel`, `headers`) are `model.options.additionalProperties` keys interpreted by `CopilotSdkChatClient.CreateProviderConfig` (issue #896).

| Field | Type | Default | Description |
|---|---|---|---|
| `Provider` | string | (required) | The BYOK provider string: `openai` or `azure-openai`. Mapped to the Copilot runtime provider type (`openai` / `azure`). |
| `BaseUrl` | string | (required) | Absolute base URL of the custom endpoint (from the connection `endpoint`). |
| `ApiKey` | string? | null | API key for the custom endpoint (resolved from the connection `apiKey`). |

Note: `CopilotByokOptions` is not expressed directly in the agent JSON schema — it is derived by `AgentFactory` from the provider string and connection, or supplied programmatically via test infrastructure.
