# VS Code Tunnel Entity Type Schema

Schema for workspace data entities with entity type `vscode-tunnel`. Represents a VS Code dev tunnel discovered on the current machine.

`vscode-tunnel` entities are discovered and upserted by the `vscode-tunnel-discovery` scheduled tool. One entity is maintained per user-computer-profile under the fixed leaf `vscode-tunnel`. The `tunnel-name` and `tunnel-url` properties identify the tunnel, and `active` reflects whether the tunnel process was running at last discovery.
