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

Fast local debug agent (no external model dependency):

```bash
./phantom-cli --agent-schema docs/examples/echo-chat.json
```

Load an agent with chat history persistence (MongoDB included in agent definition):

```bash
./phantom-cli --agent-schema docs/examples/qwen-local-chat-with-mongodb.json
```

## Examples

### qwen-local-chat.yaml / qwen-local-chat.json

A minimal chat agent that:
- Connects to **Ollama running locally** at `http://localhost:11434`
- Uses the **Qwen 3.6 model**
- Has **no thinking enabled** (thinking is disabled at the framework level)
- Supports basic conversation with configurable temperature and output limits
- Keeps the model loaded for **15 minutes** between requests
- **No persistent history** (chat messages stored in memory only)

**Prerequisites:**
- Ollama installed and running
- Qwen 3.6 model downloaded: `ollama pull qwen-3.6`

### echo-chat.json

A minimal local debug agent that:
- Uses the built-in **Echo** provider
- Requires **no external model host** or API key
- Is ideal for testing queueing, rendering, and interaction flows quickly

### qwen-local-chat-with-mongodb.json

A chat agent with persistent MongoDB history that:
- Connects to **Ollama running locally** at `http://localhost:11434`
- Uses the **Qwen 3.6 model**
- Includes a **chat-history custom tool** configured for MongoDB
- Automatically creates and manages a **Docker MongoDB container** on first use
- Persists chat state and messages by **agent session id** through the persistence store
- Keeps the model loaded for **15 minutes** between requests

**Prerequisites:**
- Ollama installed and running
- Docker installed and running (for MongoDB container)
- Qwen 3.6 model downloaded: `ollama pull qwen-3.6`

**Usage:**

Run:
```bash
./phantom-cli --agent-schema docs/examples/qwen-local-chat-with-mongodb.json
```

Resume a persisted session explicitly by session id:
```bash
./phantom-cli --agent-schema docs/examples/qwen-local-chat-with-mongodb.json \
              --session-id abc-123-def
```

The `MongoConnectionBroker` automatically:
- Creates the MongoDB container if it doesn't exist
- Starts the container if it's not running
- Initializes the database and collections
- Verifies the connection before use

Chat messages will be persisted in MongoDB and available across sessions. Data is stored in the `./mongo-data` directory on your host machine.

### github-models-chat-with-mongodb.json

A chat agent using GitHub Models with persistent MongoDB history that:
- Connects to **GitHub Models** via the OpenAI-compatible inference API
- Uses the **GPT-4.1 Mini model**
- Includes **GitHub MCP server integration** for repository access
- Includes a **chat-history custom tool** configured for MongoDB
- Automatically creates and manages a **Docker MongoDB container** on first use
- Persists chat state and messages by **agent session id** through the persistence store

**Prerequisites:**
- GitHub token with access to GitHub Models and GitHub MCP
- Docker installed and running (for MongoDB container)
- Set `GITHUB_TOKEN` environment variable with your token

**Usage:**

Run:
```bash
./phantom-cli --agent-schema docs/examples/github-models-chat-with-mongodb.json
```

Resume a persisted session explicitly by session id:
```bash
./phantom-cli --agent-schema docs/examples/github-models-chat-with-mongodb.json \
              --session-id abc-123-def
```

This example demonstrates using a commercial cloud model (GitHub Models) with persistent local storage (MongoDB).

### github-copilot-remote-chat.json

A GitHub Copilot chat agent configured for the `[remote-copilot-sdk]` split topology:
- Uses the **`github-copilot`** provider and the Copilot SDK's `CopilotSdkChatClient`.
- Router, steering middleware, and persistence run on the **source** Phantom.Workspaces instance.
- The `CopilotSdkChatClient` and its Copilot CLI process — including the SDK built-in shell/filesystem tools — run on a **remote `user-computer-profile`** reached over the reverse-tunnel transport.
- Tools split by execution target (see `["documentation", "agent-options", "tools"]` § "Execution target of tool kinds"):
  - `workspace-gui`, `workspace-entity`, and source-targeted `current-session` calls execute on the **source** (`ExecutorTarget.GuiLocal`).
  - `filesystem`, `github-cli-builtin-tools`, and the optional `mcp` GitHub tool execute on the **remote** profile (`ExecutorTarget.AgentExecutor`).
- Selects the remote host through the `trust-profile` parameter: the resolved `llm-trust-profile` entity supplies `HostingWorkspacesClientInstances` (the remote client-instance id) and `DefaultExecutionTarget` (the connection descriptor). See `Phantom.Workspaces.Llm.Core/Trust/TrustProfile.cs`.
- Persists `host-profile-entity-id` on the resulting `agent-session` entity so the topology can be reconstructed on resume.

**Prerequisites:**
- The remote `user-computer-profile` is enrolled and reachable via the reverse-tunnel transport.
- An `llm-trust-profile` entity exists whose `HostingWorkspacesClientInstances` contains the remote profile's client-instance id.
- `GITHUB_TOKEN` with Copilot access is available on the remote host (or the remote user is signed in to the Copilot CLI).

**Parameter authoring.** The example uses `${working-directory}` and `${trust-profile}` placeholders in `model.options.additionalProperties`. The parameter *declarations* (`name`, `kind`, `required`) belong on the wrapping `agent-manifest`, not on the standalone AgentDefinition; the example carries them in a `metadata.parameters` block for LLM authoring reference. See `["documentation", "agent-configuration"]` § "Remote-hosted `agent-session`" for the manifest-side worked example and `["documentation", "agent-options", "parameters"]` for the `trust-profile` parameter reference.

Design docs: `docs/design/remote-chat-client-session.md` (master topology) and `docs/design/github-copilot-provider-support.md` § "Remote hosting".

## Chat History Configuration

Chat history is now configured directly within agent definitions as a **custom tool**:

```json
{
  "name": "chat-history",
  "kind": "chat-history",
  "options": {
    "connection": {
      "provider": "mongodb",
      "mongoProvider": "container",
      "database-name": "phantom_chat_history",
      "collection-name": "messages",
      "container-name": "phantom-mongodb",
      "data-directory": "./mongo-data",
      "host-port": 27017
    }
  }
}
```

### Chat History Provider Options

| Field | Purpose |
|-------|---------|
| `provider` | Provider type: `"mongodb"` |
| `mongoProvider` | MongoDB backend: `"container"` (Docker) or `"external"` (remote) |
| `database-name` | MongoDB database name |
| `collection-name` | MongoDB collection for storing messages |
| **Container-specific:** | |
| `container-name` | Docker container name |
| `data-directory` | Directory for MongoDB data persistence |
| `host-port` | Host port mapping (default: 27017) |
| **External-specific:** | |
| `connection-string` | MongoDB connection string (e.g., `mongodb://host:27017`) |

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
| `metadata` | Optional metadata for versioning, tags, and custom fields |

> **Authoritative options reference:** The full reference for all supported providers, connection kinds, `model.options` fields, tool kinds, and manifest parameters is available as workspace documentation entities. Retrieve the index with:
> ```json
> { "get-entity": [{ "entity-name": ["documentation", "agent-options", "overview"] }] }
> ```
> The overview lists sibling entity names for providers, model-options, tools, parameters, and connections.

## Creating Your Own

To create a new agent definition:

1. Choose a format (YAML for readability, JSON for strict validation)
2. Define the model connection (local, remote, or credentials-based)
3. Write clear instructions for the agent's behavior
4. Add tools if needed (or leave empty for chat-only)
5. Optionally add chat-history tool for persistence
6. Load with `--agent-schema <path>`

## Session restore note

Explicit restore by id is available in host code through `CreateAgentChatRequest.AgentSessionId`, and via CLI with `--session-id`.

Example connection types:
- **AnonymousConnection**: For public endpoints (no auth)
- **ApiKeyConnection**: For API keys
- **OAuthConnection**: For OAuth2 flows
- **ReferenceConnection**: For named connections in your environment
