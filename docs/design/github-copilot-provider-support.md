# GitHub Copilot provider support design

## Purpose

Define the implementation work required to support a new `github-copilot` model provider in `Phantom.Workspaces.Llm.Core`, while keeping the existing `github-models` provider path explicit.

## Current state

1. Provider dispatch is centralized in `AgentFactory.CreateChatClient`.
2. `github-models` is the provider identifier for GitHub Models (OpenAI-compatible inference endpoint).
3. `github-copilot` is the provider identifier for the GitHub Copilot SDK integration.
4. The agent definition schema provider enum is validated in `AgentDefinition.json`.

## Implemented design

The GitHub Copilot SDK (`GitHub.Copilot.SDK`) controls a stateful Copilot CLI session
rather than exposing an OpenAI-compatible endpoint. To slot it into the existing
`ChatClientAgent`-based pipeline (which wraps an `IChatClient`), the integration provides
a dedicated `IChatClient` adapter, `CopilotSdkChatClient`.

### `CopilotSdkChatClient`

- Implements `IChatClient` and `IAsyncDisposable`.
- Lazily creates and starts a single `CopilotClient` and a persistent `CopilotSession`
  on first use; the Copilot session retains conversation context across turns.
- Authenticates with an explicit GitHub token when the connection supplies one
  (typically via a `${GITHUB_TOKEN}` reference); otherwise the SDK uses the logged-in
  Copilot user (`UseLoggedInUser`).
- `GetResponseAsync` forwards the latest user message via `SendAndWaitAsync` and maps the
  returned `AssistantMessageEvent` (content plus reasoning text) into a `ChatResponse`.
- `GetStreamingResponseAsync` enables session streaming, subscribes to
  `AssistantMessageDeltaEvent`/`AssistantReasoningDeltaEvent`, and yields
  `ChatResponseUpdate`s via a channel until `SessionIdleEvent` (surfacing
  `SessionErrorEvent` as an exception).
- Maps `ChatOptions.Reasoning.Effort` to the SDK `ReasoningEffort` string and
  `ChatOptions.Instructions` to the session `SystemMessage`.
- Approves tool execution with `PermissionHandler.ApproveAll`.

`AgentChat` registers the resolved chat client as an owned async resource, so the
Copilot CLI process is disposed when the chat is disposed.

### Authentication

- A GitHub token may be supplied through the model connection's `apiKey`
  (resolved through environment-variable expansion / `gh auth token` fallback).
- When no token is supplied, the SDK falls back to the logged-in Copilot user.

## Key integration points

1. `AgentFactory.CreateChatClient`
   - Explicit `github-copilot` provider dispatch returning a `CopilotSdkChatClient`.
2. `AgentDefinition` schema validation (`AgentDefinition.json`)
   - Accepts `github-copilot` as a provider value.
3. `AgentChat` construction
   - Registers `IAsyncDisposable` chat clients (including `CopilotSdkChatClient`) as owned resources.
4. Example definitions and loader tests
   - `docs/examples/github-copilot-chat.json` plus parser/factory tests cover the provider.

## Test tasks

1. Factory dispatch test: `github-copilot` returns a `CopilotSdkChatClient` with the
   expected display name (with and without an explicit connection). ✅
2. Example definition loading test for `github-copilot-chat.json`. ✅
3. Schema validation accepts the provider value. ✅

## Non-goals

1. Replacing the `github-models` path.
2. Adding fallback behavior between provider types.
3. Replaying full prior history into the Copilot session on restore (the live session
   retains context across turns within a single chat lifetime).
