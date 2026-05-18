# User entity type naming

User entity names should be structured as:

`["users", "<type>", "<realm>" (optional), "<name>"]`

Examples:

- `["users", "upn", "user@example.com"]`
- `["users", "domain", "example\\user"]`
- `["users", "web", "github.com", "user@github.com"]`
- `["users", "upn", "exmaple.com", "user@example.com"]`

All user names must begin with `"users"`.
