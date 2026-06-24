# Slash commands

## Purpose

Define a slash-command model for the Phantom.Workspaces chat input that lets users issue
structured control directives (such as `/cwd`) without sending them as natural-language
messages to the underlying LLM. Slash commands are agent-type-aware: a command may be
valid only for specific providers or agent configurations.

## Background

Phantom.Workspaces hosts chat agents through `AgentChat`. The chat input accepts free-form
messages that are forwarded verbatim to the LLM session. Certain operations — changing the
agent's working directory, adjusting model options, or managing session state — are
Phantom.Workspaces-level concerns rather than conversational requests; they should be
handled before a message reaches the LLM and should never pollute the conversation history.

Separately, the GitHub Copilot SDK exposes a `SessionConfig.Commands` list that registers
named in-process handlers recognized by the Copilot CLI process for its own command
dispatch. These are two distinct layers (see §Two command layers below).

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

For `/cwd`, Layer 1 is the correct mechanism because changing the process-level working
directory requires a new `CopilotClient` (see §Working directory section in
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

    /// <summary>
    /// When true, the chat UI must recreate the <see cref="AgentChat"/> after this
    /// command completes, using the updated session entity as the new configuration
    /// source.
    /// </summary>
    public bool RequiresAgentRecreation { get; init; }
}
```

### `ISlashCommandRegistry`

```csharp
public interface ISlashCommandRegistry
{
    IReadOnlyList<ISlashCommandHandler> GetCommands(AgentDefinition agentDefinition);
}
```

Returns the set of commands applicable to a given agent definition. Agent-type-specific
commands (e.g., Copilot-only `/cwd`) are included only when the definition's provider
matches.

## `/cwd` command

`/cwd` sets the working directory for the agent session.

```
/cwd <path>
/cwd          (with no argument: prints the current working directory)
```

### Behavior

1. If no argument is provided, respond with the current CWD from the `agent-session`
   entity's `cwd` field (or the process default if unset).
2. If a path is provided:
   a. Resolve and normalize the path.
   b. Validate that the path exists and is a directory. Return an error status if not.
   c. Update the `agent-session` entity's `cwd` field to the new path.
   d. Return `SlashCommandResult { RequiresAgentRecreation = true }`.
3. The chat UI tears down the current `AgentChat` (which disposes the `CopilotClient`
   process) and constructs a new one, picking up the updated `cwd` from the session entity.

### Why recreation is required

The Copilot CLI working directory is fixed at process startup via `CopilotClientOptions.Cwd`.
The `CopilotSdkChatClient` reuses one `CopilotClient` per instance and only creates a new
`CopilotSession` when the session signature changes. Since `Cwd` is on `CopilotClientOptions`
(not `SessionConfig`), changing the CWD requires a new `CopilotClient` instance, which means
a new `CopilotSdkChatClient` and a new `AgentChat`.

`SessionConfig.WorkingDirectory` is also forwarded and is part of the session signature, so
it changes with every session recreation anyway. Neither field can be mutated on a live
session.

### Applicable agent types

`/cwd` is registered only when the agent definition uses provider `github-copilot`. Other
providers (e.g., `github-models`, `ollama`) do not use the Copilot CLI and may not have a
concept of a process-level working directory; a similar command may be added for them
separately if warranted.

## General framework for session-property commands

Commands that control agent session properties follow the same pattern as `/cwd`:

1. The command reads or writes a field on the `agent-session` workspace entity.
2. If the change requires recreating the agent (any property baked into `CopilotClientOptions`
   or the first-turn `SessionConfig`), the result sets `RequiresAgentRecreation = true`.
3. If the change can be applied without recreation (e.g., a runtime-mutable option),
   the result leaves `RequiresAgentRecreation = false` and updates live state in the
   running `AgentChat`.

### Properties that require recreation (Copilot)

| Property | `CopilotClientOptions` field | `SessionConfig` field |
| --- | --- | --- |
| Working directory | `Cwd` | `WorkingDirectory` |
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

## UI integration

The chat input editor (`AgentChatEditorControl`) intercepts input beginning with `/` and
before the user commits (presses Enter):

1. Queries `ISlashCommandRegistry` for matching commands.
2. Shows a completion picker listing matching command names and descriptions.
3. On commit:
   - If a `/` message matches exactly one command, execute the command handler.
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

Add a `cwd` property to the `agent-session` JSON schema
(`Phantom.Workspaces.Data.Core/JsonSchemas/agent-session.json`):

```json
"cwd": {
  "type": "string",
  "description": "Runtime working-directory override for the agent session. When set, overrides the working directory specified in the agent definition."
}
```

### `AgentDefinition` schema (Llm.Core)

Add a top-level `workingDirectory` property to `AgentDefinition.json` so a default CWD can
be declared at the definition level:

```json
"workingDirectory": {
  "type": "string",
  "description": "Default working directory for the agent. For github-copilot agents, forwarded to CopilotClientOptions.Cwd and SessionConfig.WorkingDirectory."
}
```

The `agent-session` entity's `cwd` field takes precedence over this value at runtime.

### `agent-manifest` entity schema (workspace data layer)

The `agent-manifest` entity embeds a full `AgentDefinition` template, so no separate
schema change is needed for the manifest entity; the change to `AgentDefinition.json`
propagates automatically. Optionally, the manifest `parameters` block can declare a
`cwd` parameter to make it configurable per instantiation site.

## Source layout

In `Phantom.Workspaces.Agent.Gui`:

- `ViewModels/SlashCommands/ISlashCommandHandler.cs` (new)
- `ViewModels/SlashCommands/SlashCommandContext.cs` (new)
- `ViewModels/SlashCommands/SlashCommandResult.cs` (new)
- `ViewModels/SlashCommands/ISlashCommandRegistry.cs` (new)
- `ViewModels/SlashCommands/CompositeSlashCommandRegistry.cs` (new)
- `ViewModels/SlashCommands/CwdSlashCommandHandler.cs` (new)
- `Controls/AgentChatEditorControl.axaml.cs` (integrate completion picker + interception)

In `Phantom.Workspaces.Llm.Core`:

- `AgentDefinition.json` — add `workingDirectory` field

In `Phantom.Workspaces.Data.Core`:

- `JsonSchemas/agent-session.json` — add `cwd` field

## Non-goals

1. Sending slash commands to the LLM as plain text. Commands are always intercepted.
2. Slash commands for providers other than `github-copilot` in the first iteration (beyond
   what naturally applies to all providers).
3. Copilot-CLI-native command registration (`SessionConfig.Commands`) as a primary
   mechanism for `/cwd`; Layer 1 is sufficient and correct.
4. Persisting slash-command history as conversation turns.
