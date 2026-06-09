# Computer Schema

Computer entities represent physical or virtual computers that can be identified by various naming schemes (DNS names, hostnames, IP addresses, etc.).

## Expected shape

```json
{
  "entity-id": "<stable deterministic id>",
  "entity-types": ["computer"],
  "$schema": "https://schemas.workspaces.phantom.to/workspaces/data/core/computer.json",
  "names": [
    ["computers", "dns", "foo.example.com"],
    ["computers", "hostname", "foo"]
  ],
  "os": "linux"
}
```

## Properties

- `names` (array, required): Computer identifiers following pattern `["computers", "<name-type>", "<name>"]`
  - `name-type` indicates the identification scheme: `dns`, `hostname`, `ip`, `netbios`, etc.
  - `name` is the actual identifier value
- `os` (string, optional): Operating system identifier (`windows`, `linux`, `macos`, etc.)

## Naming Pattern

Computer names must follow the array pattern:
```
["computers", "<name-type>", "<name>"]
```

Examples:
- `["computers", "dns", "workstation.internal.corp"]`
- `["computers", "hostname", "dev-machine-01"]`
- `["computers", "ip", "192.168.1.100"]`

A single computer entity can have multiple names of different types to support various identification schemes.

## Guidance

- Use DNS names as primary identifiers when available
- Include multiple name types for computers with multiple interfaces or identification methods
- OS field helps categorize and filter computers by operating system
- Computer entities can be referenced by user-computer-profile entities to establish user accounts on systems
