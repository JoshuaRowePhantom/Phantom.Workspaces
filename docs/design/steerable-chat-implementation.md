# Steerable chat — implementation design

This document is the concrete implementation plan for the steerable-chat feature
described in [github-copilot-provider-support.md](github-copilot-provider-support.md).

## Design summary

No new interfaces are introduced. Both providers receive the `AgentInputQueueManager`
directly and use its existing `TryDequeueNextImmediateOrQueued` method:

- **`CopilotSdkChatClient`** subscribes to `AgentInputQueueManager.QueueStateChanged`
  during streaming and, when items arrive, drains and forwards them via
  `session.SendAsync(Mode = "immediate")`.
- **`ToolResultSteeringMiddleware`** wraps the inner `IChatClient` and calls
  `TryDequeueNextImmediateOrQueued` directly at each tool-result boundary, appending
  dequeued messages before forwarding to the inner client.

`AgentChat` does not change its processing loop. It passes `this.queueManager` to
`AgentFactory.CreateChatClient` at construction time; everything else is provider-internal.

---

## Middleware placement in the `IChatClient` stack

Understanding where `ToolResultSteeringMiddleware` sits is critical to its correctness.

### Stack for non-Copilot providers (e.g. `github-models`)

```
ChatClientAgent
  └─ FunctionInvocationMiddleware          ← added by framework (UseProvidedChatClientAsIs=false)
       └─ LoggingMiddleware                ← added by AgentChat.InitializeAsync when LogChat=true
            └─ ToolResultSteeringMiddleware ← wraps raw LLM client; added by AgentFactory
                 └─ raw LLM IChatClient   (e.g. OpenAI / GitHub Models client)
```

The framework's `FunctionInvocationMiddleware` is what executes `AIFunction` tool calls
and then re-calls `GetStreamingResponseAsync` with `FunctionResultContent` messages
appended. `ToolResultSteeringMiddleware` is BELOW this layer, so it sees exactly those
re-calls and can inject queued items at the right moment.

**If the middleware were placed above `FunctionInvocationMiddleware`**, it would only see
the model's initial call (which returns `FunctionCallContent`), never the tool-result
return. It would never find `FunctionResultContent` in the last message and would never
inject anything.

### Stack for Copilot (`CopilotSdkChatClient`)

```
ChatClientAgent
  └─ LoggingMiddleware                     ← if LogChat=true
       └─ CopilotSdkChatClient             ← UseProvidedChatClientAsIs=true; no FunctionInvocationMiddleware
```

`CopilotSdkChatClient` implements `ISelfInvokingToolChatClient`, so
`ResolveUseProvidedChatClientAsIs` returns `true` and the framework does NOT add
`FunctionInvocationMiddleware`. The Copilot CLI drives the tool loop itself and never
re-calls `GetStreamingResponseAsync` with tool results. Therefore
**`ToolResultSteeringMiddleware` is not used for Copilot at all** — the factory returns
the raw `CopilotSdkChatClient`, not a wrapped version. Steering for Copilot uses the
`QueueStateChanged` subscription path instead.

### How the factory ensures correct placement

The factory wraps the raw LLM client at the bottom of the chain:

```csharp
// In AgentFactory.WrapWithMiddleware:
var mw = new ToolResultSteeringMiddleware(rawLlmClient, queueManager);
return new ChatClientResult(mw, displayName);
// AgentChat.InitializeAsync may then add LoggingMiddleware on top of mw.
// ChatClientAgent adds FunctionInvocationMiddleware on top of that.
```

`AgentFactory` produces the innermost layers; the calling code and framework add outer
layers. This ordering is automatic and does not require any special coordination.

### `GetService` delegation and `ISelfInvokingToolChatClient`

`ResolveUseProvidedChatClientAsIs` is called with `resolvedClient` — which, for
non-Copilot providers, IS the `ToolResultSteeringMiddleware`. The check calls
`resolvedClient.GetService(typeof(ISelfInvokingToolChatClient))`. The middleware's
`GetService` delegates to its inner client. For non-Copilot inner clients this returns
`null`, so the check correctly resolves to `false` and the framework adds
`FunctionInvocationMiddleware` as expected.

Because `GetService` is delegated, if a future inner client implements
`ISelfInvokingToolChatClient`, wrapping it with `ToolResultSteeringMiddleware` would
cause `ResolveUseProvidedChatClientAsIs` to return `true`, suppressing the framework
middleware — and then `ToolResultSteeringMiddleware` would never see tool results.
In practice this situation means the inner client drives its own tool loop (like
Copilot), in which case `ToolResultSteeringMiddleware` should not have been applied
in the first place. The factory should guard against this:

```csharp
private static ChatClientResult WrapWithMiddleware(
    (IChatClient client, string displayName) inner,
    AgentInputQueueManager? queueManager)
{
    // Never wrap a self-invoking client with ToolResultSteeringMiddleware —
    // it handles its own tool loop and never produces FunctionResultContent calls.
    if (queueManager is null
        || inner.client is ISelfInvokingToolChatClient
        || inner.client.GetService(typeof(ISelfInvokingToolChatClient)) is not null)
    {
        return new ChatClientResult(inner.client, inner.displayName);
    }

    return new ChatClientResult(
        new ToolResultSteeringMiddleware(inner.client, queueManager),
        inner.displayName);
}
```

---

## Stack construction walkthrough

This section traces the exact sequence of calls in `AgentChat.InitializeAsync` that
build the final `IChatClient` stack, showing where each layer is added and which object
is held at each step.

### Step 1 — Factory produces the innermost layers

```csharp
// AgentChat.InitializeAsync (modified)
var chatClientResult = this.request.ClientOverride is not null
    ? new ChatClientResult(this.request.ClientOverride, this.request.DisplayNameOverride ?? string.Empty)
    : AgentFactory.CreateChatClient(
        resolvedAgentDefinition,
        this.request.AgentServices,
        queueManager: this.queueManager);   // ← new argument
```

For a `github-models` agent, `AgentFactory.CreateChatClient` calls `WrapWithMiddleware`:

```
chatClientResult.ChatClient =
    ToolResultSteeringMiddleware(
        inner: OpenAIClient(...)            ← raw LLM client
        queueManager: this.queueManager)
```

For a `github-copilot` agent, no wrapping occurs:

```
chatClientResult.ChatClient =
    CopilotSdkChatClient(
        ...
        queueManager: this.queueManager)   ← receives queue manager directly
```

### Step 2 — `ResolveUseProvidedChatClientAsIs` inspects the stack

```csharp
var resolvedClient = chatClientResult.ChatClient;
var useProvidedChatClientAsIs = ResolveUseProvidedChatClientAsIs(
    this.request.ClientOverride is not null,
    resolvedClient);
```

`ResolveUseProvidedChatClientAsIs` calls `resolvedClient.GetService(typeof(ISelfInvokingToolChatClient))`.

- **github-models**: `resolvedClient` is `ToolResultSteeringMiddleware`. Its `GetService`
  delegates to `OpenAIClient`, which returns `null`. Result: `false` — the framework
  WILL add `FunctionInvocationMiddleware`. ✓
- **github-copilot**: `resolvedClient` is `CopilotSdkChatClient`, which implements
  `ISelfInvokingToolChatClient`. Result: `true` — the framework will NOT add
  `FunctionInvocationMiddleware`. ✓

This check is performed on the factory-returned client, before any additional wrapping,
so the result is always based on the true inner client.

### Step 3 — Optional logging wrapper

```csharp
if (this.request.AgentServices?.LogChat == true)
{
    resolvedClient = resolvedClient.AsBuilder()
        .UseLogging(this.request.AgentServices.LoggerFactory)
        .Build();
}
```

`AsBuilder().UseLogging().Build()` inserts a `LoggingChatClient` (a
`DelegatingChatClient`) on top of whatever `resolvedClient` currently is. The
`DelegatingChatClient` propagates `GetService` to its inner, so the `ISelfInvokingToolChatClient`
identity can still be discovered if needed (though in practice this check is already done
in Step 2 before this wrapping).

After this step, for `github-models` with logging:

```
resolvedClient =
    LoggingChatClient(
        inner: ToolResultSteeringMiddleware(
                   inner: OpenAIClient(...)))
```

### Step 4 — `ChatClientAgent` adds `FunctionInvocationMiddleware`

```csharp
this.chatClientAgent = new ChatClientAgent(resolvedClient, this.chatOptions);
// chatOptions.UseProvidedChatClientAsIs was computed in Step 2
```

When `UseProvidedChatClientAsIs = false` (non-Copilot), `ChatClientAgent` wraps
`resolvedClient` with its own `FunctionInvocationMiddleware` internally. The final
runtime stack the agent uses becomes:

```
ChatClientAgent
  └─ FunctionInvocationMiddleware      ← framework; re-calls with FunctionResultContent
       └─ LoggingChatClient            ← if LogChat=true
            └─ ToolResultSteeringMiddleware  ← dequeues from queueManager here
                 └─ OpenAIClient(...)
```

When `UseProvidedChatClientAsIs = true` (Copilot), `ChatClientAgent` uses `resolvedClient`
directly — there is no `FunctionInvocationMiddleware`, and `ToolResultSteeringMiddleware`
is not in the stack at all:

```
ChatClientAgent
  └─ LoggingChatClient            ← if LogChat=true
       └─ CopilotSdkChatClient   ← subscribes to queueManager.QueueStateChanged during streaming
```

### Summary — what each component owns

| Component | Role | Holds `queueManager`? |
|---|---|---|
| `AgentChat` | Passes `queueManager` to factory; no steering logic | Owns it |
| `AgentFactory` | Routes `queueManager` to the right provider; wraps non-self-invoking clients | Passes through |
| `ToolResultSteeringMiddleware` | Dequeues at tool boundaries; sits below `FunctionInvocationMiddleware` | Yes |
| `CopilotSdkChatClient` | Subscribes to `QueueStateChanged` during streaming | Yes |
| `ChatClientAgent` / framework | Adds `FunctionInvocationMiddleware`; unaware of steering | No |

---


### `ChatClientResult.cs`

Named return type for `AgentFactory.CreateChatClient`. Replaces the existing anonymous
tuple `(IChatClient client, string displayName)`.

```csharp
namespace Phantom.Workspaces.Llm;

/// <summary>
/// The result of <see cref="AgentFactory.CreateChatClient"/>.
/// </summary>
public sealed record ChatClientResult(IChatClient ChatClient, string DisplayName);
```

---

### `ToolResultSteeringMiddleware.cs`

Wraps any `IChatClient`. At each tool-result-return call (when the last message contains
`FunctionResultContent`), drains the queue and appends dequeued messages before
forwarding to the inner client. No buffer is maintained — items come directly from the
`AgentInputQueueManager`.

```csharp
namespace Phantom.Workspaces.Llm;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

/// <summary>
/// An <see cref="IChatClient"/> middleware that injects pending steering input at
/// tool-result boundaries. At each model call where the last message contains
/// <see cref="FunctionResultContent"/>, any non-held items available in the
/// <see cref="AgentInputQueueManager"/> are dequeued and appended to the message
/// list before forwarding to the inner client.
/// </summary>
internal sealed class ToolResultSteeringMiddleware : IChatClient
{
    private readonly IChatClient inner;
    private readonly AgentInputQueueManager queueManager;

    public ToolResultSteeringMiddleware(IChatClient inner, AgentInputQueueManager queueManager)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(queueManager);
        this.inner = inner;
        this.queueManager = queueManager;
    }

    public ChatClientMetadata Metadata => this.inner.Metadata;

    public TService? GetService<TService>(object? key = null) where TService : class
        => this.inner.GetService<TService>(key);

    public async Task<ChatResponse> GetResponseAsync(
        IList<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return await this.inner
            .GetResponseAsync(this.InjectQueuedIfToolResult(messages), options, cancellationToken)
            .ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IList<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in this.inner
            .GetStreamingResponseAsync(this.InjectQueuedIfToolResult(messages), options, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    public void Dispose() => this.inner.Dispose();

    // If the last message carries FunctionResultContent, drain available non-held queue
    // items and append them as additional messages before the model call.
    private IList<ChatMessage> InjectQueuedIfToolResult(IList<ChatMessage> messages)
    {
        if (messages.Count == 0
            || !messages[^1].Contents.OfType<FunctionResultContent>().Any())
        {
            return messages;
        }

        List<ChatMessage>? augmented = null;
        while (this.queueManager.TryDequeueNextImmediateOrQueued(out var item))
        {
            augmented ??= new List<ChatMessage>(messages);
            foreach (var message in item.Messages ?? [])
            {
                augmented.Add(message);
            }
        }

        return augmented ?? messages;
    }
}
```

`TryDequeueNextImmediateOrQueued` already excludes `Held` queues — no extra filter is
needed. The method is CAS-based and thread-safe, so concurrent drains (if any) are safe.

---

## Modified files

### `CopilotSdkChatClient.cs`

#### Constructor — add optional queue manager

```csharp
private readonly AgentInputQueueManager? queueManager;

public CopilotSdkChatClient(
    string modelId,
    string displayName,
    string? gitHubToken,
    ILoggerFactory? loggerFactory,
    CopilotByokOptions? byokOptions = null,
    string? cliPath = null,
    AgentInputQueueManager? queueManager = null)   // ← new optional param
{
    // ... existing assignments ...
    this.queueManager = queueManager;
}
```

#### `GetStreamingResponseAsync` — subscribe to queue changes during streaming

After setting up `session.On(...)` and before calling `session.SendAsync`, register a
`QueueStateChanged` handler. When items arrive on non-held queues, drain them and call
`session.SendAsync(Mode = "immediate")`. Unsubscribe in `finally`.

```csharp
public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
    IEnumerable<ChatMessage> messages,
    ChatOptions? options = null,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    ArgumentNullException.ThrowIfNull(messages);

    var prompt = ExtractPrompt(messages);
    var session = await this.EnsureSessionAsync(options, cancellationToken).ConfigureAwait(false);

    var channel = Channel.CreateUnbounded<ChatResponseUpdate>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true,
    });

    await this.turnLock.WaitAsync(cancellationToken).ConfigureAwait(false);

    using var subscription = session.On(sessionEvent =>
    {
        // ... existing switch cases unchanged ...
    });

    // While a turn is running, forward any non-held queue items as steering input.
    // SendAsync with Mode="immediate" is safe to call concurrently with a live turn.
    void OnQueueChanged(object? sender, AgentInputQueueManager.QueueStateChangedEventArgs e)
    {
        if (e.ChangeKind != AgentInputQueueManager.QueueStateChangeKind.ItemAdded)
        {
            return;
        }

        while (this.queueManager!.TryDequeueNextImmediateOrQueued(out var item))
        {
            foreach (var message in item.Messages ?? [])
            {
                var text = string.Concat(
                    message.Contents.OfType<TextContent>().Select(c => c.Text));
                if (!string.IsNullOrWhiteSpace(text))
                {
                    // Fire-and-forget: Mode="immediate" writes to the CLI's stdin pipe
                    // and returns promptly. Errors are non-fatal for steering.
                    _ = session.SendAsync(
                        new MessageOptions { Prompt = text, Mode = "immediate" },
                        CancellationToken.None);
                }
            }
        }
    }

    if (this.queueManager is not null)
    {
        this.queueManager.QueueStateChanged += OnQueueChanged;
    }

    try
    {
        await session.SendAsync(
            new MessageOptions { Prompt = prompt },
            cancellationToken).ConfigureAwait(false);

        await foreach (var update in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }
    finally
    {
        if (this.queueManager is not null)
        {
            this.queueManager.QueueStateChanged -= OnQueueChanged;
        }
        this.turnLock.Release();
    }
}
```

**Reentrancy:** `TryDequeueFirst` fires `QueueStateChanged` with `ChangeKind.ItemRemoved`
after each dequeue. The handler checks `ChangeKind == ItemAdded` before acting, so
removals are ignored and do not re-enter the drain loop.

**Concurrency:** if multiple `ItemAdded` events arrive simultaneously, multiple handler
invocations may race to drain. This is safe: `TryDequeueNextImmediateOrQueued` is
CAS-based, so each item is dequeued exactly once. Concurrent `session.SendAsync` calls
with `Mode = "immediate"` are permitted by the SDK.

---

### `AgentFactory.cs`

#### `CreateChatClient` — add queue manager parameter

```csharp
public static ChatClientResult CreateChatClient(AgentDefinition agent)
    => CreateChatClient(agent, services: null, queueManager: null);

public static ChatClientResult CreateChatClient(
    AgentDefinition agent,
    AgentServices? services,
    AgentInputQueueManager? queueManager = null)
{
    // ... resolve model, provider as before ...

    return provider switch
    {
        "echo"           => new ChatClientResult(new EchoChatClient(), "Echo Chat Client"),
        "github-models"  => WrapWithMiddleware(CreateGitHubModelsClient(model), queueManager),
        "github-copilot" => CreateGitHubCopilotResult(model, services, queueManager),
        "ollama"         => WrapWithMiddleware(CreateOllamaClient(model, services), queueManager),
        // ...
    };
}

// Wraps the inner client with ToolResultSteeringMiddleware when a queue manager is
// provided. Never wraps self-invoking clients — they drive their own tool loop and
// GetStreamingResponseAsync is never re-called with FunctionResultContent.
private static ChatClientResult WrapWithMiddleware(
    (IChatClient client, string displayName) inner,
    AgentInputQueueManager? queueManager)
{
    if (queueManager is null
        || inner.client is ISelfInvokingToolChatClient
        || inner.client.GetService(typeof(ISelfInvokingToolChatClient)) is not null)
    {
        return new ChatClientResult(inner.client, inner.displayName);
    }

    return new ChatClientResult(
        new ToolResultSteeringMiddleware(inner.client, queueManager),
        inner.displayName);
}

private static ChatClientResult CreateGitHubCopilotResult(
    Model model,
    AgentServices? services,
    AgentInputQueueManager? queueManager)
{
    var (client, displayName) = CreateGitHubCopilotClient(model, services);
    // Re-create with the queue manager wired in.
    // (Or: CreateGitHubCopilotClient returns the CopilotSdkChatClient directly
    // and the queue manager is passed as a constructor argument.)
    if (queueManager is not null && client is CopilotSdkChatClient copilotClient)
    {
        copilotClient.SetQueueManager(queueManager); // or pass at construction
    }
    return new ChatClientResult(client, displayName);
}
```

`echo` and `test` providers do not receive a queue manager — steering is not meaningful
for deterministic/in-process clients. `WrapWithMiddleware` with `null` is a no-op.

**Alternative (preferred):** refactor `CreateGitHubCopilotClient` to accept the queue
manager directly and pass it to the `CopilotSdkChatClient` constructor, avoiding the
need for a `SetQueueManager` setter.

#### Call-site update in `AgentChat.InitializeAsync`

The single call site changes to pass the queue manager:

```csharp
// Before
var clientInfo = AgentFactory.CreateChatClient(resolvedAgentDefinition, this.request.AgentServices);
var resolvedClient = clientInfo.Item1;
this.DisplayName = ... clientInfo.Item2;

// After
var chatClientResult = AgentFactory.CreateChatClient(
    resolvedAgentDefinition,
    this.request.AgentServices,
    queueManager: this.queueManager);
var resolvedClient = chatClientResult.ChatClient;
this.DisplayName = ... chatClientResult.DisplayName;
```

No other changes to `AgentChat`. The processing loop is unmodified.

---

### `AgentChat.cs`

No changes to `RunProcessLoopAsync` or any other method. The only change is the
`InitializeAsync` call site shown above.

---

## No changes needed

- `ISteerableChatClient` — not introduced
- `InternalCreateAgentChatRequest` — no `SteerableOverride` needed
- `AgentChat.RunProcessLoopAsync` — unchanged; steering is entirely provider-internal

---

## Tests

### `ToolResultSteeringMiddlewareTests.cs` (new)

Location: `Phantom.Workspaces.Llm.Core.Tests`

Uses a `CapturingChatClient` test double that records the messages it receives, and a
real `AgentInputQueueManager` to enqueue items.

```
NoItemsInQueue_MessageListPassedUnchanged
    → Queue is empty
    → GetStreamingResponseAsync with FunctionResultContent last
    → inner receives original message list

ItemsInQueue_AppendedAfterFunctionResults
    → Enqueue a user message via queueManager
    → GetStreamingResponseAsync with FunctionResultContent last
    → inner receives original messages + the queued ChatMessage appended

MultipleItemsInQueue_AllAppendedInFifoOrder
    → Enqueue "first" and "second" via queueManager
    → GetStreamingResponseAsync with FunctionResultContent last
    → inner receives both messages in enqueue order

ItemsInQueue_NotInjected_WhenLastMessageIsNotToolResult
    → Enqueue a user message
    → GetStreamingResponseAsync ending with a plain User message (no FunctionResultContent)
    → inner receives original list unchanged; item remains in queue

HeldQueue_ItemsNotInjected
    → Create a queue with Immediacy = Held, enqueue an item on it
    → GetStreamingResponseAsync with FunctionResultContent
    → inner receives original list unchanged (TryDequeueNextImmediateOrQueued skips Held)

ItemsNotInjected_WhenQueueManagerEmpty_GetResponseAsync
    → Same as first test but via GetResponseAsync
```

---

### `AgentFactoryTests.cs` additions

```
CreateChatClient_GitHubModels_WithQueueManager_WrapsWithMiddleware
    → Call CreateChatClient with a github-models definition and a real AgentInputQueueManager
    → result.ChatClient is ToolResultSteeringMiddleware
    → (Verify via GetService<ToolResultSteeringMiddleware> or type check)

CreateChatClient_GitHubModels_WithoutQueueManager_NoMiddleware
    → Call CreateChatClient with null queueManager
    → result.ChatClient is NOT ToolResultSteeringMiddleware

CreateChatClient_Echo_NoMiddlewareRegardlessOfQueueManager
    → echo provider always returns unwrapped EchoChatClient
```

---

### `AgentChatSteeringTests.cs` (new)

Location: `Phantom.Workspaces.Llm.Core.Tests`

Uses `DeterministicTestChatClient` with a pause point and a real `AgentInputQueueManager`.

Since the `ToolResultSteeringMiddleware` uses the real `AgentInputQueueManager`, these
tests verify end-to-end steering without any mocks for the steering path itself.

```
SteeringMiddleware_InjectsQueuedMessage_AtToolResultBoundary
    Arrange:
        - A DeterministicTestChatClient that emits one ToolCallContent update followed
          by a FunctionResultContent update, then completes.
        - Wrap it with ToolResultSteeringMiddleware + AgentInputQueueManager.
        - Enqueue a user message on the queue before the run starts.
    Act:
        Consume all updates from GetStreamingResponseAsync.
    Assert:
        The CapturingChatClient (inner) received the queued message appended after
        the FunctionResultContent message on the second GetStreamingResponseAsync call.

HeldQueueItem_NotInjected_AtToolResultBoundary
    Same as above but the queue uses Immediacy = Held.
    Assert: inner never receives the held item; it remains in the queue.

NoFunctionResults_ItemsNotConsumed
    DeterministicTestChatClient that returns only text (no tool calls).
    Enqueue an item.
    Assert: item remains in the queue after the run completes.
```

---

## Implementation order

1. `ChatClientResult.cs` — new record (no deps)
2. `ToolResultSteeringMiddleware.cs` — new class
3. `ToolResultSteeringMiddlewareTests.cs` — all green independently
4. `CopilotSdkChatClient.cs` — add `queueManager` constructor param + `OnQueueChanged` handler
5. `AgentFactory.cs` — change return type to `ChatClientResult`; add `queueManager` param;
   route it to Copilot constructor and `WrapWithMiddleware`
6. `AgentFactoryTests.cs` additions — verify wrapping behaviour
7. `AgentChat.cs` — update the single `InitializeAsync` call site to pass `this.queueManager`
8. `AgentChatSteeringTests.cs` — end-to-end tests; all green before merge

Steps 1–3 can be done and merged independently. Steps 4–8 are a single coherent change.
