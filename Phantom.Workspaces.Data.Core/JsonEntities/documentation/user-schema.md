# User Schema

User entities represent identities with naming conventions for organization and domain-specific contexts. Users have profiles on computers via user-computer-profile entities.

## Expected Shape

```json
{
  "entity-id": "<generated-guid>",
  "entity-types": ["user"],
  "$schema": "https://schemas.workspaces.phantom.to/workspaces/data/core/user.json",
  "names": [
    ["users", "dev", "alice"],
    ["users", "github", "alice-dev"]
  ],
  "display-name": {
    "default": "Alice Developer"
  }
}
```

## Properties

- `entity-types` (array, required): Must contain "user"
- `names` (array, required): One or more user identifiers following the pattern `["users", <category>, <username>]` or `["users", <category>, <domain>, <username>]`
  - Must have at least 3 components: `["users", <category>, <username>]`
  - Can have 4 components: `["users", <category>, <domain>, <username>]`
  - The second component categorizes the user (e.g., "dev", "github", "azure-devops", "local")
  - Additional components provide domain/organization context
  - Users can have multiple names representing different identity providers
- `display-name` (local-string, optional): Human-readable name shown in UI

## Name Patterns

### Simple Username (3 components)
```
["users", "dev", "alice"]
["users", "local", "admin"]
```

### Domain-Qualified Username (4 components)
```
["users", "github", "github.com", "alice-dev"]
["users", "azure-devops", "contoso", "alice"]
["users", "active-directory", "corp.example.com", "alice"]
```

## Identity Categories

Common category values:

- `"dev"` — Development/generic user identity
- `"local"` — Local system account
- `"github"` — GitHub username
- `"azure-devops"` — Azure DevOps user
- `"active-directory"` — AD domain user
- `"email"` — Email-based identity

## Multiple Identities

A user entity can have multiple names representing the same person across different systems:

```json
{
  "entity-id": "12345678-1234-1234-1234-123456789abc",
  "entity-types": ["user"],
  "names": [
    ["users", "dev", "alice"],
    ["users", "github", "github.com", "alice-dev"],
    ["users", "azure-devops", "contoso", "alice@example.com"]
  ],
  "display-name": {
    "default": "Alice Developer"
  }
}
```

This links the user's identities across multiple platforms.

## Relationships

Users are linked to computers through **user-computer-profile** entities:

```json
{
  "entity-types": ["user-computer-profile"],
  "names": [
    ["computer-user-profiles", "users", "dev", "alice", "computers", "hostname", "devbox"]
  ],
  "computer-reference": ["computers", "hostname", "devbox"],
  "user-reference": ["users", "dev", "alice"],
  "home-directory": "/home/alice"
}
```

Common relationships:
- **owns** — User owns repositories, workspaces, or other resources
- **assigned-to** — User is assigned to tasks or work items
- **created-by** — User created an entity (implicit authorship)

## Current User Reference

Use the special token `${USER}` in entity names to reference the current user:

```
["${USER}", "workspaces", "my-project"]
```

At runtime, `${USER}` expands to the current user's name components (e.g., `["users", "dev", "alice"]`).

## LLM Configuration Guide

To create a user entity that an LLM can use:

1. **Determine username**: Identify the user's username and category
2. **Add domain context**: Include organization/domain if known (optional 4th component)
3. **Link identities**: If the user exists on multiple platforms, include all names
4. **Set display name**: Provide a human-readable display name

Example prompt for LLM:
```
Create a user entity for GitHub user "alice-dev" who is also known as "alice" in the development environment
```

The LLM should:
- Generate a new entity-id (GUID)
- Set entity-types to ["user"]
- Set names to include:
  - ["users", "dev", "alice"]
  - ["users", "github", "github.com", "alice-dev"]
- Set display-name to {"default": "Alice Developer"} or similar

## Usage

User entities serve as:
1. **Identity anchors** — Linking profiles across systems
2. **Ownership tracking** — Identifying who owns workspaces, projects, tools
3. **Assignment targets** — Assigning tasks or responsibilities
4. **Access control** — Determining permissions through user-computer-profiles
5. **Session context** — Providing the current user context for entity operations

The `${USER}` token in queries and entity names provides dynamic user-specific filtering.
