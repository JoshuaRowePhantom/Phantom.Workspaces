# AgentSchema tool kinds

Generated from `AgentSchema` package `1.0.0-beta.8` via reflection (`AgentSchema.dll`).

| Tool type | kind |
| --- | --- |
| `AgentSchema.CodeInterpreterTool` | `code_interpreter` |
| `AgentSchema.CustomTool` | *(empty string; caller-specified custom kind)* |
| `AgentSchema.FileSearchTool` | `file_search` |
| `AgentSchema.FunctionTool` | `function` |
| `AgentSchema.McpTool` | `mcp` |
| `AgentSchema.OpenApiTool` | `openapi` |
| `AgentSchema.WebSearchTool` | `bing_search` |

## Phantom-recognized custom kinds

| Custom kind | Runtime mapping |
| --- | --- |
| `github-cli-builtin-tools` | Provider-specific Copilot SDK policy. Maps to `SessionConfig.AvailableTools`, `SessionConfig.ExcludedTools`, `ResumeSessionConfig.AvailableTools`, `ResumeSessionConfig.ExcludedTools`, and `CopilotClientOptions.Mode` in `GitHub.Copilot.SDK` 1.0.8. |
