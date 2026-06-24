# Tool visualization

## Purpose

Add a unified visualization layer for tool calls and tool results so the chat user interface can
render rich, tool-specific output for:

1. embedded Phantom.Workspaces tools, and
2. external tools surfaced by providers such as GitHub Copilot.

The layer must work for both chat output containers:

- SelectableTextBox mode
- FlowDocument mode

and preserve the existing fallback behavior for any content that has no special visualizer.
Every rendered `AIContent` item must also expose a clickable inline inspector affordance that opens a
full content visualization (including metadata and raw payload details).

## Current state

Tool rendering is currently hard-coded in two separate paths:

- `ChatMessageSelectableInlineModel` (`SelectableTextBlockChatOutputModels.cs`)
- `ChatMessageDocumentModel` (`ChatMessageDocumentModel.cs`)

Both switch on `AIContent` directly and render generic labels (`tool call`, `tool result`) plus
pretty-printed JSON text. This duplicates behavior and makes it difficult to add provider-specific
tool visualization (especially Copilot built-in tools).

## Core contracts

### `IToolVisualizerFactory`

```csharp
public interface IToolVisualizerFactory
{
    object? Visualize(
        ToolVisualizationContext context,
        AIContent content,
        Type containerType);
}
```

- `containerType` is one of:
  - SelectableTextBox container type (`typeof(SelectableTextBlock)` in current UI implementation)
  - `typeof(FlowDocument)`
- `null` means "no special visualization; use existing AIContent fallback rendering."

### `CompositeToolVisualizerFactory`

```csharp
public sealed class CompositeToolVisualizerFactory : IToolVisualizerFactory
{
    public static IToolVisualizerFactory Combine(params IToolVisualizerFactory[] factories);
}
```

Behavior:

1. evaluate factories in order,
2. return the first non-null visualization,
3. return null when no factory handles the content.

This lets host code combine built-in visualizers, workspace visualizers, and provider-specific
visualizers without coupling them.

### `ToolVisualizationContext`

```csharp
public sealed record ToolVisualizationContext
{
    public required bool IsThinkingVisible { get; init; }
    public required string AgentSessionId { get; init; }
    public required ChatRole MessageRole { get; init; }
    public required IReadOnlyDictionary<string, object?> Items { get; init; }
}
```

`Items` holds extensible context such as active provider id, trust profile name, or session
metadata. This keeps `Visualize` stable while allowing future inputs.

### `Summary`

```csharp
public sealed record Summary(string Text, object? Visualization);
```

- `Text` is concise user-facing summary text.
- `Visualization` is interpreted by the same rules as `Visualize` return values.
- `Summary` renders in an expander that is **expanded by default**.

## Visualization result interpretation

The object returned by `Visualize` is interpreted with these rules:

| Returned object | SelectableTextBox container | FlowDocument container |
| --- | --- | --- |
| `null` | Existing AIContent fallback rendering | Existing AIContent fallback rendering |
| `Summary` | Expander (expanded by default), header = `Text`, body = interpreted `Visualization` | Same behavior in FlowDocument |
| `Control` | `InlineUIContainer(control)` | `BlockUIContainer(control)` |
| `Inline` / text-inline model | Render inline directly | Convert to plain text and render as flow text |
| `Block` / flow model | Convert to plain text and render inline | Render block directly |
| `string` | Monospace/body inline text (style by type) | Body paragraph text |
| any other object | `DocumentBlockUtilities.PrettyJson(value)` text fallback | same text fallback |

This ensures type-safe rich rendering when possible and deterministic text fallback otherwise.

## Per-`AIContent` inline inspector affordance

Every rendered `AIContent` item (text, reasoning, function call/result, data, uri, error, and
unknown content types) includes a small clickable inline user interface element (for example `…`
or an inspect icon) appended next to that content item's rendered output.

Selecting that inline element opens a full visualization surface (popup window / flyout) for that
single content item.

### Inspector payload requirements

The full visualization must include:

1. content type and role context,
2. all metadata properties available on the content item and chat message context,
3. full content payload (not truncated),
4. structured and raw views (pretty JSON + raw text / bytes where applicable).

For binary `DataContent`, render according to MIME type:

1. `image/*`: render image preview.
2. `text/*`, `application/json`, `*+json`, `application/xml`, `*+xml`: decode and render text
   using declared/default charset.
3. `audio/*` and `video/*`: render media metadata and provide playable surface when available;
   otherwise show metadata plus open/save actions.
4. `application/pdf`: render via a PDF-capable viewer when available, else show metadata plus
   open/save actions.
5. unknown / unsupported MIME types: show metadata, byte length, base64 view, and hex view.

MIME-type rendering in the inspector must **reuse the existing shared `DataContent` MIME-type
rendering controls/components** (and extend them when needed), rather than introducing a parallel
set of one-off inspector-only renderers.

The inspector is read-only and does not mutate chat history.

## Concrete visualizer factories

### `WorkspaceVisualizerFactory`

Handles Phantom.Workspaces embedded tools first:

- `workspaces_entity_get`
- `workspaces_entity_update`
- `workspaces_entity_generate_guid`
- filesystem MCP tools exposed through the built-in filesystem toolset (`Read`, `Search`,
  `make_directory`, `remove_item`, `move_item`, `Edit`, `EditApply`, `DescribeEdit`)

For file operations:

1. tool call visualization must state which path/file is requested (plus range/operation details),
2. tool result visualization must show file content when available,
3. for edit-style results, show human-readable summaries plus machine-readable payload.

Examples:

- call summary: `Read C:\repo\Program.cs (start=1, end=80)`
- result summary: `Read C:\repo\Program.cs succeeded`
- expanded body: returned file content text

### `CopilotToolVisualizerFactory` (new)

Handles provider tool calls/results emitted through Copilot SDK mapping
(`FunctionCallContent`/`FunctionResultContent` from `CopilotToolEventMapper`), including
GitHub Copilot built-in tools and forwarded tools.

Initial strategy:

1. explicit mappings for known tool names and argument schemas,
2. file-operation heuristics when names vary (look for `path`, `file`, `start`, `end`,
   `content`, `edits`),
3. generic fallback summary for unknown tools.

This factory is combined after `WorkspaceVisualizerFactory` so workspace-specific rendering wins
for known local tools.

## Renderer integration points

Add a single visualization interpreter used by both output modes:

- `ToolVisualizationInterpreter` (new)
  - input: `ToolVisualizationContext`, `AIContent`, `containerType`, `IToolVisualizerFactory`
  - output: container-native render units (inline/block/control/text)

Wire it into:

1. `ChatMessageSelectableInlineModel.Render` (replace direct tool switch logic)
2. `ChatMessageDocumentModel.AppendContent` (replace direct tool switch logic)
3. per-content inspect inline element plumbing for both renderers, opening the shared inspector
   surface with the selected `AIContent` instance.

Both paths continue to use the existing fallback when interpreter returns null.

## Migration plan: existing AIContent rendering to core visualization

### Phase 1: extract common fallback helpers

1. Keep current rendering output unchanged.
2. Extract shared fallback formatting from `ChatMessageSelectableInlineModel` and
   `ChatMessageDocumentModel` into common helpers.
3. Keep `DocumentBlockUtilities.PrettyJson` as the canonical text fallback formatter.

### Phase 2: add visualizer contracts and composite factory

1. Introduce `IToolVisualizerFactory`, `CompositeToolVisualizerFactory`,
   `ToolVisualizationContext`, and `Summary`.
2. Add `ToolVisualizationInterpreter`.
3. Register a default composite with only no-op fallback (behavior unchanged).

### Phase 3: implement `WorkspaceVisualizerFactory`

1. Add tool-specific call/result summaries for workspace and filesystem operations.
2. Ensure file operations show requested file path and result content.
3. Keep unknown tools on fallback path.

### Phase 4: provider-specific expansion (Copilot)

1. Add `CopilotToolVisualizerFactory`.
2. Map known Copilot tool signatures.
3. Add heuristics for unknown file-operation-like tools.
4. Keep full fallback coverage for unmatched content.

### Phase 5: remove duplicated ad hoc tool rendering

1. Delete legacy tool-call/tool-result switch branches from both renderers.
2. Keep a single visualization path shared across containers.

## Source layout / touched code

In `Phantom.Workspaces.Agent.Gui`:

- `ViewModels/Visualization/IToolVisualizerFactory.cs` (new)
- `ViewModels/Visualization/CompositeToolVisualizerFactory.cs` (new)
- `ViewModels/Visualization/ToolVisualizationContext.cs` (new)
- `ViewModels/Visualization/Summary.cs` (new)
- `ViewModels/Visualization/ToolVisualizationInterpreter.cs` (new)
- `ViewModels/Visualization/WorkspaceVisualizerFactory.cs` (new)
- `ViewModels/Visualization/CopilotToolVisualizerFactory.cs` (new)
- `ViewModels/Visualization/AIContentInspectorViewModel.cs` (new)
- `Controls/AIContentInspectorWindow.axaml` (new)
- `Controls/AIContentInspectorWindow.axaml.cs` (new)
- shared `DataContent` MIME-type rendering controls/components (reuse existing; extend in place as
  needed, no duplicate inspector-only renderer stack)
- `ViewModels/DocumentModels/SelectableTextBlockChatOutputModels.cs` (integrate interpreter)
- `ViewModels/DocumentModels/ChatMessageDocumentModel.cs` (integrate interpreter)
- `ViewModels/AgentViewModel.cs` (construct composite factory and pass context inputs)

No changes are required to `Phantom.Workspaces.Llm.Core` for this visualization architecture;
it already emits standardized `FunctionCallContent` and `FunctionResultContent`.

## Test tasks

1. `CompositeToolVisualizerFactory` tests:
   - first non-null wins,
   - null-all returns null,
   - ordering is respected.
2. `ToolVisualizationInterpreter` tests:
   - each return-type mapping for both container types,
   - `Summary` renders expanded by default,
   - text fallback is deterministic.
3. `WorkspaceVisualizerFactory` tests:
   - workspace tool calls/results produce expected summaries,
   - filesystem calls include requested file path,
   - filesystem read results include returned content.
4. `CopilotToolVisualizerFactory` tests:
   - known Copilot file tool calls/results map to readable summaries,
   - unknown tool names still render safe generic summaries.
5. integration tests:
   - `SelectableTextBlockChatOutputModelsTests` verifies rich rendering via interpreter,
   - `ChatDocumentModelsTests` verifies same logical output in FlowDocument mode.
6. inspector tests:
   - every rendered `AIContent` gets a clickable inspect inline element,
   - clicking inspect opens full visualization with metadata + payload,
   - inspector is read-only and does not alter message content.
7. binary MIME rendering tests:
   - `DataContent` routes to correct renderer by MIME type,
   - unsupported MIME type falls back to metadata + base64 + hex,
   - text-like MIME types honor charset decoding.

## TODOs for Copilot tool-output investigation

To build a high-quality Copilot visualizer, capture and classify real Copilot tool payloads first.

1. Add diagnostic capture in development builds for raw `FunctionCallContent` and
   `FunctionResultContent` from Copilot sessions (arguments + results).
2. Create a sanitized fixture corpus grouped by tool name and operation type
   (file read, file edit, search, command execution, web, repository operations).
3. Identify stable argument keys and result shapes per tool.
4. Document naming inconsistencies (same operation, different tool names) and required heuristics.
5. Add fixture-driven tests for every captured shape before implementing mapping logic.
6. Re-run fixture capture after Copilot SDK upgrades and compare diffs.

## Non-goals

1. Replacing `AIContent` itself.
2. Making visualization logic mutate chat history.
3. Provider-specific rendering in `Phantom.Workspaces.Llm.Core` (rendering remains GUI concern).
