# Agent Manifest Entity Type Schema

Schema for workspace data entities with entity type `agent-manifest`. The `manifest` property must conform to the LLM agent manifest JSON schema.

Agent manifests are declarative agent configurations with tool resource references that are resolved at runtime based on execution context (user, machine, workspace). This enables machine-specific and user-specific tool configurations, such as MCP servers that may vary by machine.
