# Blocked interest schema

Marks a `target` entity as **blocked** for a `user` — the user is waiting on something before they
can act. Derived from `relationship.json`.

## Participants

- `target` (required): the blocked entity (any entity type).
- `user` (required): the user for whom the target is blocked.
- `view` (optional): the view in which the target is blocked (view scope).

Each entity type's classifier instructions define when an entity of that type becomes blocked or not
blocked.
