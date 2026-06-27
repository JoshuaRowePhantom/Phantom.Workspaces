# Slash commands

## Purpose

Define a slash-command model for the Phantom.Workspaces chat input that lets users issue
structured control directives (such as `/working-directory`) without sending them as
natural-language messages to the underlying LLM. Slash commands are agent-type-aware: a
command may be valid only for specific providers or agent configurations.

## Background

Phantom.Workspaces hosts chat agents through `AgentChat`. The chat input accepts free-form
messages that are forwarded verbatim to the LLM session. Certain operations — changing the
agent's working directory, adjusting model options, or managing session state — are
Phantom.Workspaces-level concerns rather than conversational requests; they should be
handled before a message reaches the LLM and should never pollute the conversation history.

Separately, the GitHub Copilot SDK exposes a `SessionConfig.Commands` list that registers
named in-process handlers recognized by the Copilot CLI process for its own command
dispatch. These are two distinct layers (see §Two command layers below).

A command picker popup appears as the user types a `/` prefix, providing discoverability
and reducing typing errors (see §Command picker popup).

## Two command layers

### Layer 1 — Phantom.Workspaces chat commands

Commands intercepted by the Phantom.Workspaces chat input layer, resolved entirely within
Phantom.Workspaces before any message is forwarded to the underlying provider.

Characteristics:

- Triggered by a `/` prefix at the start of a chat input message.
- Never forwarded to the LLM.
- Handled synchronously or asynchronously by a registered `ISlashCommandHandler`.
- Can trigger agent-lifecycle operations (session recreation, entity updates, etc.).
- Agent-type-scoped: each agent type declares the commands it supports.
- Consumed by the chat input and result summarized to the user in-line (not as a chat
  message).

### Layer 2 — Copilot CLI native commands

Commands registered via `SessionConfig.Commands` (type `IList<CommandDefinition>`) when
the Copilot SDK session is created. The Copilot CLI process intercepts these before the
prompt is forwarded to the model.

Characteristics:

- Handled in-process by a `CommandHandler` delegate supplied to `CommandDefinition`.
- Visible to the Copilot CLI for help text and autocomplete.
- Appropriate for commands that can be satisfied without recreating the Copilot CLI process
  (e.g., toggling a feature flag, querying session state).
- **Not** appropriate for commands that require a new `CopilotClient` instance (such as
  changing `CopilotClientOptions.Cwd`).

For `/working-directory`, Layer 1 is the correct mechanism because changing the process-level
working directory requires a new `CopilotClient` (see §Working directory section in
`github-copilot-provider-support.md`).

## Core contracts

### `ISlashCommandHandler`

```csharp
public interface ISlashCommandHandler
{
    /// <summary>Command name without the leading slash, e.g. "cwd".</summary>
    string Name { get; }

    /// <summary>Short description shown in the command picker.</summary>
    string Description { get; }

    /// <summary>
    /// Execute the command. The handler receives the remainder of the input
    /// after the command name (trimmed), or an empty string if none was provided.
    /// Returns a <see cref="SlashCommandResult"/> indicating how the chat should
    /// respond.
    /// </summary>
    Task<SlashCommandResult> ExecuteAsync(
        SlashCommandContext context,
        string arguments,
        CancellationToken cancellationToken);
}
```

### `SlashCommandContext`

```csharp
public sealed record SlashCommandContext
{
    /// <summary>The live <see cref="AgentChat"/> instance.</summary>
    public required AgentChat AgentChat { get; init; }

    /// <summary>
    /// The workspace entity id for the <c>agent-session</c> entity, if the session
    /// is persisted.
    /// </summary>
    public string? AgentSessionEntityId { get; init; }

    /// <summary>Access to the workspace data layer for reading/updating entities.</summary>
    public IWorkspaceDataAccessLayer? DataAccessLayer { get; init; }
}
```

### `SlashCommandResult`

```csharp
public sealed record SlashCommandResult
{
    /// <summary>Status message shown inline to the user (not added to history).</summary>
    public required string StatusMessage { get; init; }
}
```

### `ISlashCommandRegistry`

```csharp
public interface ISlashCommandRegistry
{
    IReadOnlyList<ISlashCommandHandler> Commands { get; }
}
```

Exposes the commands registered for the owning `AgentChat` instance. Provider-specific
commands (e.g., Copilot-only `/working-directory`) are registered during `AgentChat`
initialisation when the corresponding chat client is detected in the pipeline.

`AgentChat` exposes a `SlashCommands` property of type `ISlashCommandRegistry`. The GUI
reads from this property; `Phantom.Workspaces.Llm.Core` writes to the underlying
`SlashCommandRegistry` at initialisation time.

## `/working-directory` command

`/working-directory` gets or sets the working directory for the agent session.

```
/working-directory <path>
/working-directory          (with no argument: prints the current working directory)
```

### Behavior

1. If no argument is provided, respond with the current working directory from the
   `agent-session` entity's `working-directory` parameter value (or a "not set" message).
2. If a path is provided:
   a. Validate that the path exists and is a directory. Return an error status if not.
   b. Update the `agent-session` entity's `working-directory` parameter value.
   c. Return a success `SlashCommandResult`.
3. The change takes effect on the **next turn**: `CopilotSdkChatClient.ComputeSessionSignature`
   includes `working-directory`; when the signature changes, `EnsureSessionAsync` calls
   `ResumeSessionAsync` with the updated `WorkingDirectory`, which recreates the Copilot
   session context while preserving conversation history. No `AgentChat` recreation is needed.

### Why recreation is NOT required

`CopilotClientOptions.CurrentWorkingDirectory` (process-level cwd) is fixed at `CopilotClient`
creation time and is NOT updated by `/working-directory`. The process cwd affects Copilot CLI
binary behaviour (config file discovery) and does not need to match the session working
directory.

`ResumeSessionConfig.WorkingDirectory` (session-level cwd) IS updated on the next turn via
the signature mechanism in `CopilotSdkChatClient.EnsureSessionAsync`. This is what the model
sees, and it is sufficient to change the effective working directory without recreating the
`CopilotClient` or the `AgentChat`.

### Applicable agent types

`/working-directory` is registered only when the agent definition uses provider
`github-copilot`. Other providers (e.g., `github-models`, `ollama`) do not use the Copilot
CLI and may not have a concept of a process-level working directory; a similar command may
be added for them separately if warranted.

## `/help` command

`/help` lists all available slash commands or shows detailed help for a single command.

```
/help                   (lists all available commands)
/help <command-name>    (shows detailed help for the named command)
```

### Behavior

`/help` is available for all agent types; it requires no agent-specific context.

1. **No argument**: returns a `StatusMessage` listing every command registered for the
   current agent definition, one per line in the format:
   ```
   /command-name  description
   ```
   The list is sorted alphabetically.

2. **With `<command-name>`**: looks up the named command (without the leading `/`) in the
   registry. If found, returns its full description and usage line. If not found, returns
   an error status: `Unknown command: /command-name`.

### Detailed help protocol

`ISlashCommandHandler` gains an optional `Usage` property for a one-line usage string and
a `LongDescription` property for multi-line help text:

```csharp
public interface ISlashCommandHandler
{
    string Name { get; }
    string Description { get; }

    /// <summary>One-line usage string, e.g. "/working-directory [path]". Optional.</summary>
    string? Usage { get; }

    /// <summary>Extended help text shown by /help. Optional; falls back to Description.</summary>
    string? LongDescription { get; }

    Task<SlashCommandResult> ExecuteAsync(
        SlashCommandContext context,
        string arguments,
        CancellationToken cancellationToken);
}
```

`HelpSlashCommandHandler` reads these properties at execution time; no additional
registration or metadata is needed.

### Applicable agent types

`/help` is registered for all agent types. `ISlashCommandRegistry.GetCommands` always
includes it.

## General framework for session-property commands

Commands that control agent session properties follow the same pattern as `/working-directory`:

1. The command reads or writes a field on the `agent-session` workspace entity.
2. If the change requires recreating the agent (any property baked into `CopilotClientOptions`
   or the first-turn `SessionConfig`), the result sets `RequiresAgentRecreation = true`.
3. If the change can be applied without recreation (e.g., a runtime-mutable option),
   the result leaves `RequiresAgentRecreation = false` and updates live state in the
   running `AgentChat`.

### Properties that require recreation (Copilot)

| Property | `CopilotClientOptions` field | `SessionConfig` field |
| --- | --- | --- |
| Working directory | `CurrentWorkingDirectory` | `WorkingDirectory` |
| CLI path | `CliPath` | — |
| GitHub token | `GitHubToken` | `GitHubToken` |
| Model | — | `Model` |
| Reasoning effort | — | `ReasoningEffort` |
| System instructions | — | `SystemMessage` |
| Tool set | — | `Tools` |

Tool set, model, reasoning, and instructions changes are already handled by
`CopilotSdkChatClient.ComputeSessionSignature`: a change to those values automatically
recreates the Copilot session (but not the client process). `Cwd` and `CliPath` require
recreating the full `AgentChat`.

### Properties that do not require recreation

No currently identified `SessionConfig` properties can be mutated after session creation.
If the Copilot SDK adds mutable session properties in a future release, the corresponding
commands would set `RequiresAgentRecreation = false` and directly invoke SDK APIs.

## Command picker popup

The command picker is a floating popup that appears above the chat input whenever the
user's text begins with `/`. It provides discovery, autocomplete, and keyboard navigation.

### Trigger and filtering

- The popup opens as soon as `/` is the first character in the input.
- As the user continues typing, the list is filtered to commands whose name starts with
  the typed text (after the leading `/`, case-insensitive). Typing `/h` shows only
  commands whose name begins with `h`.
- The popup closes when:
  - The input no longer begins with `/` (user deleted the `/` or pasted different text).
  - The user presses **Escape**.
  - The user commits (Enter) or dismisses (clicks away from input + popup).

### Layout and content

- Positioned directly above the chat input box, anchored to its left edge.
- Each row shows: **command name** (bold, monospace) and **description** (muted, regular).
  Example: `/working-directory  Set the working directory for this session`
- Rows are ordered alphabetically by name, with the best prefix match highlighted first.
- Maximum visible rows: 8; scrollable when more match.
- Width matches the chat input width.

### Keyboard navigation

| Key | Action |
|---|---|
| `↑` / `↓` | Move selection |
| `Tab` or `→` | Complete the selected command name into the input (appends a space) |
| `Enter` | If a row is selected, complete + submit; otherwise submit as typed |
| `Escape` | Dismiss popup; focus stays in input |
| Any other key | Continues filtering; selection resets to the first match |

### Mouse interaction

Clicking a row completes the command name into the input and moves focus back to the
input so the user can type arguments.

### Avalonia implementation notes

- Implemented as a `Popup` (`PlacementMode.Top`, `PlacementTarget` = the input TextBox)
  inside `AgentChatEditorControl.axaml`.
- The popup's `ItemsControl` is bound to a `SlashCommandSuggestionsViewModel` (a
  filtered observable collection derived from `ISlashCommandRegistry`).
- `IsOpen` is driven by a computed property on `AgentChatEditorViewModel`:
  `IsSlashCommandPickerOpen = Text.StartsWith("/") && AvailableCommands.Count > 0`.

## UI integration

The chat input editor (`AgentChatEditorControl`) intercepts input beginning with `/` and
before the user commits (presses Enter):

1. Queries `ISlashCommandRegistry` for matching commands.
2. Shows the command picker popup (see §Command picker popup above).
3. On commit:
   - Parse the input as `/command-name [arguments]`.
   - If `command-name` matches a registered command exactly, execute the command handler.
   - If no command matches, forward the message as a normal chat message (allows legitimate
     slash-prefixed prompts to reach the model if the user explicitly confirms).

Execution feedback (the `StatusMessage`) is displayed as a transient inline notification in
the chat area, not added to the conversation history.

If `RequiresAgentRecreation` is true, the UI:
1. Shows a "Reconnecting…" status in the chat header.
2. Disposes the current `AgentChat`.
3. Recreates the `AgentChat` using `AgentFactory.CreateAgentChatAsync` with the updated
   session configuration sourced from the refreshed `agent-session` entity.
4. Replaces the `AgentChat` reference in the view model.

## Schema changes required

### `agent-session` entity schema (workspace data layer)

Add a `working-directory` property to the `agent-session` JSON schema
(`Phantom.Workspaces.Data.Core/JsonSchemas/agent-session.json`):

```json
"working-directory": {
  "type": "string",
  "description": "Runtime working-directory override for the agent session. When set, overrides the working directory specified in the agent definition."
}
```

### `AgentDefinition` schema (Llm.Core)

`AgentDefinition.json` gains a `parameters` block (see the model parameter substitution
design in `github-copilot-provider-support.md`). The `working-directory` parameter is
declared per definition, not as a top-level hardcoded field.

### `agent-manifest` entity schema (workspace data layer)

The `agent-manifest` entity embeds a full `AgentDefinition` template, so no separate
schema change is needed for the manifest entity; the change to `AgentDefinition.json`
propagates automatically. Optionally, the manifest `parameters` block can declare a
`working-directory` parameter to make it configurable per instantiation site.

## Source layout

In `Phantom.Workspaces.Llm.Core` (namespace `Phantom.Workspaces.Llm.SlashCommands`):

- `SlashCommands/ISlashCommandHandler.cs` — includes `Usage` and `LongDescription`
- `SlashCommands/SlashCommandContext.cs`
- `SlashCommands/SlashCommandResult.cs`
- `SlashCommands/ISlashCommandRegistry.cs` — read-only view: `Commands` property
- `SlashCommands/SlashCommandRegistry.cs` — mutable: `Register(handler)` method
- `SlashCommands/HelpSlashCommandHandler.cs`
- `SlashCommands/WorkingDirectorySlashCommandHandler.cs` — Copilot-only; registered by `AgentChat`
- `AgentChat.cs` — exposes `ISlashCommandRegistry SlashCommands { get; }`; populates it in `InitializeAsync`

In `Phantom.Workspaces.Agent.Gui`:

- `ViewModels/AgentViewModel.cs` — `ConfigureSlashCommands(Func<SlashCommandContext>)` reads from `AgentChat.SlashCommands`
- `ViewModels/QueueComposerViewModel.cs` — `SlashCommandInterceptorAsync` hook (unchanged)

In `Phantom.Workspaces` (workspace host):

- `ViewModels/OpenAgentSessionShortcutHandler.cs` — provides the `SlashCommandContext` factory


## Non-goals

1. Sending slash commands to the LLM as plain text. Commands are always intercepted.
2. Slash commands for providers other than `github-copilot` in the first iteration (beyond
   what naturally applies to all providers).
3. Copilot-CLI-native command registration (`SessionConfig.Commands`) as a primary
   mechanism for `/cwd`; Layer 1 is sufficient and correct.
4. Persisting slash-command history as conversation turns.
