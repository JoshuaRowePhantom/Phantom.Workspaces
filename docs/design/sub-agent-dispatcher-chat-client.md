# Sub-agent dispatcher chat client

## Overview

`SubAgentDispatcherChatClient` is an `IChatClient` implementation that acts as a
lightweight dispatcher — each incoming message is parsed for routing prefixes, and
each prefix is dispatched to a dedicated sub-agent `AgentChat` session rather than
being processed directly in a single long-running context.

The dispatcher session itself maintains a very short context: it only routes messages,
waits for sub-agent results, and echoes those results back to the user. All expensive
reasoning happens inside the dedicated sub-agent chats. This keeps the top-level
session cheap and avoids context-length degradation over long-lived orchestration
conversations.

`SubAgentDispatcherChatClient` is registered as the `agent-definition` for an
`agent-session` entity via the entity's `agent-definition-reference` field — exactly
the same way any other `IChatClient` provider is registered. The dispatcher session
entity therefore appears in the Sessions view like any other agent session; its
sub-agents appear in the Sub-agents panel beneath it.

Related prior art:
- [`subagent-toolset.md`](subagent-toolset.md) — sub-agent lifecycle, persistence, `ISubAgentTable`
- [`steerable-chat-implementation.md`](steerable-chat-implementation.md) — `ImmediateInputQueue`, steering pattern
- [`llm-session.md`](llm-session.md) — `AgentChat`, `AgentChatCompletionState`, `RunningItems`
- [`vector-search.md`](vector-search.md) — `IEmbeddingsProvider`, `DeterministicEmbeddingsProvider`

---

## Message protocol

### Prefix formats

The **entire user message** is examined as a unit. The routing prefix appears at the
very start of the message. This allows multi-line prompts and images to be routed
correctly to the target sub-agent — the full message body (everything after the prefix)
is forwarded intact, including newlines, embedded JSON, images, etc.

| Prefix | Meaning |
|---|---|
| `new: <prompt>` | Create a new sub-agent. Auto-generate an ID slug from the first few words of `<prompt>` (lowercase, hyphenated, max 5 words). Use `<prompt>` as both the sub-agent's description and its first message. |
| `new(def): <prompt>` | Create a new sub-agent using definition named `def`. Sub-agent ID is `"def-<slug>"` where slug is auto-generated from `<prompt>`. If `def` is not a known definition name, error: `"Unknown agent definition 'def'. Available: …"` |
| `new(def id): <prompt>` | Create a new sub-agent using definition named `def` with explicit ID `id`. If `def` is not a known definition name, error. |
| `<id>: <message>` | Route `<message>` to the sub-agent identified by `<id>` (exact match first; fuzzy if no exact match). |
| `: <message>` | Route `<message>` to the most recently dispatched sub-agent (the one whose last dispatch was most recent by wall-clock time). If no sub-agent has been dispatched yet, return an error: `"No sub-agent has been dispatched yet. Use new: <prompt> to create one."` |

The prefix is matched at the very start of the whole message; everything after the
prefix (on any number of lines) is the message body forwarded to the target sub-agent.

### Fuzzy routing via cosine similarity

When `<id>` does not exactly match any existing sub-agent ID, fuzzy routing applies:

1. Compute a vector embedding for the typed `<id>` string using `IEmbeddingsProvider`
   (the `DeterministicEmbeddingsProvider` instance injected at construction time).
2. Compute embeddings for every existing sub-agent's `Description` string (or use the
   cached embedding stored on `DispatchedSubAgent.DescriptionEmbedding`).
3. Score each candidate via cosine similarity:

   ```
   similarity = dot(v_query, v_candidate) / (|v_query| × |v_candidate|)
   ```

   This mirrors the `CosineSimilarity` helper in `InMemoryQueryEvaluator`.
4. Apply a **recency bias**: if the best-matching sub-agent's `LastUpdated` is more
   than `RecencyThreshold` (default: 48 hours) in the past, treat it the same as an
   ambiguous result even if the similarity score is high. Stale sub-agents should never
   be silently re-targeted.
5. **Clear winner**: if one candidate's score exceeds all others by at least
   `AmbiguityThreshold` (default: 0.05) *and* that sub-agent was updated within
   `RecencyThreshold` → route to it.
6. **Ambiguous / too close / too old**: return a disambiguation response (see
   [Disambiguation response format](#disambiguation-response-format)) and do not
   route the message.

---

## Sub-agent lifecycle

### Creation (`new:` / `new(id):`)

1. **Resolve the sub-agent definition.** Parse the `new(...)` prefix tokens to determine the definition name and sub-agent ID. Look up the named `AgentDefinitionTool` in `SubAgentDispatcherOptions.AgentDefinitionTools` (matched by `Name`). If no definition name is given (bare `new:`), use the entry named `"default"` or the first entry in the list. If a definition name is given but does not match any entry, return an error: `"Unknown agent definition '<name>'. Available: <names>."` The `AgentDefinitionTool.Definition` is the resolved `AgentDefinition` ready for `AgentChatFactory`.

2. **Derive the sub-agent entity name.** Append the sub-agent's ID as a terminal
   component to the dispatcher's entity name array:

   ```
   dispatcher name:  ["users","username","alice","agent-sessions","dispatcher-session"]
   sub-agent id:     "foo-bar"
   sub-agent name:   ["users","username","alice","agent-sessions","dispatcher-session","foo-bar"]
   ```

   See [Entity naming convention](#entity-naming-convention).

3. **Acquire the session.** Call `IRunningAgentChatTable.AcquireAsync` with:
   - `sessionId`: new `AgentSessionId(Guid.NewGuid().ToString("n"))`
   - `definition`: the resolved `AgentDefinition`
   - `entityName`: the sub-agent's `EntityName` (serialised as a path string)
   - `entityDisplayName`: the sub-agent ID
   - `entityDescription`: the `<prompt>` text (truncated if needed)

   This returns a `RunningAgentChatLease` whose `.AgentChat` is the live session.

4. **Register for output.** Subscribe to `lease.AgentChat.RunningItems.CollectionChanged`
   and `lease.AgentChat.CompletionStateChanged` to detect when the sub-agent becomes
   idle.

5. **Emit a streaming acknowledgement.** Immediately yield:
   ```
   Sending "<truncated prompt ≤40 chars>" to <id>.\n
   ```
   If the prompt is longer than 40 characters, truncate at 40 and append `...`.

6. **Send the first message.** Call:
   ```csharp
   lease.AgentChat.EnqueueUserMessage(prompt);
   ```

7. **Track the lease.** Store a `DispatchedSubAgent` record keyed by the sub-agent ID,
   and update `_mostRecentlyDispatchedId` to this sub-agent's ID.

8. **Respond to the user** with the sub-agent's ID so the user knows the auto-generated
   slug:
   ```
   Created sub-agent "foo-bar".
   ```

### Idle detection and output emission

The dispatcher's `GetStreamingResponseAsync` does not complete until all
dispatched-to sub-agents are idle (i.e. `RunningItems.Count == 0`). As each sub-agent
becomes idle:

- Capture only the **newly added `ChatMessage` items** — those added to the sub-agent's
  history starting from the message index recorded at the time the dispatch was sent
  (`DispatchedSubAgent.DispatchHistoryIndex`).
- Copy those `ChatMessage` items directly into the dispatcher's response output (not as
  a text summary, but as actual `ChatMessage` content items appended to the dispatcher's
  response). This is for archival/context purposes in the dispatcher's own history; the
  sub-agents view already shows live sub-agent progress.
- Update `DispatchedSubAgent.LastUpdated` to `DateTimeOffset.UtcNow`.

The entire `GetStreamingResponseAsync` stream completes only after all sub-agents
targeted by the current message have become idle.

### Steering messages (arriving while sub-agents are running)

When a new user message arrives while one or more sub-agents are still running:

1. Parse the message for the routing prefix (whole-message, as described above).
2. For the target sub-agent, always enqueue via the default queue:
   ```csharp
   agentChat.EnqueueUserMessage(message);
   ```
   Queue handling (immediate vs. default) is determined by the underlying agent's own
   queue policy; the dispatcher does not need to distinguish.

---

## Entity naming convention

Sub-agent entity names are hierarchical: the sub-agent's name array is the dispatcher's
name array with the sub-agent's slug ID appended as the terminal component.

| Entity | Name array |
|---|---|
| Dispatcher session | `["users","username","alice","agent-sessions","my-dispatcher"]` |
| Sub-agent `foo-bar` | `["users","username","alice","agent-sessions","my-dispatcher","foo-bar"]` |
| Sub-agent `baz-qux` | `["users","username","alice","agent-sessions","my-dispatcher","baz-qux"]` |

This convention groups all sub-agents under the dispatcher entity in the entity tree,
making namespace collisions across sibling dispatchers impossible.

Note: today's `agent-session` entity-type schema declares
`"default-name-prefixes": [["${USER}","agent-sessions"]]`. Sub-agent sessions created
by the dispatcher follow the *same* prefix convention extended by one additional path
component. The schema's `names` array for a sub-agent entity will therefore contain:

```json
[
  ["users","username","alice","agent-sessions","my-dispatcher","foo-bar"],
  ["users","id","<user-id>","agent-sessions","my-dispatcher","foo-bar"]
]
```

---

## Class design

```csharp
public sealed class SubAgentDispatcherChatClient : IChatClient
{
    /// <summary>
    /// Constructor injected dependencies:
    ///   - IRunningAgentChatTable: acquire / track sub-agent sessions
    ///   - AgentDefinitionResolver (issue #999): resolve the sub-agent definition reference
    ///   - IEmbeddingsProvider: compute embeddings for fuzzy routing (typically DeterministicEmbeddingsProvider)
    ///   - IEntityDataAccessLayer: look up existing sub-agent entities on restart
    ///   - EntityName dispatcherEntityName: the dispatcher session's own entity name
    ///   - SubAgentDispatcherOptions options: tuning knobs
    /// </summary>
    public SubAgentDispatcherChatClient(
        IRunningAgentChatTable runningAgentChatTable,
        AgentDefinitionResolver agentDefinitionResolver,
        IEmbeddingsProvider embeddingsProvider,
        IEntityDataAccessLayer entityDataAccessLayer,
        EntityName dispatcherEntityName,
        SubAgentDispatcherOptions options) { ... }

    // IChatClient
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default);

    public object? GetService(Type serviceType, object? key = null);
    public void Dispose();
}

public sealed class SubAgentDispatcherOptions
{
    /// <summary>
    /// The list of agent-definition tool entries extracted from the dispatcher's
    /// AgentDefinition or AgentManifest. Defines the available sub-agent templates.
    /// </summary>
    public required IReadOnlyList<AgentDefinitionTool> AgentDefinitionTools { get; init; }

    /// <summary>
    /// Sub-agents not updated within this window are considered stale for fuzzy routing
    /// and will trigger disambiguation instead of silent re-routing.
    /// Default: 48 hours.
    /// </summary>
    public TimeSpan RecencyThreshold { get; init; } = TimeSpan.FromHours(48);

    /// <summary>
    /// Minimum cosine-similarity delta between the best and second-best candidate for a
    /// clear-winner determination. Values closer together than this trigger disambiguation.
    /// Default: 0.05.
    /// </summary>
    public double AmbiguityThreshold { get; init; } = 0.05;
}

/// <summary>
/// Represents one "agent-definition" tool entry from the dispatcher's manifest.
/// Declares one available sub-agent template.
/// </summary>
public sealed class AgentDefinitionTool
{
    /// <summary>The definition ID, referenced as the first token in new(id) or new(id subagent-id).</summary>
    public required string Name { get; init; }
    /// <summary>Human-readable description, used in completions and /available-subagents output.</summary>
    public required string Description { get; init; }
    /// <summary>The resolved AgentDefinition (from inline definition or manifest-reference).</summary>
    public required AgentDefinition Definition { get; init; }
}

/// <summary>Tracks one dispatched sub-agent for the lifetime of the dispatcher session.</summary>
internal sealed class DispatchedSubAgent
{
    public required string Id { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<float> DescriptionEmbedding { get; init; }
    public required EntityId EntityId { get; init; }
    public required RunningAgentChatLease Lease { get; init; }
    public DateTimeOffset LastUpdated { get; set; }
    /// <summary>
    /// Index into AgentChat.History at the time the last dispatch was sent.
    /// Used to capture only newly added ChatMessage items after the sub-agent becomes idle.
    /// </summary>
    public int DispatchHistoryIndex { get; set; }
}
```

---

## Processing loop

### `GetStreamingResponseAsync` implementation

```
1. Parse routing prefix
   ─────────────────────
   Examine the entire last user ChatMessage content as a unit.
   Match the start of the message against:
     a) /^new\((?<args>[^)]+)\):\s*(?<prompt>.+)$/s   → parse args as 1–2 whitespace-separated tokens:
                                                           1 token  → CreateSubAgent(defName: token[0], id: "<defName>-<slug>", prompt)
                                                           2 tokens → CreateSubAgent(defName: token[0], id: token[1], prompt)
                                                           unknown defName in either case → yield error and return
     b) /^new:\s*(?<prompt>.+)$/s                    → CreateSubAgent(defName: default, id: GenerateSlug(prompt), prompt)
     c) /^:\s*(?<message>.+)$/s                      → RouteToMostRecent(message)
     d) /^(?<id>[^\s:]+):\s*(?<message>.+)$/s        → RouteToSubAgent(id, message)
   If the message matches no pattern: yield an error response and return.

2. Execute the instruction
   ─────────────────────────────────────────────
   For CreateSubAgent(defName, id, prompt):
     i.  Look up AgentDefinitionTool by Name == defName in SubAgentDispatcherOptions.AgentDefinitionTools.
         If defName is null/absent, use the entry named "default" or the first entry.
         If defName is not found, yield error: "Unknown agent definition '<defName>'. Available: <names>.\n"; return.
     ii. Derive sub-agent EntityName (dispatcher name + id).
     iii.Call IRunningAgentChatTable.AcquireAsync with the entity name and definition.
     iv. Register CollectionChanged handler on Lease.AgentChat.RunningItems.
     v.  yield "Sending \"<prompt truncated to 40 chars>\" to <id>.\n"
     vi. Record DispatchHistoryIndex = Lease.AgentChat.History.Count.
     vii.Enqueue prompt: Lease.AgentChat.EnqueueUserMessage(prompt).
     viii.Update _mostRecentlyDispatchedId = id.
     ix. yield "Created sub-agent \"<id>\".\n"

   For RouteToMostRecent(message):
     i.  If _mostRecentlyDispatchedId is null:
           yield "No sub-agent has been dispatched yet. Use new: <prompt> to create one.\n"; return.
     ii. Resolve to DispatchedSubAgent and proceed as RouteToSubAgent.

   For RouteToSubAgent(id, message):
     i.  Look up id in _subAgents (exact match by Id).
     ii. If no exact match → run cosine-similarity fuzzy match (see §2).
         If ambiguous → yield disambiguation response; return.
     iii.yield "Sending \"<message truncated to 40 chars>\" to <id>.\n"
     iv. Record DispatchHistoryIndex = subAgent.Lease.AgentChat.History.Count.
     v.  agentChat.EnqueueUserMessage(message).
     vi. Update _mostRecentlyDispatchedId = id.

   For unrecognised message:
     yield "Unrecognised prefix. Use \"new: ...\", \"new(<id>): ...\", \"<id>: ...\",
            or \": ...\" (route to most recent sub-agent).\n"

3. Wait for idle and stream results
   ──────────────────────────────────
   Maintain a set of sub-agent IDs that were targeted by this message.
   For each targeted sub-agent, subscribe to RunningItems.CollectionChanged.

   Use a TaskCompletionSource-per-agent pattern (mirrors WaitForConditionAsync in tests):
     - On CollectionChanged, if RunningItems.Count == 0:
         capture newly added ChatMessage items from agentChat.History starting at
         DispatchHistoryIndex.
         Append those ChatMessage items to the dispatcher's response output.
         mark that sub-agent done.
     - When all targeted sub-agents are done → complete the async enumerable.

   CancellationToken propagation: pass cancellationToken into the TCS; on cancellation
   yield a "Cancelled." update and break.
```

### Interrupt propagation

When the dispatcher's `AgentChat` receives an interrupt (i.e. the `CancellationToken` passed to `GetStreamingResponseAsync` is cancelled), the dispatcher must propagate the interrupt to all currently running sub-agents:

1. Iterate over all `DispatchedSubAgent` entries in `_subAgents` whose `Lease.AgentChat.RunningItems.Count > 0`.
2. For each active sub-agent, call the interrupt mechanism on its `AgentChat` — specifically `lease.AgentChat.Interrupt()` or the equivalent cancellation method available on `AgentChat` (look up `AgentChat.InterruptAsync` or the `CancellationToken`-based interrupt path in the `AgentChat` implementation and document the exact call here once confirmed).
3. Yield an `"Interrupted."` update and complete the async enumerable.

This ensures that cancelling the dispatcher does not leave orphaned running sub-agent sessions consuming resources.

---

## Disambiguation response format

When routing is ambiguous (no clear winner, multiple close candidates, or best match
is stale), the dispatcher emits a structured text response and does **not** route the
message. The user must resubmit with an explicit ID.

```
Ambiguous sub-agent identifier "foo". Matching agents:
  foo-bar-baz (last updated 2026-07-14 10:23 -07:00): "File a bug to discover foo bar baz…"
  foo-something (last updated 2026-07-13 09:11 -07:00): "Investigate foo something in the…"
Please resubmit with the explicit agent ID.
```

Rules:
- Show the top 3 closest-scoring candidates (or fewer if fewer exist).
- Truncate each description to ≤ 60 characters; append `…` if truncated.
- Format `LastUpdated` using `DateTimeOffset.ToLocalTime()` as `"yyyy-MM-dd HH:mm zzz"`
  (e.g. `2026-07-15 09:25 -07:00`). The timezone offset is taken from the process
  environment / OS local timezone.
- The line `"Please resubmit with the explicit agent ID."` is always the last line.

---

## Integration points

### Dispatcher entity schema

The dispatcher `agent-session` entity only needs to reference the manifest (or inline definition) that carries the `agent-definition` tool entries — no separate `sub-agent-definition` property is required:

```json
{
  "entity-types": ["entity", "agent-session"],
  "agent-session-id": "<session-guid>",
  "agent-definition-reference": ["users", "username", "alice", "agent-manifests", "my-dispatcher"]
}
```

#### Sub-agent definitions via the `agent-definition` tool type

Sub-agent definitions are declared in the dispatcher's `AgentDefinition` (or `AgentManifest`) as entries of a new custom tool kind: `"agent-definition"`. Each entry declares one available sub-agent template:

Agent-session entities reference their manifest or definition via the `agent-definition-reference` field — a JSON array of strings representing the entity-name path components (e.g. `["users", "username", "alice", "agent-manifests", "my-dispatcher"]`). Its schema in `agent-session.json` is `{ "type": "array", "minItems": 1, "items": { "type": "string" } }`. The `agent-definition` tool entries in the dispatcher manifest use the same conventions: the `manifest-reference` field uses the identical array-of-strings shape, and the inline `definition` field `$ref`s the existing `https://phantom-workspaces/schemas/agent-definition.json` document-level schema — the same content schema used by `agent-definition` entities.

```json
{
  "agent-type": "sub-agent-dispatcher",
  "tools": [
    {
      "kind": "agent-definition",
      "name": "foo",
      "description": "A specialized agent for foo tasks.",
      "definition": {
        "provider": "github-copilot",
        "model": { "id": "gpt-4o" }
      }
    },
    {
      "kind": "agent-definition",
      "name": "bar",
      "description": "A specialized agent for bar tasks.",
      "manifest-reference": ["defaults", "agent-manifests", "github-copilot"]
    }
  ]
}
```

Each `agent-definition` tool entry has:
- `name` — the definition ID referenced in `new(id)` prefix
- `description` — human-readable description, also used in completions and `/available-subagents` output
- Either an inline `definition` (`AgentDefinition`) OR a `manifest-reference` (entity name path resolved by `AgentDefinitionResolver`)

One entry may be named `"default"`. If no entry is named `"default"`, the first entry in the list is the default.

`SubAgentDispatcherChatClient` is constructed with the list of `AgentDefinitionTool` entries extracted from the manifest by `AgentFactory`.

`IChatClient` implementations do not have access to their own `AgentDefinition` at construction time. `AgentFactory.CreateChatClient()` unpacks only the individual scalar fields it needs (model ID, provider, token, connection, etc.) into each concrete client constructor — the full `AgentDefinition` object is not passed to any client. It travels instead per-turn via `ChatOptions.AdditionalProperties["agent_definition"]`. `CurrentSessionContext.AgentDefinitionReference` holds only an entity-name reference (not the resolved definition). There is therefore no mechanism by which `SubAgentDispatcherChatClient` could self-retrieve its `AgentDefinitionTool` list at construction time; the `ExtractAgentDefinitionTools` call in `AgentFactory` is the correct approach.

#### `new(...)` prefix resolution rules

| Prefix | Resolution |
|---|---|
| `new: <prompt>` | Use the default definition (entry named `"default"`, or first entry). Auto-generate slug ID from first 5 words of prompt. |
| `new(foo): <prompt>` | Use definition named `"foo"`. Sub-agent ID is `"foo-<slug>"` where slug comes from prompt. |
| `new(foo blammo): <prompt>` | Use definition named `"foo"`. Sub-agent ID is `"blammo"` (second token is explicit ID). |
| `new(baz): <prompt>` | `"baz"` is not a known definition ID → **error**: `"Unknown agent definition 'baz'. Available: foo, bar."` |
| `new(baz blammo): <prompt>` | `"baz"` is not a known definition ID → **error**: `"Unknown agent definition 'baz'. Available: foo, bar."` |

#### Sample manifest

A complete sample manifest demonstrating `foo` and `bar` sub-agent definitions:

```json
{
  "entity-types": ["entity", "agent-manifest"],
  "names": [["users", "username", "alice", "agent-manifests", "my-dispatcher"]],
  "display-name": { "default": "My Dispatcher" },
  "agent-type": "sub-agent-dispatcher",
  "tools": [
    {
      "kind": "agent-definition",
      "name": "foo",
      "description": "A specialized agent for foo tasks.",
      "definition": {
        "provider": "github-copilot",
        "model": { "id": "gpt-4o" }
      }
    },
    {
      "kind": "agent-definition",
      "name": "bar",
      "description": "A specialized agent for bar tasks.",
      "manifest-reference": ["defaults", "agent-manifests", "github-copilot"]
    }
  ]
}
```

### Schema Changes

Three schema files require updates to support `"kind": "agent-definition"` tool entries.

**`Phantom.Workspaces.Data.Core\JsonSchemas\agent-manifest.json`** — entity-level wrapper schema

Currently declares only `entity-types` and `manifest` as properties. The sub-agent-dispatcher
manifest places `agent-type` and `tools` at the entity level (rather than inside the `manifest`
sub-object), so these two properties must be added:

```json
"agent-type": {
  "type": "string",
  "description": "Discriminator for specialised agent-manifest variants. Set to \"sub-agent-dispatcher\" for dispatcher manifests."
},
"tools": {
  "type": "array",
  "description": "Tool entries used by sub-agent-dispatcher manifests in place of the manifest.template.tools path.",
  "items": {
    "anyOf": [
      { "$ref": "#/$defs/agentDefinitionTool" }
    ]
  }
}
```

With the following `$def` added to the same file:

The `entity-reference` type already exists in `core.json` (`Phantom.Workspaces.Data.Core\JsonSchemas\core.json`) as an `anyOf` union of `entity-id` (UUID string) and `entity-name` (array of ≥1 strings):

```json
"entity-reference": {
  "anyOf": [
    { "$ref": "#/$defs/entity-id" },
    { "$ref": "#/$defs/entity-name" }
  ]
}
```

The `manifest-reference` property below and the `agent-definition-reference` field in `agent-session.json` both represent entity name references. Both should use `{ "$ref": "core.json#/$defs/entity-reference" }` with a sibling `x-entity-types` annotation to restrict allowed entity types — the same pattern already used in `view.json` and `llm-trust-profile.json`. The inline `definition` property already correctly `$ref`s the existing document-level schema; no duplication is needed there.

```json
"agentDefinitionTool": {
  "type": "object",
  "description": "A named sub-agent that the dispatcher can instantiate.",
  "required": ["kind", "name", "description"],
  "additionalProperties": false,
  "properties": {
    "kind":        { "const": "agent-definition" },
    "name":        { "type": "string", "minLength": 1 },
    "description": { "type": "string", "minLength": 1 },
    "definition": {
      "$ref": "https://phantom-workspaces/schemas/agent-definition.json",
      "description": "Inline agent definition. Mutually exclusive with manifest-reference."
    },
    "manifest-reference": {
      "$ref": "core.json#/$defs/entity-reference",
      "x-entity-types": ["agent-manifest"],
      "description": "Entity reference to an existing agent-manifest entity. Mutually exclusive with definition."
    }
  }
}
```

**`Phantom.Workspaces.Llm.Core\JsonSchemas\AgentDefinition.json`** — agent definition content schema

The `tools` array currently supports `mcpTool` and generic `tool` variants. A third
`agentDefinitionTool` variant must be added to the `anyOf` list so that inline definitions
authored inside an `AgentDefinition` object can also embed sub-agent references:

```json
"tools": {
  "type": "array",
  "items": {
    "anyOf": [
      { "$ref": "#/$defs/mcpTool" },
      { "$ref": "#/$defs/tool" },
      { "$ref": "#/$defs/agentDefinitionTool" }
    ]
  }
}
```

The `agentDefinitionTool` `$def` mirrors the one above (`kind`, `name`, `description`,
`definition`, `manifest-reference`).

**`Phantom.Workspaces.Llm.Core\JsonSchemas\agent-manifest.json`** — LLM-level manifest content schema

No structural changes are required here; dispatcher manifests use the entity-level schema path
above. However, the `model.provider` enum in `AgentDefinition.json` currently lists
`"github-copilot-subagent"` but not `"sub-agent-dispatcher"` — add `"sub-agent-dispatcher"` to
that enum to formally register the new provider discriminator used by `AgentFactory`.

The `agent-session` entity that references this manifest:

```json
{
  "entity-types": ["entity", "agent-session"],
  "agent-session-id": "<session-guid>",
  "agent-definition-reference": ["users", "username", "alice", "agent-manifests", "my-dispatcher"]
}
```

The `AgentFactory` provider switch that instantiates `SubAgentDispatcherChatClient`
requires a new provider discriminator value — e.g. `"provider": "sub-agent-dispatcher"`
— so the factory knows to construct a `SubAgentDispatcherChatClient` rather than a
plain LLM client.

### DI / `AgentFactory` instantiation

```csharp
// In AgentFactory.CreateChatClient, new branch:
case "sub-agent-dispatcher":
    var dispatcherEntityName = services.GetRequiredService<CurrentSessionContext>()
                                       .AgentDefinitionReference!.Value;
    var agentDefinitionTools = ExtractAgentDefinitionTools(agent, services);
    var options = new SubAgentDispatcherOptions
    {
        AgentDefinitionTools = agentDefinitionTools,
    };
    return new ChatClientResult(
        new SubAgentDispatcherChatClient(
            services.GetRequiredService<IRunningAgentChatTable>(),
            services.GetRequiredService<AgentDefinitionResolver>(),       // issue #999
            services.GetRequiredService<IEmbeddingsProvider>(),
            services.GetRequiredService<IEntityDataAccessLayer>(),
            dispatcherEntityName,
            options),
        displayName: "Sub-agent dispatcher");
```

`IEmbeddingsProvider` is already registered as `DeterministicEmbeddingsProvider` in
the `InMemoryDataAccessLayer` and `MongoDbEntityDataAccessLayer` DI setups; the same
registration is available to `AgentFactory` via `AgentServices`.

---

## Resolved design decisions

1. **Sub-agent persistence.** Sub-agents are **persistent entities** — they survive
   process restart and are stored in the entity database under the dispatcher's namespace.
   On restart, the dispatcher reconstructs its `_subAgents` dictionary by querying the
   DAL for all entities whose `EntityName` is a child of the dispatcher's name prefix.

2. **Top-level sessions view.** Sub-agents must **not** appear in the top-level Sessions
   view.

   **Codebase findings:** `parent-agent` does **not** currently exist as a persisted field
   anywhere. `AgentChat.ParentAgent` (`Phantom.Workspaces.Llm.Core\AgentChat.cs:379`) is
   an in-memory-only reference set when a child `AgentChat` is spawned
   (`childChat.parentAgent = this;` at line 866); it is not written to any entity JSON.
   The `agent-session` entity schema
   (`Phantom.Workspaces.Data.Core\JsonSchemas\agent-session.json`) contains no parent
   reference field, and the sessions view
   (`Phantom.Workspaces.Data.Core\JsonEntities\views\sessions-view.json`) lists all
   `agent-session` entities without filtering.

   The following must be added as part of this work:

   - **`Phantom.Workspaces.Data.Core\JsonSchemas\agent-session.json`** — add an optional
     `parent-agent-session-ids` array property (array of `entity-reference` with
     `x-entity-types: ["agent-session"]`) that stores entity references to the parent
     agent sessions; used to filter sub-agents from the top-level sessions view.

   - **`sessions-view.json`** — add a filter clause to its query to exclude `agent-session`
     entities where `parent-agent-session-ids` is non-empty. This keeps sub-agent sessions
     out of the top-level sessions list without ViewModel changes.

   - **`SubAgentDispatcherChatClient`** / sub-agent creation code — when persisting a new
     sub-agent entity, set `parent-agent-session-ids` to a one-element array containing
     an entity reference to the dispatcher's own agent-session entity.

3. **Concurrent sub-agents.** Unbounded — no throttling limit is imposed by the
   dispatcher.

4. **Final turn output.** When a sub-agent becomes idle after a dispatch:
   - Do **not** re-emit the entire sub-agent history.
   - Capture only the **newly added `ChatMessage` items** — those added to the sub-agent's
     history starting from `DispatchedSubAgent.DispatchHistoryIndex` (recorded when the
     dispatch was sent).
   - Copy those `ChatMessage` items directly into the dispatcher's response output (not
     as a text summary, but as actual `ChatMessage` content items appended to the
     dispatcher's response).
   - The sub-agents view on the main agent already shows live sub-agent progress; the
     final copy is for archival/context purposes in the dispatcher's own history.

---

## Default manifest entity

A default `agent-manifest` entity ships with the product so users can create a
Sub-Agent Dispatcher session without authoring any entity configuration themselves.

**File:** `Phantom.Workspaces.Data.Core/JsonEntities/defaults/agent-manifests/sub-agent-dispatcher-manifest.json`

The `agent-definition-reference` field in `agent-session.json` should be updated to use
`{ "$ref": "core.json#/$defs/entity-reference", "x-entity-types": ["agent-manifest", "agent-definition"] }`
instead of a plain string array. This makes the field a proper typed entity reference —
consistent with the `entity-reference` type used throughout the schema (see the `agentDefinitionTool`
`manifest-reference` field above) — and allows the agent-definition resolver to accept either
a manifest or a definition entity as input.

```json
{
  "entity-types": ["entity", "agent-manifest"],
  "names": [["defaults", "agent-manifests", "sub-agent-dispatcher"]],
  "display-name": { "default": "Sub-Agent Dispatcher" },
  "agent-type": "sub-agent-dispatcher",
  "tools": [
    {
      "kind": "agent-definition",
      "name": "default",
      "description": "A GitHub Copilot sub-agent.",
      "manifest-reference": ["defaults", "agent-manifests", "github-copilot"]
    }
  ]
}
```

### How `AgentFactory` uses this manifest

When `AgentFactory` encounters a manifest whose `agent-type` is `"sub-agent-dispatcher"`,
it reads the `tools` array from the manifest entity and extracts all entries whose `kind`
is `"agent-definition"`. Each entry is resolved via `AgentDefinitionResolver` (issue #999)
and collected into the `IReadOnlyList<AgentDefinitionTool>` passed to
`SubAgentDispatcherOptions.AgentDefinitionTools`.

The default manifest above provides a single `"default"` entry wired to the standard
`["defaults", "agent-manifests", "github-copilot"]` definition, so sub-agents run as
GitHub Copilot agent sessions by default.

### Creating a dispatcher session

A user creates a `SubAgentDispatcherChatClient` session by creating an `agent-session`
entity whose `agent-definition-reference` points to
`["defaults", "agent-manifests", "sub-agent-dispatcher"]`:

```json
{
  "entity-types": ["entity", "agent-session"],
  "agent-session-id": "<session-guid>",
  "agent-definition-reference": ["defaults", "agent-manifests", "sub-agent-dispatcher"]
}
```

No additional properties are required; the dispatcher picks up the sub-agent definitions
from the manifest's `tools` list automatically via `AgentDefinitionResolver`.

---

## Slash commands

### `/available-subagents`

Lists all available `agent-definition` tool entries in the dispatcher's manifest:

```
Available sub-agent definitions:
  foo  — A specialized agent for foo tasks.
  bar  — A specialized agent for bar tasks.
```

`GetCompletionsAsync` is not applicable for this command (no arguments).

### `/new-subagent [definition-id] [subagent-id]`

Slash command wrapper for the `new:` prefix behaviour:

- `GetCompletionsAsync` for the first argument returns available definition IDs (from the `agent-definition` tool list) with their descriptions as completions.
- `GetCompletionsAsync` for the second argument suggests a slug derived from the current chat context, or leaves it empty for the user to supply.
- Executing `/new-subagent foo my-task` is equivalent to sending `new(foo my-task): ` with the next user message as the prompt. If additional text is provided as slash command arguments beyond the two tokens, that text is used as the prompt directly.

### `/subagent [subagent-id]`

Slash command for routing to an existing sub-agent, equivalent to `<id>:` prefix:

- `GetCompletionsAsync` returns existing sub-agent IDs with their descriptions.
- Executing `/subagent foo-bar` routes the accompanying or next user message to sub-agent `foo-bar`.

---



Implementation tasks in dependency order (issue numbers to be assigned when bugs are
filed):

1. **`agent-definition` tool type** — define the `AgentDefinitionTool` class and its JSON schema; extend the `agent-manifest` schema to accept a `tools` array containing `"kind": "agent-definition"` entries (with inline `definition` or `manifest-reference`).

2. **`sub-agent-dispatcher` provider constant** — add the `"sub-agent-dispatcher"`
   discriminator to the `AgentDefinition` JSON schema's `model.provider` enum; update
   `AgentFactory` provider-switch to recognise it.

3. **`SubAgentDispatcherOptions` and `AgentDefinitionTool`** — implement the options record with
   `AgentDefinitionTools`, `RecencyThreshold`, `AmbiguityThreshold`; implement `AgentDefinitionTool`.

4. **`DispatchedSubAgent`** — implement the internal tracking record.

5. **Message parser** — implement the whole-message prefix parser (regex-based, `s`
   flag for multiline bodies); cover all prefix forms (`new:`, `new(def):`, `new(def id):`, `<id>:`,
   bare `:`), definition-name lookup with error for unknown names, unrecognised-message handling,
   and the most-recently-dispatched tracking; unit-test in isolation.

6. **Slug generator** — implement `GenerateSlug(prompt)`: lowercase, hyphenated, max 5
   words, deduplication suffix if collision; unit-test in isolation.

7. **Fuzzy router** — implement cosine-similarity routing with recency bias and
   ambiguity detection; depends on `IEmbeddingsProvider`; unit-test against
   `DeterministicEmbeddingsProvider`.

8. **`SubAgentDispatcherChatClient` core** — implement `GetStreamingResponseAsync`
   processing loop: parse whole-message prefix, definition lookup, dispatch with streaming acknowledgement,
   record `DispatchHistoryIndex`, wait-for-idle, copy new `ChatMessage` items into
   dispatcher response, interrupt propagation on cancellation.

9. **Entity naming helper** — implement the `dispatcher entity name + sub-agent id`
   concatenation; add tests covering the two-prefix (username + id) expansion.

10. **`AgentFactory` integration** — wire the new provider case; extract `AgentDefinitionTool`
    entries from the manifest's `tools` array and resolve each via `AgentDefinitionResolver`;
    propagate into `SubAgentDispatcherOptions.AgentDefinitionTools`; register
    `IEmbeddingsProvider` in the `AgentServices` DI container if not already present;
    add the default manifest JSON file.

11. **`AgentDefinitionResolver` (issue #999)** — this is a prerequisite; the dispatcher
    depends on it to resolve `manifest-reference` entries in `AgentDefinitionTool` to a usable
    `AgentDefinition`. If #999 is not ready, the dispatcher can temporarily accept only
    inline `definition` entries as a stopgap.

12. **Slash commands** — implement `ISlashCommandHandler` for `/available-subagents`,
    `/new-subagent` (with `GetCompletionsAsync` returning definition IDs), and `/subagent`
    (with `GetCompletionsAsync` returning active sub-agent IDs).

13. **Persistence restore** — on dispatcher session restart, query the DAL for all
    child entity names and reconstruct the `_subAgents` dictionary from persisted
    entity properties (description, last-updated). Depends on §9 (naming helper).

14. **Integration tests** — end-to-end test with `InMemoryDataAccessLayer` and
    `EchoChatClient` as the sub-agent definition: create two sub-agents using named definitions,
    route messages, assert idle-detection, output streaming, and interrupt propagation.
