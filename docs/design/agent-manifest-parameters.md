# Agent Manifest Parameters

## Purpose

Enable agent manifests to declare typed parameters that callers must supply when
starting a session. Provide a first-class UI for collecting those values before
the session is created, and persist them on the `agent-session` entity so that
sessions can be reconstructed correctly after restart.

---

## Motivation

Manifests that target the `github-copilot` provider need a `working-directory`
at session creation time. In the future, every manifest will accept a
`trust-profile` parameter to select the security context for execution. These
values cannot be baked into the manifest file because they vary per invocation.
Rather than adding ad-hoc fields to the session entity or the shortcut handler,
we introduce a single, general-purpose parameter mechanism driven by
`AgentManifest.Parameters` (from `AgentSchema`).

---

## Concepts

### Parameters in `AgentManifest`

`AgentManifest.Parameters` (`PropertySchema`) already models parameters as a
keyed collection of `Property` objects:

```
Property
  Name        : string          // e.g. "working-directory"
  Kind        : string          // "string" | "integer" | "boolean" | ...
  Description : string
  Required    : bool?
  Default     : object?
```

Parameter typing is punted for this iteration; all values are collected and
stored as strings in this phase.

### `${name}` placeholder substitution

String values in `model.options` (the `additionalProperties` bag of the model
block) may contain `${param-name}` placeholders. `AgentDefinitionParameterSubstitutor`
replaces those placeholders with the resolved string values before the
`AgentDefinition` is passed to `AgentFactory.CreateChatClient`.

---

## Entity schema changes

### `agent-session.json` additions

```json
"parameter-values": {
  "type": "object",
  "description": "Parameter values supplied when this session was created. Keyed by parameter name; all values are strings in this version. Used to reconstruct ModelParameters on session resume.",
  "additionalProperties": { "type": "string" }
}
```

The existing `agent-definition-entity-id` field is renamed to
`agent-source-entity-id` to cover both `agent-definition` and `agent-manifest`
source entity types.

---

## New type: `AgentDefinitionParameterSubstitutor`

Location: `Phantom.Workspaces.Llm.Core`

```csharp
public static class AgentDefinitionParameterSubstitutor
{
    /// <summary>
    /// Validates supplied values against <paramref name="manifest"/>.Parameters,
    /// applies defaults for missing optional parameters, substitutes ${name}
    /// placeholders in model.options string values, and returns the resolved
    /// AgentDefinition ready for CreateChatClient.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when a required parameter has no supplied value and no default.
    /// </exception>
    public static AgentDefinition Substitute(
        AgentManifest manifest,
        IReadOnlyDictionary<string, string>? parameterValues);
}
```

Rules:
- Required parameters with no value and no default → `ArgumentException`.
- Unknown supplied parameters → silently ignored.
- Substitution scope: `model.options` string values only (Phase 1).
- `${name}` with no matching parameter → left as-is (forward-compatible).

---

## `CreateAgentChatRequest` change

Add:

```csharp
/// <summary>
/// Parameter values to substitute into the manifest before building the chat
/// client. Keys are parameter names; values are their resolved string representations.
/// Ignored when <see cref="AgentDefinition"/> is supplied directly (no manifest).
/// </summary>
public IReadOnlyDictionary<string, string>? ModelParameters { get; init; }
```

`AgentFactory.CreateAgentChatAsync` calls
`AgentDefinitionParameterSubstitutor.Substitute(manifest, request.ModelParameters)`
before dispatching to `CreateChatClient` when a manifest is present.

---

## UI: Agent Manifest Launchpad tab

### Behaviour

Opening an `agent-manifest` (or `agent-definition`) entity with the `Open`
shortcut opens a **Launchpad tab** instead of immediately starting a session.

The Launchpad tab shows:

1. The manifest's display name and description.
2. A parameter-entry form (one row per declared parameter).
3. A **Start Session** button (disabled until all required params are valid).
4. An **Edit Manifest** button.

If the manifest declares no parameters, `Start Session` is enabled immediately
and clicking it creates the session without showing the form.

### View model: `AgentManifestLaunchpadViewModel`

```csharp
public sealed class AgentManifestLaunchpadViewModel : WorkspaceTabViewModel
{
    // Manifest source
    public SubscribedEntityViewModel ManifestEntity { get; }

    // Per-parameter rows derived from manifest.Parameters.Properties
    public ObservableCollection<AgentManifestParameterRowViewModel> Parameters { get; }

    // True when all required parameters have non-empty values
    public bool CanStart { get; }

    // Commands
    public ICommand StartSessionCommand { get; }
    public ICommand EditManifestCommand { get; }
}
```

### View model: `AgentManifestParameterRowViewModel`

```csharp
public sealed class AgentManifestParameterRowViewModel : ViewModelBase
{
    public string Name { get; }           // parameter name (e.g. "working-directory")
    public string DisplayName { get; }    // human-friendly (e.g. "Working Directory")
    public string Description { get; }
    public bool IsRequired { get; }
    public string Value { get; set; }     // bindable; initialized from Property.Default
    public bool IsValid { get; }          // non-empty when Required, otherwise always true
}
```

### Tab identity

The Launchpad tab uses the manifest entity's id as its tab id so re-opening
the same manifest focuses the existing tab rather than opening a duplicate.

### Start Session flow

```
StartSessionCommand
  → Validate all required params (guard; already enforced by CanStart)
  → Collect IReadOnlyDictionary<string, string> from parameter rows
  → AgentDefinitionParameterSubstitutor.Substitute(manifest, values)
  → AgentFactory.CreateAgentChatAsync(CreateAgentChatRequest {
        AgentManifest = manifest,
        ModelParameters = values,
        AgentServices = ...
    })
  → CreateAgentSessionEntity (writes parameter-values + agent-source-entity-id)
  → OpenAgentSessionShortcutHandler.CreateAgentSessionTabAsync(...)
  → mainWindowViewModel.OpenTabAsync(agentSessionTab)
  → Close (or keep) the Launchpad tab
```

The Launchpad tab stays open so the user can start additional sessions with
different parameter values.

---

## UI: Manifest editor

### Behaviour

Clicking **Edit Manifest** on the Launchpad (or using the `Edit` shortcut on an
`agent-manifest` entity) opens the manifest in an editor tab.

The editor always presents a **Clone** affordance prominently so the user is
reminded to work on a copy before modifying a shared manifest.

The editor header / toolbar exposes:

- **Clone** — duplicate the entity with a new name and open the clone for editing.
- **Save** — persist changes to the current entity.
- **Start Session** — save (if dirty) and navigate to the Launchpad tab for this
  manifest.

### Shortcut handler: `EditAgentManifestShortcutHandler`

Handles `Shortcut.Edit` on `agent-manifest` entities. Opens
`AgentManifestEditorWorkspaceTabViewModel`. Tab id: `"edit-manifest-{entityId}"`.

### View model: `AgentManifestEditorViewModel`

```csharp
public sealed class AgentManifestEditorViewModel : WorkspaceTabViewModel
{
    public SubscribedEntityViewModel ManifestEntity { get; }
    public string ManifestJson { get; set; }  // live-editable JSON text
    public bool IsDirty { get; }

    public ICommand SaveCommand { get; }
    public ICommand CloneCommand { get; }
    public ICommand StartSessionCommand { get; }  // save-then-open-launchpad
}
```

---

## Shortcut handler changes

### `OpenAgentManifestShortcutHandler` (replacement)

Instead of creating a session directly, opens the Launchpad tab:

```csharp
public override async Task<bool> Handle(...)
{
    var tab = new AgentManifestLaunchpadWorkspaceTabViewModel(
        manifestEntity,
        agentSessionShortcutContext,
        openAgentSessionShortcutHandler,
        mainWindowViewModel);

    await mainWindowViewModel.OpenTabAsync(tab);
    return true;
}
```

### `OpenAgentDefinitionShortcutHandler` (same change)

Agent definitions are also treated through the Launchpad. If the definition has
no parameters the Launchpad auto-starts the session immediately (zero-friction
path identical to the current direct-start).

---

## Session resume: parameter-values round-trip

When `OpenAgentSessionShortcutHandler` reconstructs a session from a stored
`agent-session` entity it reads `parameter-values` back and supplies them as
`CreateAgentChatRequest.ModelParameters`:

```csharp
var parameterValues = agentSessionEntityData
    .TryGetProperty("parameter-values", out var pv)
    ? ReadStringDictionary(pv)
    : null;

createAgentChatRequest = new CreateAgentChatRequest
{
    AgentManifest = ...,
    ModelParameters = parameterValues,
    ...
};
```

---

## Parameters of note

### `working-directory` (Copilot manifests)

Copilot manifests declare:

```yaml
parameters:
  properties:
    working-directory:
      kind: string
      description: "Directory the Copilot CLI uses for file operations."
      required: true
```

The Launchpad row for this parameter should show a directory-picker button
alongside the text input. The picker is activated when the parameter `kind` is
`"string"` and the parameter `name` matches `"working-directory"` (or a
convention TBD).

### `trust-profile` (future)

All manifests will eventually expose a `trust-profile` parameter (kind:
`"string"`, required: false, default: `"default"`). The Launchpad row will
render a dropdown bound to the available `llm-trust-profile` entities.
This is out of scope for the current iteration; the mechanism is intentionally
general enough to support it.

---

## File checklist

| File | Change |
|---|---|
| `Phantom.Workspaces.Data.Core/JsonSchemas/agent-session.json` | Add `parameter-values`; rename `agent-definition-entity-id` → `agent-source-entity-id` |
| `Phantom.Workspaces.Llm.Core/AgentDefinitionParameterSubstitutor.cs` | New |
| `Phantom.Workspaces.Llm.Core/CreateAgentChatRequest.cs` | Add `ModelParameters` |
| `Phantom.Workspaces.Llm.Core/AgentFactory.cs` | Call substitutor before `CreateChatClient` |
| `Phantom.Workspaces/ViewModels/AgentManifestLaunchpadViewModel.cs` | New |
| `Phantom.Workspaces/ViewModels/AgentManifestParameterRowViewModel.cs` | New |
| `Phantom.Workspaces/ViewModels/AgentManifestEditorViewModel.cs` | New |
| `Phantom.Workspaces/ViewModels/OpenAgentManifestShortcutHandler.cs` | Replace direct-start with Launchpad open |
| `Phantom.Workspaces/ViewModels/OpenAgentDefinitionShortcutHandler.cs` | Same |
| `Phantom.Workspaces/ViewModels/EditAgentManifestShortcutHandler.cs` | New |
| `Phantom.Workspaces/ViewModels/OpenAgentSessionShortcutHandler.cs` | Read `parameter-values` on resume |
| `Phantom.Workspaces/Views/AgentManifestLaunchpadView.axaml` | New |
| `Phantom.Workspaces/Views/AgentManifestEditorView.axaml` | New |
| `Phantom.Workspaces.Llm.Core.Tests/AgentDefinitionParameterSubstitutorTests.cs` | New |
