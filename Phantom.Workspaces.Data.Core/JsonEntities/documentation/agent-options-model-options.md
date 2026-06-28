# Agent Options — Model Options

The `model.options` object configures LLM sampling parameters and provider-specific extensions.

<!-- When changing model.options fields, additionalProperties keys, or working-directory handling, update ["documentation", "agent-options", "model-options"] and ["documentation", "agent-options", "providers"]. -->

## Standard fields (all providers)

These map directly to `ChatOptions` properties in `AgentFactory.ApplyModelOptions`:

| Field | Type | `ChatOptions` property | Notes |
|---|---|---|---|
| `temperature` | float | `Temperature` | Sampling temperature. |
| `topP` | float | `TopP` | Nucleus sampling. |
| `frequencyPenalty` | float | `FrequencyPenalty` | Penalise token frequency. |
| `presencePenalty` | float | `PresencePenalty` | Penalise token presence. |
| `maxOutputTokens` | int | `MaxOutputTokens` | Maximum tokens in response. |
| `topK` | int | `AdditionalProperties["topK"]` | Top-K sampling. |

## `additionalProperties` bag

All remaining keys in `model.options.additionalProperties` are forwarded verbatim into `ChatOptions.AdditionalProperties`. The table below documents the keys that Phantom.Workspaces itself interprets:

| Key | Type | Provider(s) | Description |
|---|---|---|---|
| `thinking` | bool \| string | `github-copilot` | Enables or configures reasoning/thinking mode. `true`/`"high"` → high effort; `"medium"`/`"med"` → medium; `"low"` → low; `false`/`"none"`/`"off"` → disabled. Maps to `ReasoningOptions.Effort` → `CopilotSdkChatClient` `ReasoningEffort`. |
| `working-directory` | string | `github-copilot` | Used as a `${working-directory}` placeholder target in parameter substitution. Typically set via `AgentDefinition.workingDirectory` or as a manifest parameter rather than a hardcoded option value. |
| `num_ctx` | int | `ollama` | Context window token count. Passed through to Ollama via `ChatOptions.AdditionalProperties`. |
| `keep_alive` | string | `ollama` | How long Ollama keeps the model loaded between requests (e.g. `"15m"`, `"1h"`, `"-1"` for forever). |
| `additionalInstructions` | string | all | Extra instructions appended to the system prompt at runtime. Set programmatically from `PromptAgent.additionalInstructions`; do not set manually. |

## Parameter substitution in `additionalProperties`

String values in `model.options.additionalProperties` may contain `${param-name}` placeholders. `AgentDefinitionParameterSubstitutor` replaces them with resolved manifest parameter values before the definition is passed to `AgentFactory.CreateChatClient`. See `["documentation", "agent-options", "parameters"]` for details.

## Example

```json
{
  "model": {
    "id": "qwen3:latest",
    "provider": "ollama",
    "connection": { "kind": "Anonymous", "endpoint": "http://localhost:11434" },
    "options": {
      "temperature": 0.7,
      "maxOutputTokens": 4096,
      "additionalProperties": {
        "num_ctx": 8192,
        "keep_alive": "15m",
        "thinking": false
      }
    }
  }
}
```
