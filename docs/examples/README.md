# Agent Definition Examples

This directory contains example AgentSchema definitions that can be loaded via the CLI.

## Usage

Load an agent definition from file:

```bash
./phantom-cli --agent-schema docs/examples/qwen-local-chat.yaml
```

Or with JSON:

```bash
./phantom-cli --agent-schema docs/examples/qwen-local-chat.json
```

## Examples

### qwen-local-chat.yaml / qwen-local-chat.json

A minimal chat agent that:
- Connects to **Ollama running locally** at `http://localhost:11434`
- Uses the **Qwen 3.6 model**
- Has **no thinking enabled** (thinking is disabled at the framework level)
- Supports basic conversation with configurable temperature and output limits

**Prerequisites:**
- Ollama installed and running
- Qwen 3.6 model downloaded: `ollama pull qwen-3.6`

## AgentSchema Format

These files follow the [Microsoft AgentSchema specification](https://microsoft.github.io/AgentSchema/). Key fields:

| Field | Purpose |
|-------|---------|
| `kind` | Agent type: `"prompt"` (LLM-based), `"workflow"` (orchestration), or `"hosted"` (container) |
| `name` | Internal identifier (kebab-case) |
| `displayName` | Human-readable name |
| `model.id` | Model name/identifier |
| `model.provider` | Provider: `"openai"`, `"ollama"`, etc. |
| `model.connection` | Authentication details (kind, endpoint, credentials) |
| `model.options` | LLM parameters: temperature, topP, maxOutputTokens, etc. |
| `instructions` | System prompt for the agent |
| `tools` | Array of available tools (FunctionTool, CustomTool, McpTool, etc.) |

## Creating Your Own

To create a new agent definition:

1. Choose a format (YAML for readability, JSON for strict validation)
2. Define the model connection (local, remote, or credentials-based)
3. Write clear instructions for the agent's behavior
4. Add tools if needed (or leave empty for chat-only)
5. Load with `--agent-schema <path>`

Example connection types:
- **AnonymousConnection**: For public endpoints (no auth)
- **ApiKeyConnection**: For API keys
- **OAuthConnection**: For OAuth2 flows
- **ReferenceConnection**: For named connections in your environment
