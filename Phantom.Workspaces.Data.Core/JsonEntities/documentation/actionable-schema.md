# Actionable interest schema

Marks a `target` entity as **actionable** by a `user` — it requires that user's action. The inbox
view shows all entities a user has the actionable interest on (any entity type). Derived from
`relationship.json`.

## Participants

- `target` (required): the actionable entity (any entity type).
- `user` (required): the user for whom the target is actionable.
- `view` (optional): the view in which the target is actionable (view scope).

Each entity type's classifier instructions define when an entity of that type becomes actionable or
not actionable.
