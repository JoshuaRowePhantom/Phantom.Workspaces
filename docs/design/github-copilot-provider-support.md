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
- Forwards the agent's function tools: `ChatOptions.Tools` (the `AIFunction`
  entries) are passed to `SessionConfig.Tools` via the testable
  `CopilotSdkChatClient.BuildSessionConfig`. The Copilot CLI runs the agentic
  tool loop itself and invokes these handlers in-process, so without this the
  session would only expose the CLI's built-in tools and the workspace
  `AIFunction`s (for example `workspaces_entity_get`) would never reach the
  model. Note: the Copilot session is created once and cached, so the tool set
  is captured from the first turn's options; changing the enabled tools later
  requires a new session.
- Approves tool execution with `PermissionHandler.ApproveAll` (covers both the
  CLI's built-in tools and our custom `custom_tool` callbacks).

`AgentChat` registers the resolved chat client as an owned async resource, so the
Copilot CLI process is disposed when the chat is disposed.

### Authentication

- A GitHub token may be supplied through the model connection's `apiKey`
  (resolved through environment-variable expansion / `gh auth token` fallback).
- When no token is supplied, the SDK falls back to the logged-in Copilot user.

## Working directory

The Copilot CLI process requires a working directory both at startup
(`CopilotClientOptions.Cwd`) and for the session it runs
(`SessionConfig.WorkingDirectory`). Both are nullable strings; when omitted the CLI
inherits the process working directory of the Phantom.Workspaces host.

### SDK fields

| Type | Field | Scope | Effect |
| --- | --- | --- | --- |
| `CopilotClientOptions` | `Cwd` | Process | Sets the OS working directory of the spawned Copilot CLI process. Fixed for the lifetime of that `CopilotClient` instance. |
| `SessionConfig` | `WorkingDirectory` | Session | Sets the session-level working directory forwarded by the CLI to its tools and context. Recreated whenever `ComputeSessionSignature` detects a change. |

Both fields must be set consistently. A mismatch (e.g., process cwd = `/a`, session
cwd = `/b`) would produce unexpected behavior in path-relative tool calls.

### Data flow

```
AgentDefinition.workingDirectory (static default, defined in agent JSON)
      │
      │ overridden by
      ▼
agent-session entity "cwd" field (runtime override, set via /cwd command or entity update)
      │
      ▼
AgentFactory.CreateGitHubCopilotClient
  ├─► CopilotSdkChatClient.workingDirectory
  │         │
  │         ├─► CopilotClientOptions.Cwd  (passed at CopilotClient construction)
  │         └─► BuildSessionConfig → SessionConfig.WorkingDirectory
  │                   (included in ComputeSessionSignature)
  └─► workingDirectory added to session signature ∴ CWD change → session recreation
```

### `AgentDefinition.workingDirectory`

A new top-level `workingDirectory` string field added to `AgentDefinition.json` (the
Llm.Core schema) declares the default CWD for the agent. It maps directly to both SDK
fields above. Example:

```json
{
  "kind": "prompt",
  "name": "my-copilot",
  "workingDirectory": "C:\\projects\\my-repo",
  "model": { "id": "claude-sonnet-4.5", "provider": "github-copilot" }
}
```

### `agent-session` entity `cwd` field

The `agent-session` workspace entity (`Phantom.Workspaces.Data.Core/JsonSchemas/agent-session.json`)
gains a `cwd` optional string field. When present it overrides `AgentDefinition.workingDirectory`
when the session's `AgentChat` is (re)created. This is the field mutated by the `/cwd`
slash command.

### `CopilotSdkChatClient` changes

- Add `string? workingDirectory` constructor parameter.
- In `EnsureSessionAsync`, pass it to `CopilotClientOptions.Cwd` when constructing the
  `CopilotClient`.
- In `BuildSessionConfig`, include it in `SessionConfig.WorkingDirectory` when non-null.
- In `ComputeSessionSignature`, include `workingDirectory` in the signature string so any
  change triggers session recreation automatically.
- In `AgentFactory.CreateGitHubCopilotClient`, extract `workingDirectory` from
  `PromptAgent.Metadata["workingDirectory"]` (or the dedicated schema field once added)
  and forward it to the client constructor.

### `/cwd` slash command

A `/cwd <path>` slash command (defined in `slash-commands.md`) provides runtime CWD
control without restarting Phantom.Workspaces. Because `CopilotClientOptions.Cwd` is
fixed at process startup, changing CWD always requires tearing down the current `AgentChat`
and reconstructing it with the new value sourced from the updated `agent-session` entity.

See `slash-commands.md` for the full command model, recreation lifecycle, and UI integration.

## Key integration points

1. `AgentFactory.CreateChatClient`
   - Explicit `github-copilot` provider dispatch returning a `CopilotSdkChatClient`.
2. `AgentDefinition` schema validation (`AgentDefinition.json`)
   - Accepts `github-copilot` as a provider value.
   - New: accepts optional `workingDirectory` string field.
3. `AgentChat` construction
   - Registers `IAsyncDisposable` chat clients (including `CopilotSdkChatClient`) as owned resources.
4. Example definitions and loader tests
   - `docs/examples/github-copilot-chat.json` plus parser/factory tests cover the provider.
5. `agent-session` entity schema
   - New: optional `cwd` field stores the runtime working-directory override.

## Known issues: Ctrl+Break and steering

### Background: the agentic-turn asymmetry

For providers such as `github-models` and `ollama`, `AgentChat` drives the tool loop
itself: each model call is a short streaming turn, and between tool calls the agent
framework can dequeue new input from the input queue. For Copilot, the Copilot CLI runs
the entire agentic loop internally. From `AgentChat`'s perspective, one Copilot "turn"
spans everything from `session.SendAsync` to `SessionIdleEvent` — potentially dozens of
tool calls and several minutes of wall-clock time. This asymmetry is the root cause of
both issues described below.

### Issue 1: Ctrl+Break does not stop the Copilot CLI

**Current behavior:**
`AgentChat.Interrupt()` cancels `runCancellation`, which propagates to
`GetStreamingResponseAsync`. The `channel.Reader.ReadAllAsync` loop throws
`OperationCanceledException`, and the method's `finally` block releases `turnLock`. The
Copilot CLI process, however, is never signaled to stop.

Consequences:
1. The CLI continues running its agentic loop in the background after the user interrupts.
2. `EnsureSessionAsync` on the next turn checks the session signature; if the signature
   is unchanged it returns the *same* `CopilotSession`. Calling `session.SendAsync` on a
   session whose CLI is still processing the previous turn produces undefined behavior
   (likely a no-op or a duplicate-request error from the CLI).
3. Even if the signature changed (e.g., tools were toggled), the old CLI run and the new
   session share the same `CopilotClient` process, so the stale run may interfere with the
   new one.

**Root cause in `CopilotSdkChatClient`:**
`CopilotSession.AbortAsync(CancellationToken)` is available in the SDK but is never called
on cancellation. The `finally` block in `GetStreamingResponseAsync` only releases
`turnLock`.

**Proposed fix:**

When the cancellation token fires during `GetStreamingResponseAsync`, call `AbortAsync` on
the live session and then invalidate the cached session so the next `EnsureSessionAsync`
creates a fresh one:

```csharp
// In GetStreamingResponseAsync finally block (or a CancellationToken.Register callback):
if (cancellationToken.IsCancellationRequested && this.copilotSession is { } sessionToAbort)
{
    // Fire-and-forget: finally block cannot await; background the abort so cleanup
    // does not block the processing loop or the interrupted-run path in AgentChat.
    _ = Task.Run(async () =>
    {
        try { await sessionToAbort.AbortAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { /* ignore */ }
    });

    // Invalidate the cached session so the next turn starts fresh.
    // Must acquire sessionInitializationLock to avoid races with EnsureSessionAsync.
    await this.sessionInitializationLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
    try
    {
        if (ReferenceEquals(this.copilotSession, sessionToAbort))
        {
            this.copilotSession = null;
            this.currentSessionSignature = null;
        }
    }
    finally
    {
        this.sessionInitializationLock.Release();
    }
}
```

Because the `finally` block in an `async IAsyncEnumerable` cannot `await`, the abort
itself should be backgrounded; the session invalidation (which only assigns null) can be
done synchronously inside a synchronous lock, or the entire abort+invalidate can be
factored into a dedicated `AbortAndInvalidateSessionAsync` method that is fire-and-forget
from the `finally` path and awaitable from tests.

**`AgentChat` side note:**
`CleanUpRunAsync` already backgrounds `providerEnumerator.DisposeAsync()`, which disposes
the async-enumerator state machine and thereby releases `turnLock`. The Ctrl+Break fix is
entirely inside `CopilotSdkChatClient`; no `AgentChat` changes are needed for this part.

---

### Issue 2: Steering text (new input typed mid-turn) is not delivered

**Current behavior:**
`AgentChat.RunProcessLoopAsync` is serial: it dequeues all pending items, starts a run,
and waits for it to complete before re-checking the queue. For Copilot, a run can last
several minutes. Text typed by the user during that run is enqueued into the default
`AgentChatQueue` (immediacy = `Queue`) and simply waits. There is no path by which a
queued item reaches the Copilot CLI while its current agentic loop is active.

Separately, setting a queue to `AgentInputQueueImmediacy.Held` prevents its items from
being dequeued even after the run completes. If any code places the default queue in
`Held` state during a Copilot run, steering text is silently retained but never forwarded,
even across multiple turns, until the held state is cleared.

**SDK support: `MessageOptions.Mode`**

The Copilot SDK `MessageOptions` has a `Mode` string property that accepts two values:

- `"enqueue"` — the default; the message is queued and processed in order.
- `"immediate"` — the message is injected into the live session without waiting for the
  current agentic loop to finish. The CLI treats it as steering input: the model receives
  the new text in-context while still processing the previous turn.

This is exactly the in-band steering mechanism needed. The problem is not missing SDK
support — it is that `CopilotSdkChatClient` holds `turnLock` for the entire duration of
`GetStreamingResponseAsync`, blocking any concurrent `SendAsync` call, including a
steering call with `Mode = "immediate"`.

**Proposed fix:**

Add a steering path to `CopilotSdkChatClient` that bypasses `turnLock`:

```csharp
/// <summary>
/// Sends a steering message to the active Copilot session using
/// <c>MessageOptions.Mode = "immediate"</c>. Safe to call while
/// <see cref="GetStreamingResponseAsync"/> is in progress; the steering
/// text arrives in-context in the ongoing streaming response.
/// Does nothing if no session is currently active.
/// </summary>
public async Task SteerAsync(string text, CancellationToken cancellationToken = default)
{
    var session = this.copilotSession;
    if (session is null || string.IsNullOrWhiteSpace(text))
    {
        return;
    }

    await session.SendAsync(
        new MessageOptions { Prompt = text, Mode = "immediate" },
        cancellationToken).ConfigureAwait(false);
}
```

No lock is needed: `session.SendAsync` with `Mode = "immediate"` is designed to be called
concurrently with an in-progress turn.

Expose this capability through a new interface so `AgentChat` can call it without a
compile-time dependency on `CopilotSdkChatClient`:

```csharp
public interface ISteerableChatClient
{
    Task SteerAsync(string text, CancellationToken cancellationToken = default);
}
```

`CopilotSdkChatClient` implements `ISteerableChatClient`. Other providers do not.

**`AgentChat` changes:**

In `RunProcessLoopAsync`, after starting a Copilot run, poll the input queue for new
items concurrently with the provider stream. When an item arrives while the run is
active and the active client implements `ISteerableChatClient`:

1. Dequeue the item.
2. Call `steerableClient.SteerAsync(item.Text)`.
3. Do **not** add the item to history separately — the steered text will appear in the
   session's event stream as a `UserMessageEvent` and flow through the existing
   `PartialResponseConflator` path.
4. If the client does **not** implement `ISteerableChatClient` (non-Copilot providers),
   leave the item in the queue; it will be dequeued normally when the current run ends.

This is analogous to the existing `WasCanceledBeforeCompletingAsync` racing pattern: add
a second `Task.WhenAny` arm that watches the `queueStateSignal` semaphore alongside the
provider read.

**Held-queue fix:**
Any code that sets the default `AgentChatQueue` to `Held` during a Copilot run should
either: (a) not hold the default queue for Copilot agents, or (b) automatically clear the
held state when the current run completes, so queued steering text is delivered on the
next turn.

---

### Summary

| Issue | Root cause | Recommended fix |
| --- | --- | --- |
| Ctrl+Break doesn't stop CLI | `session.AbortAsync` never called on cancellation | Call `AbortAsync` + invalidate session in `CopilotSdkChatClient` `finally`/cancel path |
| Stale session reused after interrupt | `copilotSession` not nulled on interrupt | Null `copilotSession` + `currentSessionSignature` when aborting (same fix as above) |
| Steering text not delivered mid-turn | `turnLock` blocks concurrent `SendAsync`; `AgentChat` loop is serial | Add `ISteerableChatClient.SteerAsync` that bypasses `turnLock`; use `Mode = "immediate"` SDK feature; `AgentChat` polls queue concurrently with active run |
| Held queue blocks steering post-turn | Held state not cleared on turn completion | Auto-clear held state when Copilot run ends, or avoid holding the default queue |



1. Factory dispatch test: `github-copilot` returns a `CopilotSdkChatClient` with the
   expected display name (with and without an explicit connection). ✅
2. Example definition loading test for `github-copilot-chat.json`. ✅
3. Schema validation accepts the provider value. ✅

## Non-goals

1. Replacing the `github-models` path.
2. Adding fallback behavior between provider types.
3. Replaying full prior history into the Copilot session on restore (the live session
   retains context across turns within a single chat lifetime).

## BYOK testing

`CopilotSdkChatClient` accepts optional `CopilotByokOptions` (and `cliPath`). When provided, the
session is configured with a Copilot SDK `ProviderConfig` (`CreateProviderConfig`) that points
the CLI at a custom OpenAI-compatible endpoint instead of GitHub's hosted models. This lets the
provider be exercised against one of our own test chat providers:

- `OpenAiCompatibleChatServer` (test helper) fronts any `IChatClient` (for example
  `EchoChatClient`) over `HttpListener` at `http://localhost:{port}/`, serving non-streaming
  chat completions and SSE streaming chunks (the CLI requests streaming).
- Deterministic tests (`CopilotByokTests`): `CreateProviderConfig` mapping, and the server
  round-tripping the echo provider over HTTP.
- An opt-in end-to-end test (`CopilotProvider_Byok_AgainstTestServer_EndToEnd`) runs a real
  Copilot CLI session against the local server; it is gated on `COPILOT_BYOK_E2E=1` (plus the
  Copilot CLI, via `COPILOT_CLI_PATH`) so it never runs in the deterministic suite. Verified
  passing locally: the CLI accepts the BYOK provider and surfaces the test server's response.
