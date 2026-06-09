# User Computer Profile Schema

User-computer-profile entities represent a user's account and settings on a specific computer. They bridge user identities with computer systems.

## Expected shape

```json
{
  "entity-id": "<stable deterministic id>",
  "entity-types": ["user-computer-profile"],
  "$schema": "https://schemas.workspaces.phantom.to/workspaces/data/core/user-computer-profile.json",
  "names": [
    ["computer-user-profiles", "computers", "dns", "foo.example.com", "users", "dev", "alice"]
  ],
  "computer-reference": ["computers", "dns", "foo.example.com"],
  "user-reference": ["users", "dev", "alice"],
  "home-directory": "/home/alice"
}
```

## Properties

- `names` (array, required): Profile identifiers following pattern `["computer-user-profiles", <computer-name>, <username>]`
  - Concatenates the computer name array and username array after the prefix
- `computer-reference` (string, required): Reference to the computer entity
- `user-reference` (string, required): Reference to the user entity
- `home-directory` (string, optional): Home directory path for this user on this computer

## Naming Pattern

User-computer-profile names concatenate three components:

```
["computer-user-profiles", <computer-name-components>, <username-components>]
```

For a computer named `["computers", "dns", "foo.example.com"]` and user `["users", "dev", "alice"]`:

```
["computer-user-profiles", "computers", "dns", "foo.example.com", "users", "dev", "alice"]
```

**Note on JSON Schema Concatenation:** Standard JSON Schema does not directly support array concatenation syntax. In practice, the concatenated array is validated by length constraints and description. The array must maintain the logical structure where computer name components and username components follow the prefix in order.

## Guidance

- Use user-computer-profile to establish user accounts on specific computers
- The computer-reference and user-reference fields maintain semantic links to specific computer and user entities
- home-directory should reflect the actual home directory path on the target system (platform-specific)
- Multiple user-computer-profile entities can reference the same user on different computers
- Multiple user-computer-profile entities can reference different users on the same computer
