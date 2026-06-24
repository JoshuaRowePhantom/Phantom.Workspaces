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

## GitHub CLI (`gh`) tool output shapes

The agent uses `gh` to interact with GitHub. These commands are invoked through the shell tool
(PowerShell). The `CopilotToolVisualizerFactory` should recognize `gh` invocations in the shell
tool's command argument and produce targeted call/result summaries rather than raw JSON dumps.

### `gh release`

| Command | Key flags / args | JSON fields returned |
| --- | --- | --- |
| `gh release list` | `--limit <n>` | array of `{tagName, isDraft, isPrerelease, publishedAt, name}` |
| `gh release view [<tag>]` | `--json <fields>` | `tagName`, `isDraft`, `isPrerelease`, `publishedAt`, `assets[]` |
| `gh release view [<tag>]` | `--json assets --jq '.assets[].name'` | plain list of asset filenames |
| `gh release create <tag> <files…>` | `--title`, `--generate-notes`, `--verify-tag` | URL of the created release |
| `gh release edit <tag>` | `--prerelease` / `--latest` | (no JSON output; exit code) |

**`assets` element shape** (from `--json assets`):

```json
{
  "name": "Phantom.Workspaces-v1.2.3-win-x64.zip",
  "downloadUrl": "https://github.com/…",
  "size": 12345678
}
```

**Stable-channel rule**: a release counts for the stable in-app updater only when
`isDraft == false` and `isPrerelease == false`.

**Expected per-release asset set**: `win-x64` zip + `.sha256`, `win-arm64` zip + `.sha256`
(four files total).

**Call summary hint**: `gh release view <tag>` → `Inspect release <tag>`  
**Result summary hint**: show `tagName`, stable/pre-release status, asset count.

### `gh run`

| Command | Key flags / args | Notable output fields |
| --- | --- | --- |
| `gh run list` | `--workflow release.yml --limit <n>` | `databaseId`, `status`, `conclusion`, `workflowName`, `headBranch`, `event`, `createdAt` |
| `gh run view` | `--log` (or `<run-id>`) | full log text (large); `status`, `conclusion` in structured form |
| `gh run watch` | `--workflow <name>` | real-time status stream; final `conclusion` on completion |

`status` values: `queued`, `in_progress`, `completed`.  
`conclusion` values (when completed): `success`, `failure`, `cancelled`, `skipped`.

**Call summary hint**: `gh run list --workflow release.yml` → `List release pipeline runs`  
**Result summary hint**: show most recent run status and conclusion.

### `gh pr`

| Command | Key flags / args | Notable output fields |
| --- | --- | --- |
| `gh pr list` | `--state merged --search "merged:>=<date>" --limit <n>` | `number`, `title`, `state`, `mergedAt`, `labels[]`, `url` |

**Call summary hint**: `gh pr list …` → `List merged PRs since <date>`  
**Result summary hint**: PR count, date range.

### `gh api`

| Command | Key flags / args | Notable output fields |
| --- | --- | --- |
| `gh api repos/:owner/:repo/releases/generate-notes` | `-f tag_name`, `-f previous_tag_name`, `--jq '.body'` | markdown release notes body |

**Call summary hint**: `gh api … generate-notes` → `Generate release notes for <tag>`  
**Result summary hint**: first line of notes body (truncated).

### Heuristics for `CopilotToolVisualizerFactory`

When the shell tool command string contains `gh `:

1. Match `gh release (view|list|create|edit)` → route to release summary renderer.
2. Match `gh run (list|view|watch)` → route to run status renderer.
3. Match `gh pr list` → route to PR list renderer.
4. Match `gh api .*/generate-notes` → route to release notes preview renderer.
5. Any other `gh` invocation → generic shell fallback (show command + exit code).

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

## GitHub Copilot CLI built-in tool catalog

Captured from session `c115e131-bc5c-40df-a4ef-e59ea5365949` events.jsonl (`tool.execution_start` /
`tool.execution_complete`). This catalog feeds the `CopilotToolVisualizerFactory` mapping table and
fixture corpus (TODOs 2–5 above).

Result content is always a plain string returned in `result.content`; `result.detailedContent` adds
the original query/command as a header prefix when present.

### File system tools

#### `view`

Read a file (or a range of lines within a file).

Arguments:

| Key | Type | Notes |
| --- | --- | --- |
| `path` | `string` | absolute path |
| `view_range` | `[number, number]` | optional; `[start, end]` 1-based lines; `-1` end = file end |
| `forceReadLargeFiles` | `bool` | optional; bypasses 50 KB truncation guard |

Result: file content with `N. ` line-number prefixes per line.

Example call summary: `Read C:\repo\Program.cs (lines 1–65)`
Example result summary: `Read C:\repo\Program.cs`

#### `edit`

Replace exactly one occurrence of a string in an existing file.

Arguments:

| Key | Type | Notes |
| --- | --- | --- |
| `path` | `string` | absolute path |
| `old_str` | `string` | exact substring to replace (must be unique in file) |
| `new_str` | `string` | replacement text |

Result: `File <path> updated with changes.`

Example call summary: `Edit C:\repo\Program.cs`
Example result summary: `C:\repo\Program.cs updated`

#### `create`

Create a new file (fails if the file already exists).

Arguments:

| Key | Type | Notes |
| --- | --- | --- |
| `path` | `string` | absolute path; parent directories must exist |
| `file_text` | `string` | full file content |

Result: `Created file <path> with <N> characters`

Example call summary: `Create C:\repo\NewFile.cs`
Example result summary: `C:\repo\NewFile.cs created`

#### `glob`

Find files by name pattern.

Arguments:

| Key | Type | Notes |
| --- | --- | --- |
| `pattern` | `string` | glob pattern, e.g. `**/*.cs`, `src/**/*.{ts,tsx}` |
| `paths` | `string \| string[]` | optional root directory/directories; defaults to cwd |

Result: matching absolute paths, one per line (empty if no matches).

Example call summary: `Glob **/*.md in C:\repo`
Example result summary: `N file(s) matched`

#### `grep`

Search file contents using ripgrep.

Arguments:

| Key | Type | Notes |
| --- | --- | --- |
| `pattern` | `string` | regex pattern |
| `output_mode` | `string` | `"files_with_matches"` (default) \| `"content"` \| `"count"` |
| `-n` | `bool` | show line numbers (requires `output_mode: "content"`) |
| `-C` | `number` | context lines before and after match |
| `-B` | `number` | context lines before match |
| `-A` | `number` | context lines after match |
| `-i` | `bool` | case-insensitive |
| `glob` | `string` | file glob filter, e.g. `"*.cs"` |
| `type` | `string` | ripgrep file-type filter, e.g. `"cs"`, `"ts"` |
| `paths` | `string \| string[]` | search root(s); defaults to cwd |
| `head_limit` | `number` | limit output to first N results |
| `multiline` | `bool` | enable multiline matching |

Result: ripgrep output matching the chosen `output_mode`.

Example call summary: `Grep "schemasByReference" in *.cs`
Example result summary: `N match(es)` / list of file paths / annotated lines

### Shell execution tools

#### `powershell`

Run a PowerShell command in an interactive session.

Arguments:

| Key | Type | Notes |
| --- | --- | --- |
| `command` | `string` | PowerShell command string |
| `description` | `string` | human-readable label (displayed in UI) |
| `mode` | `string` | `"sync"` (default) \| `"async"` |
| `initial_wait` | `number` | seconds to wait for initial output before backgrounding (sync mode) |
| `shellId` | `string` | optional; reuse an existing session |
| `detach` | `bool` | async only; process persists after session shutdown |

Result (sync / completed async): command stdout/stderr text.
Result (async, still running): shell session ID string.

Example call summary: `Run: dotnet build`
Example result summary: command output (first N lines)

#### `stop_powershell`

Terminate a running PowerShell session by ID.

Arguments:

| Key | Type | Notes |
| --- | --- | --- |
| `shellId` | `string` | ID returned by a prior `powershell` async call |

Result: `<command with id: <shellId> stopped>`

### Web tool

#### `web_fetch`

Fetch a URL and return it as markdown or raw HTML.

Arguments:

| Key | Type | Notes |
| --- | --- | --- |
| `url` | `string` | URL to fetch |
| `raw` | `bool` | optional; `true` = raw HTML, `false` (default) = simplified markdown |
| `max_length` | `number` | optional; character limit (default 5000, max 20000) |
| `start_index` | `number` | optional; pagination offset |

Result: page content prefixed with `Contents of <url>:`.

Example call summary: `Fetch https://example.com`
Example result summary: `Fetched https://example.com`

### Agent / orchestration tools

#### `task`

Launch a specialized sub-agent in a separate context window.

Arguments:

| Key | Type | Notes |
| --- | --- | --- |
| `name` | `string` | short agent name (used to generate human-readable agent ID) |
| `prompt` | `string` | full task description with all required context |
| `agent_type` | `string` | `"explore"` \| `"task"` \| `"general-purpose"` \| `"code-review"` \| `"research"` |
| `description` | `string` | 3–5 word display label |
| `mode` | `string` | optional; `"sync"` \| `"background"` |
| `model` | `string` | optional model override |

Result: agent summary text (brief on success, full output on failure).

Example call summary: `Task: implement-entity-status-badges`
Example result summary: agent completion summary paragraph

#### `task_complete`

Signal task completion from within a sub-agent, returning a summary to the orchestrator.

Arguments:

| Key | Type | Notes |
| --- | --- | --- |
| `summary` | `string` | completion summary delivered back to the calling agent |

Result: the `summary` string echoed back.

#### `ask_user`

Pause and present a question to the human user.

Arguments:

| Key | Type | Notes |
| --- | --- | --- |
| `question` | `string` | question text |
| `choices` | `string[]` | optional list of presented options |

Result: user's free-text or choice response.

Example call summary: `Ask user: <first 60 chars of question>…`
Example result summary: `User responded: <first 60 chars of answer>…`

#### `skill`

Invoke a named skill (loads skill context into the session).

Arguments:

| Key | Type | Notes |
| --- | --- | --- |
| `skill` | `string` | skill name, e.g. `"run-tests"` |

Result: `Skill "<name>" loaded successfully. Follow the instructions in the skill context.`

Example call summary: `Invoke skill: run-tests`
Example result summary: `Skill run-tests loaded`

### Memory / data tools

#### `sql`

Execute SQL against the per-session SQLite database or the global read-only session store.

Arguments:

| Key | Type | Notes |
| --- | --- | --- |
| `description` | `string` | 2–5 word human label |
| `query` | `string` | SQLite-compatible SQL |
| `database` | `string` | optional; `"session"` (default) \| `"session_store"` |

Result: tabular markdown (`N row(s) returned:\n\n| col | … |`) or `N row(s) inserted/updated.`
`detailedContent` prepends the full query text.

Example call summary: `SQL: Insert extracted todos`
Example result summary: `3 row(s) inserted` / `5 row(s) returned`

#### `session_store_sql`

Execute a read-only SQL query against the global historical session store (all past sessions).
Functionally equivalent to `sql` with `database: "session_store"` but a distinct tool name.

Arguments: identical to `sql` (`description`, `query`).

Result: same tabular format; rows include a `_query_source` column (`"cloud"` \| `"local"`).

#### `store_memory`

Persist a durable fact to the agent memory store.

Arguments:

| Key | Type | Notes |
| --- | --- | --- |
| `subject` | `string` | topic label |
| `fact` | `string` | the fact text to store |
| `reason` | `string` | why this fact is worth storing |
| `citations` | `string` | optional; source quotes |
| `scope` | `string` | optional; e.g. `"repository"` |

Result: `Memory stored successfully.`

#### `vote_memory`

Up- or down-vote a previously stored memory to adjust its reliability weight.

Arguments:

| Key | Type | Notes |
| --- | --- | --- |
| `direction` | `string` | `"upvote"` \| `"downvote"` |
| `fact` | `string` | exact fact text being voted on |
| `reason` | `string` | justification for the vote |
| `scope` | `string` | optional |

Result: `Vote recorded successfully.`

## Non-goals

1. Replacing `AIContent` itself.
2. Making visualization logic mutate chat history.
3. Provider-specific rendering in `Phantom.Workspaces.Llm.Core` (rendering remains GUI concern).
