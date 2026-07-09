# User Account Schema

User-account entities represent a user's account on an external provider such as GitHub, Azure DevOps, or other services. They enable UsageMetricsService and other components to discover which external-provider accounts exist in the workspace.

## Expected shape

```json
{
  "entity-id": "<stable deterministic id>",
  "entity-types": ["user-account"],
  "$schema": "https://schemas.workspaces.phantom.to/workspaces/data/core/user-account.json",
  "names": [
    ["users", "username", "jrowe", "user-accounts", "github.com"]
  ],
  "provider": "https://github.com",
  "user-name": "jrowe"
}
```

## Properties

- `names` (array, required): Account identifiers following pattern `["users", "username", "<username>", "user-accounts", "<provider-hostname>"]`
  - The last component is the hostname of the provider (e.g., "github.com", "dev.azure.com")
- `provider` (string, required): Provider base URL in URI format (e.g., "https://github.com", "https://dev.azure.com")
- `user-name` (string, required): The account username on this provider

## Naming Pattern

User-account names live under a user entity and are structured as:

```
["users", "username", "<username>", "user-accounts", "<provider-hostname>"]
```

**Examples:**

- GitHub account for user "jrowe": `["users", "username", "jrowe", "user-accounts", "github.com"]`
- Azure DevOps account for user "alice": `["users", "username", "alice", "user-accounts", "dev.azure.com"]`

The naming pattern ensures:
- Each user can have multiple external provider accounts
- Accounts are uniquely identified by provider hostname
- Natural hierarchical organization under the user entity

## Guidance

- Use user-account to establish connections between workspace users and external provider identities
- The `provider` field should be the base URL of the service (e.g., "https://github.com", not a specific repository)
- The `user-name` field should match the external provider's username exactly
- Multiple user-account entities can exist for the same user on different providers
- The entity name's provider-hostname component should match the hostname from the provider URL
