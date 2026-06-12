# Workspace Entity Tool Instructions

Before doing any workspace entity operations, first read the detailed instructions entity with this exact `workspaces_entity_get` invocation:

```json
{
  "get-entity": [
    {
      "entity-name": ["documentation", "entity-workspace-agent-tool-instruction-details"]
    }
  ],
  "properties": ["content.default.content.text"]
}
```

Pass `get-entity` and `properties` as raw JSON arrays, not JSON-encoded strings.

Read and follow those detailed instructions exactly before making any additional tool calls.
