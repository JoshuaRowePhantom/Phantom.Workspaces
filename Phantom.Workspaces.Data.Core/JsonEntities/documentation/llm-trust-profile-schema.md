# LLM trust profile schema

Defines the persisted **entity-level** trust profile used for composition into runtime LLM trust profiles.

The runtime/composed trust profile strips user semantics (`name`, `base-trust-profiles`) and keeps only effective execution policy.

## Expected shape

```json
{
  "entity-id": "<uuid>",
  "entity-types": ["llm-trust-profile", "..."],
  "names": [["trust-profiles", "default"]],
  "display-name": { "default": "Default Trust Profile" },
  "base-trust-profiles": ["<entity-id-or-name>", "..."],
  "hosting-workspaces-client-instances": [".", "remote-client"],
  "mount-points": [
    {
      "source-path": "/host/path",
      "target-path": "/workspace",
      "access-mode": "read-only|read-write",
      "type": "bind|volume|tmpfs"
    }
  ],
  "network-access-policy": "no-network|local-network|natted-network|host-network",
  "https-proxy-policy": {
    "mode": "disabled|required|optional",
    "proxy-url": "https://proxy.example.com:8443",
    "credentials-reference": "<entity-id-or-name>"
  },
  "allowed-mcp-tool-call-schemas": [
    {
      "type": "object",
      "required": ["toolName", "input"],
      "anyOf": []
    }
  ]
}
```

## Notes

- `hosting-workspaces-client-instances` supports `"."` for the local hosting client instance.
- `allowed-mcp-tool-call-schemas` are composed with `anyOf` when building effective policy.
- Command execution is modeled as MCP tool usage and controlled through the allowed MCP tool-call schemas.
