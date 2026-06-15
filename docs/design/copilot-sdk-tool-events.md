# Surfacing GitHub Copilot SDK tool calls and results

## Problem

When an agent uses the **github-copilot** provider, `CopilotSdkChatClient` runs the
GitHub Copilot SDK, which drives the whole agentic loop inside the `copilot` CLI. Tools
(both the CLI's built-in tools and our forwarded `SessionConfig.Tools` — see
[github-copilot-provider-support.md](./github-copilot-provider-support.md)) are invoked by
the CLI, not by `FunctionInvokingChatClient` in our pipeline. Today
`GetResponseAsync`/`GetStreamingResponseAsync` only map the final `AssistantMessageEvent`
(content + reasoning) into a `ChatResponse`.

As a result tool **use and results never reach the chat history** the GUI renders. Other
providers surface tools as `FunctionCallContent` / `FunctionResultContent`, which the
SelectableTextBlock chat output renders as collapsible tool-call / tool-result expanders
(see `SelectableTextBlockChatOutputModels`). Under the Copilot provider the user only sees
the assistant's final text.

## SDK events

The Copilot session emits tool lifecycle events (subscribe with `session.On(...)`):

| Event                          | Data (`event.Data`)                                                                 |
| ------------------------------ | ----------------------------------------------------------------------------------- |
| `ToolExecutionStartEvent`      | `ToolCallId`, `ToolName`, `Arguments`, `McpServerName`, `McpToolName`, `ParentToolCallId`, `TurnId` |
| `ToolExecutionCompleteEvent`   | `ToolCallId`, `Result`, `Success`, `Error` (`Code`/`Message`), `ParentToolCallId`, `TurnId`         |

`Result` is a `ToolExecutionCompleteContent` discriminated union with `text`
(`ToolExecutionCompleteContentText.Text`), `terminal`
(`Cwd`/`ExitCode`/`Text`), and `image` variants.

## Mapping to Microsoft.Extensions.AI content

Translate the SDK events into the same content types every other provider produces, so the
existing GUI renders them with no GUI changes:

| SDK event                     | Emit                                                                                     |
| ----------------------------- | ---------------------------------------------------------------------------------------- |
| `ToolExecutionStartEvent`     | `FunctionCallContent(callId: ToolCallId, name: ToolName, arguments: <parsed Arguments>)`  |
| `ToolExecutionCompleteEvent`  | `FunctionResultContent(callId: ToolCallId, result: <text/terminal/image, or Error>)`      |

Notes:

- Use `ToolCallId` as the correlating `callId` so the result is paired with its call.
- Parse `Arguments` (JSON) into the `FunctionCallContent.Arguments` dictionary; on failure
  fall back to a single raw-string argument.
- For `Result`, prefer the `text` variant's `Text`; for `terminal`, include `ExitCode`/`Text`;
  for `image`, emit a `DataContent`. When `Success` is false, surface `Error.Message` as the
  result (or an `ErrorContent`).
- Prefer a structured value (a `JsonElement`/object) over a stringified blob for the result,
  consistent with the entity tools — see
  [the function-result conventions](./github-copilot-provider-support.md). This keeps the
  GUI's JSON pretty-printing working.

## Where to implement

In `CopilotSdkChatClient`:

- **Streaming** (`GetStreamingResponseAsync`): the method already subscribes to
  `session.On(...)` and writes `ChatResponseUpdate`s to a channel. Add `case
  ToolExecutionStartEvent` and `case ToolExecutionCompleteEvent` arms that write
  `ChatResponseUpdate`s whose `Contents` are the `FunctionCallContent` /
  `FunctionResultContent` above. They will flow into running-item history and render via the
  selectable tool expanders.
- **Non-streaming** (`GetResponseAsync`): accumulate the start/complete events seen during
  `SendAndWaitAsync` and include the resulting `FunctionCallContent` /
  `FunctionResultContent` in the returned `ChatResponse` message contents, before the final
  assistant text.

## Testing

The live SDK path needs the CLI, so factor the event→content mapping into a pure, testable
helper (mirroring `BuildSessionConfig`), e.g. `static IEnumerable<AIContent>
MapToolEvent(SessionEvent)` or `TryMapToolStart/Complete(...)`, and unit-test:

- a start event maps to a `FunctionCallContent` with the call id, name, and parsed arguments;
- a complete event maps to a `FunctionResultContent` paired by call id, with text / terminal /
  image / error results handled;
- malformed `Arguments` fall back gracefully.

The opt-in end-to-end BYOK test (`COPILOT_BYOK_E2E=1`) can additionally assert that a tool
round-trip surfaces a call+result pair.
