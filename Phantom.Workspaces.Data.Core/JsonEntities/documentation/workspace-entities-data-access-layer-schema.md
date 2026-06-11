# Workspace Entities Data Access Layer Schema

Schema for serializing `GetRequest`, `GetEntityRequest`, and `GetRelationshipRequest` payloads used by workspace view definitions.

The top-level request shape uses:

- `get-entity`: array of get-entity requests.
- `relationships-to-return`: optional relationship filters.
- `timestamps`: optional as-of timestamps.

Each `get-entity` item supports:

- `entity-id`
- `entity-name`
- `enumerate-children` (`self`, `children`, `all-children`)
- `entity-type-names`
- `relationships-to-return`
