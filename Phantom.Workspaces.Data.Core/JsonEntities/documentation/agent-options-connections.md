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
| `github-copilot` | GitHub token (optional) | Not used — Copilot SDK manages the endpoint |
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

The `CopilotByokOptions` record configures bring-your-own-key mode for the `github-copilot` provider, pointing the Copilot SDK at a custom OpenAI-compatible endpoint instead of GitHub's hosted models. This is used primarily in test scenarios.

| Field | Type | Default | Description |
|---|---|---|---|
| `BaseUrl` | string | (required) | Absolute base URL of the custom endpoint. |
| `ApiKey` | string? | null | API key for the custom endpoint. |
| `BearerToken` | string? | null | Bearer token for the custom endpoint. |
| `ProviderType` | string | `"openai"` | Provider type understood by the Copilot runtime. |
| `WireApi` | string | `"chat-completions"` | Wire API the endpoint speaks. |
| `WireModel` | string? | null | Wire model name when it differs from `model.id`. |
| `Headers` | `Dictionary<string,string>`? | null | Extra request headers. |

Note: `CopilotByokOptions` is not expressed directly in the agent JSON schema — it is supplied programmatically via test infrastructure or service configuration.
