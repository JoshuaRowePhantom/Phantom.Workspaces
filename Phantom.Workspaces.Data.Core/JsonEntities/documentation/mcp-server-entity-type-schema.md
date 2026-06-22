# MCP Server Entity Type Schema

Schema for workspace data entities with entity type `mcp-server`. The `mcp-server` property must conform to the LLM MCP server JSON schema.

MCP server entities register Model Context Protocol servers. They are referenced by name from agent manifest tool resources (type `mcp-server-entity`) and resolved into MCP tools at runtime. Registering servers as entities allows different machines and users to register different servers (for example, machine-specific local stdio commands) under the same logical name.
