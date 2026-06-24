# Steerable chat — implementation design

This document is the concrete implementation plan for the steerable-chat feature
described in [github-copilot-provider-support.md](github-copilot-provider-support.md).
It covers every file to create or modify, the expected shape of each type, and the
tests that must pass.

---

## New files in `Phantom.Workspaces.Llm.Core`

### `ISteerableChatClient.cs`

A narrow, standalone interface. Does not extend `IChatClient`.

```csharp
namespace Phantom.Workspaces.Llm;

/// <summary>
/// Accepts in-flight steering text for an active agent run. Implementations are
/// provider-specific and do not carry any <see cref="IChatClient"/> semantics.
/// </summary>
public interface ISteerableChatClient
{
    /// <summary>
    /// Forwards <paramref name="text"/> to the running model session.
    /// The call must be safe to make concurrently with an in-progress
    /// <see cref="IChatClient.GetStreamingResponseAsync"/> on the same provider.
    /// </summary>
    Task SteerAsync(string text, CancellationToken cancellationToken = default);
}
```

---

### `ChatClientResult.cs`

The return type of `AgentFactory.CreateChatClient`. Carries the chat client, its
display name, and the optional steerable side-channel.

```csharp
namespace Phantom.Workspaces.Llm;

/// <summary>
/// The result of <see cref="AgentFactory.CreateChatClient"/>.
/// </summary>
/// <param name="ChatClient">The primary LLM client for this provider.</param>
/// <param name="DisplayName">Human-readable name shown in the UI.</param>
/// <param name="Steerable">
/// If non-<see langword="null"/>, mid-run steering is supported and
/// <see cref="AgentChat"/> will activate the steering poll task.
/// </param>
public sealed record ChatClientResult(
    IChatClient ChatClient,
    string DisplayName,
    ISteerableChatClient? Steerable);
```

---

### `ToolResultSteeringMiddleware.cs`

Implements both `IChatClient` and `ISteerableChatClient`. Used for all providers
other than `github-copilot`. The factory returns the same instance as both
`ChatClientResult.ChatClient` and `ChatClientResult.Steerable`.

```csharp
namespace Phantom.Workspaces.Llm;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

/// <summary>
/// An <see cref="IChatClient"/> middleware that implements provider-agnostic
/// steering by buffering text queued via <see cref="SteerAsync"/> and injecting it
/// as a <see cref="ChatRole.User"/> message immediately after any
/// <see cref="FunctionResultContent"/> messages on the next model call.
/// </summary>
internal sealed class ToolResultSteeringMiddleware : IChatClient, ISteerableChatClient
{
    private readonly IChatClient inner;
    private readonly ConcurrentQueue<string> pending = new();

    public ToolResultSteeringMiddleware(IChatClient inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        this.inner = inner;
    }

    /// <inheritdoc/>
    public ChatClientMetadata Metadata => this.inner.Metadata;

    /// <inheritdoc/>
    public TService? GetService<TService>(object? key = null) where TService : class
        => this.inner.GetService<TService>(key) ?? (this as TService);

    /// <inheritdoc/>
    public Task SteerAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            this.pending.Enqueue(text);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<ChatResponse> GetResponseAsync(
        IList<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        messages = this.InjectPendingIfToolResult(messages);
        return await this.inner.GetResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IList<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        messages = this.InjectPendingIfToolResult(messages);
        await foreach (var update in this.inner
            .GetStreamingResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => this.inner.Dispose();

    // Drains the pending steering buffer if the last message in the list carries
    // FunctionResultContent, appending each item as a ChatRole.User message.
    // Returns the original list unchanged when there is nothing to inject.
    private IList<ChatMessage> InjectPendingIfToolResult(IList<ChatMessage> messages)
    {
        var lastMessage = messages.Count > 0 ? messages[^1] : null;
        if (lastMessage is null
            || !lastMessage.Contents.OfType<FunctionResultContent>().Any()
            || this.pending.IsEmpty)
        {
            return messages;
        }

        var augmented = new List<ChatMessage>(messages);
        while (this.pending.TryDequeue(out var text))
        {
            augmented.Add(new ChatMessage(ChatRole.User, text));
        }
        return augmented;
    }
}
```

---

## Modified files

### `CopilotSdkChatClient.cs`

Add `ISteerableChatClient` to the class declaration and implement `SteerAsync`.
The method reads `this.copilotSession` without acquiring `turnLock`, which is safe
because `Mode = "immediate"` is explicitly designed for concurrent delivery.

```csharp
// Class declaration
public sealed class CopilotSdkChatClient
    : IChatClient, IAsyncDisposable, ISelfInvokingToolChatClient, ISteerableChatClient
```

```csharp
/// <inheritdoc/>
public async Task SteerAsync(string text, CancellationToken cancellationToken = default)
{
    var session = this.copilotSession;         // volatile-ish read; null = no active session
    if (session is null || string.IsNullOrWhiteSpace(text))
    {
        return;
    }

    await session.SendAsync(
        new MessageOptions { Prompt = text, Mode = "immediate" },
        cancellationToken).ConfigureAwait(false);
}
```

---

### `AgentFactory.cs`

#### Return-type change

Change both `CreateChatClient` overloads from returning `(IChatClient, string)` to
`ChatClientResult`. The single-arg overload becomes:

```csharp
public static ChatClientResult CreateChatClient(AgentDefinition agent)
    => CreateChatClient(agent, services: null);
```

The two-arg overload:

```csharp
public static ChatClientResult CreateChatClient(
    AgentDefinition agent,
    AgentServices? services)
{
    // ... resolve model, provider as before ...

    return provider switch
    {
        "echo"           => new ChatClientResult(new EchoChatClient(), "Echo Chat Client", null),
        "github-models"  => WrapWithMiddleware(CreateGitHubModelsClient(model)),
        "github-copilot" => CreateGitHubCopilotResult(model, services),
        "ollama"         => WrapWithMiddleware(CreateOllamaClient(model, services)),
        // ...
    };
}

private static ChatClientResult WrapWithMiddleware((IChatClient client, string displayName) inner)
{
    var mw = new ToolResultSteeringMiddleware(inner.client);
    return new ChatClientResult(mw, inner.displayName, mw);
}

private static ChatClientResult CreateGitHubCopilotResult(Model model, AgentServices? services)
{
    var (client, displayName) = CreateGitHubCopilotClient(model, services);
    // CopilotSdkChatClient implements ISteerableChatClient directly.
    return new ChatClientResult(client, displayName, (ISteerableChatClient)client);
}
```

`echo` and `test` providers return `null` for `Steerable` — steering is not meaningful
for deterministic/in-process providers. The processing loop null-checks before activating
the poll, so this is a no-op.

#### Call-site update in `AgentChat.InitializeAsync`

The single call site at line 122 changes from:

```csharp
var clientInfo = ... AgentFactory.CreateChatClient(resolvedAgentDefinition, ...);
var resolvedClient = clientInfo.Item1;
this.DisplayName = ... clientInfo.Item2;
```

to:

```csharp
var chatClientResult = ... AgentFactory.CreateChatClient(resolvedAgentDefinition, ...);
var resolvedClient = chatClientResult.ChatClient;
this.steerable = chatClientResult.Steerable;
this.DisplayName = ... chatClientResult.DisplayName;
```

---

### `InternalCreateAgentChatRequest.cs`

Add an optional `SteerableOverride` so tests can inject a fake `ISteerableChatClient`
alongside `ClientOverride`:

```csharp
/// <summary>
/// Optional steerable override for test scenarios.
/// When <see langword="null"/> and <see cref="ClientOverride"/> is set, steering
/// is disabled for the override path (same as production clients that return
/// <see langword="null"/> from the factory).
/// </summary>
public ISteerableChatClient? SteerableOverride { get; init; }
```

---

### `AgentChat.cs`

#### New field

```csharp
private ISteerableChatClient? steerable;
```

#### `InitializeAsync` — pick up steerable from factory result

See the `AgentFactory` section above for the exact diff at line 122. When
`ClientOverride` is set, use `request.SteerableOverride` (defaults to `null`).

#### `RunProcessLoopAsync` — extend the existing `WhenAny` race

The inner provider-stream loop already races `MoveNextAsync` against the interrupt token
via `WasCanceledBeforeCompletingAsync`. Steering is a third arm in that same race — no
separate task, signal, or `CancellationTokenSource` is needed.

Replace the single `WasCanceledBeforeCompletingAsync` call with an inline loop that
also watches the existing `queueStateSignal` when a steerable client is present.
`queueStateSignal` is safe to share because the outer "wait for first item" loop only
uses it when `chatMessagesToSubmit.Count == 0`, which is never true while a run is
executing.

```csharp
while (true)
{
    pendingMoveNext = providerEnumerator.MoveNextAsync().AsTask();

    // Race the provider against the interrupt and (when steering is active) against
    // new queue items.  Loop until the provider step completes or the run is cancelled;
    // drain and steer any items that arrive mid-step.
    while (!pendingMoveNext.IsCompleted)
    {
        runCancellation.Token.ThrowIfCancellationRequested();

        // Without a steerable client the behaviour is identical to the previous
        // WasCanceledBeforeCompletingAsync: race against an infinite delay that the
        // cancellation token will trigger.
        var raceTarget = this.steerable is not null
            ? queueStateSignal.WaitAsync(runCancellation.Token)
            : Task.Delay(Timeout.Infinite, runCancellation.Token);

        if (await Task.WhenAny(pendingMoveNext, raceTarget).ConfigureAwait(false)
            == pendingMoveNext)
        {
            break; // provider step finished first
        }

        if (runCancellation.Token.IsCancellationRequested)
        {
            break; // interrupt won — detected on the next ThrowIfCancellationRequested
        }

        // Queue changed while the provider was still running — drain and steer.
        while (this.queueManager.TryDequeueNextImmediateOrQueued(out var steeringItem))
        {
            this.AppendUserMessagesToHistory(steeringItem.Messages ?? []);

            foreach (var message in steeringItem.Messages ?? [])
            {
                var text = string.Concat(
                    message.Contents.OfType<TextContent>().Select(c => c.Text));
                if (!string.IsNullOrWhiteSpace(text))
                {
                    await this.steerable!.SteerAsync(text, runCancellation.Token)
                        .ConfigureAwait(false);
                }
            }
        }
    }

    if (runCancellation.Token.IsCancellationRequested)
    {
        throw new OperationCanceledException(runCancellation.Token);
    }

    var hasNext = await pendingMoveNext; // already completed
    pendingMoveNext = null;
    if (!hasNext)
    {
        break;
    }

    this.AccumulateUsage(providerEnumerator.Current);
    partialResponses.Notify(providerEnumerator.Current);
}
```

This replaces the previous pattern:
```csharp
// OLD — replaced
pendingMoveNext = providerEnumerator.MoveNextAsync().AsTask();
if (await WasCanceledBeforeCompletingAsync(pendingMoveNext, runCancellation.Token))
    throw new OperationCanceledException(runCancellation.Token);
var hasNext = await pendingMoveNext;
```

No changes are needed to the outer loop, the `finally` block, or `CleanUpRunAsync`.
`WasCanceledBeforeCompletingAsync` can be deleted if it is no longer used elsewhere.

**Note on held queues:** `TryDequeueNextImmediateOrQueued` already excludes held queues —
no extra check is needed.

---

## Tests

### `ToolResultSteeringMiddlewareTests.cs` (new)

Location: `Phantom.Workspaces.Llm.Core.Tests`

```
SteerAsync_DoesNothing_WhenNoPendingItems
    → GetStreamingResponseAsync with FunctionResultContent last message
    → inner receives original message list unchanged

SteerAsync_InjectsUserMessages_AfterFunctionResults
    → Call SteerAsync("focus on auth")
    → GetStreamingResponseAsync with a message ending in FunctionResultContent
    → inner receives original messages + ChatMessage(ChatRole.User, "focus on auth") appended

SteerAsync_InjectsMultipleItems_InOrder
    → SteerAsync("first"), SteerAsync("second")
    → GetStreamingResponseAsync with FunctionResultContent
    → inner receives two User messages in FIFO order

SteerAsync_DoesNotInject_WhenLastMessageIsNotToolResult
    → SteerAsync("focus on auth")
    → GetStreamingResponseAsync with a plain user/assistant message last
    → inner receives original list unchanged; "focus on auth" stays in pending

SteerAsync_PendingCarriesOverToNextToolResult
    → SteerAsync("deferred")
    → GetStreamingResponseAsync with no-tool-result message (not consumed)
    → GetStreamingResponseAsync with FunctionResultContent
    → inner receives "deferred" as User message on the second call

GetResponseAsync_AlsoInjectsPending
    → SteerAsync("text")
    → GetResponseAsync with FunctionResultContent
    → inner.GetResponseAsync receives injected User message

GetService_ReturnsSelf_ForISteerableChatClient
    → middleware.GetService<ISteerableChatClient>() returns the middleware itself

GetService_DelegatesToInner_ForOtherTypes
    → inner exposes a mock service; middleware.GetService<SomeType>() returns inner's value
```

Use a `CapturingChatClient` test double that records the messages it receives, so
assertions can inspect what the inner client saw.

---

### `AgentFactoryTests.cs` additions

```
CreateChatClient_GitHubModels_ReturnsChatClientResultWithMiddleware
    → provider = "github-models", real connection not required (can throw; test only the shape)
    → Actually: use a fake model definition that routes to echo, which returns null steerable
    → For this test, verify the wrapping logic via a unit-testable helper or by using
       a test definition with a real-enough model spec

CreateChatClient_Echo_ReturnsChatClientResultWithNullSteerable
    → provider = "echo"
    → result.Steerable is null
    → result.ChatClient is EchoChatClient

CreateChatClient_GitHubCopilot_ReturnsChatClientResultWithCopilotSteerable
    → provider = "github-copilot" with a dummy token
    → result.ChatClient is CopilotSdkChatClient
    → result.Steerable is the same CopilotSdkChatClient instance (cast to ISteerableChatClient)
    → (Existing test already covers the provider dispatch; this extends it)
```

---

### `AgentChatSteeringTests.cs` (new)

Location: `Phantom.Workspaces.Llm.Core.Tests`  
Uses `DeterministicTestChatClient`, `InMemoryAgentPersistenceStore`, and a `MockSteerableChatClient` test double.

```csharp
// Test double — record calls to SteerAsync
internal sealed class MockSteerableChatClient : ISteerableChatClient
{
    public List<string> Steered { get; } = [];
    public SemaphoreSlim SteeringReceived { get; } = new(0);

    public Task SteerAsync(string text, CancellationToken ct = default)
    {
        this.Steered.Add(text);
        this.SteeringReceived.Release();
        return Task.CompletedTask;
    }
}
```

#### Tests

```
NullSteerable_NoSteeringPollStarted_ItemsRemainQueued
    Arrange:
        Chat with ClientOverride = slow DeterministicTestChatClient, SteerableOverride = null.
        Run is in progress (not yet complete).
    Act:
        EnqueueUserMessage("steer me") on the default queue while run is active.
    Assert:
        MockSteerable never called (null, so poll not started).
        Message appears as the next normal turn input when run ends.

NonNullSteerable_ItemDequeuedAndSteered_DuringActiveRun
    Arrange:
        - Chat with SteerableOverride = new MockSteerableChatClient().
        - DeterministicTestChatClient that pauses mid-stream (via a ManualResetEventSlim
          held by the test; stream continues only after the test signals it).
    Act:
        1. EnqueueUserMessage("initial prompt") — starts the run.
        2. Wait until the run is active (poll IsBusy or subscribe to a running-item event).
        3. EnqueueUserMessage("steer me") on the default queue.
        4. Release the stream to complete.
    Assert:
        mock.Steered contains "steer me".
        "steer me" is in chat History as a User message (AppendUserMessagesToHistory was called).

HeldQueue_ItemNotSteered
    Arrange:
        - SteerableOverride = new MockSteerableChatClient().
        - Slow DeterministicTestChatClient.
        - A custom AgentInputQueue with Immediacy = Held.
    Act:
        EnqueueUserContents on the held queue while run is active.
    Assert:
        mock.Steered is empty (held queue excluded from TryDequeueNextImmediateOrQueued).

SteeringPoll_StopsWhenRunEnds_NextItemBecomesNormalTurn
    Arrange:
        - SteerableOverride = mock.
        - Fast DeterministicTestChatClient (completes immediately).
    Act:
        EnqueueUserMessage("first") — run completes quickly.
        EnqueueUserMessage("second") — becomes a new turn input.
    Assert:
        mock.Steered does NOT contain "second" (run ended before it was enqueued).
        "second" appears as a second turn in History.
```

---

## Implementation order

1. `ISteerableChatClient.cs` — new interface (no deps)
2. `ChatClientResult.cs` — new record (depends on `ISteerableChatClient`)
3. `ToolResultSteeringMiddleware.cs` — new class
4. `ToolResultSteeringMiddlewareTests.cs` — unit tests pass independently
5. `CopilotSdkChatClient.cs` — add `ISteerableChatClient` + `SteerAsync`
6. `AgentFactory.cs` — change return type; update all call sites (one: `AgentChat.InitializeAsync`)
7. `AgentFactoryTests.cs` additions — cover new steerable shape
8. `InternalCreateAgentChatRequest.cs` — add `SteerableOverride`
9. `AgentChat.cs` — add `steerable` field; update `InitializeAsync`; replace
   `WasCanceledBeforeCompletingAsync` call with inline steering-aware race loop
10. `AgentChatSteeringTests.cs` — integration tests; all green before merge

Steps 1–4 can be done and merged independently. Steps 5–10 are a single coherent change.
