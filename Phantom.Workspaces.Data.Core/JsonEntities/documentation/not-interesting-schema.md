# Not-interesting interest schema

Marks a `target` entity as **not interesting** so it is filtered out of query results and views
unless "show hidden items" is selected. It is an interest type (a relationship) derived from
`relationship.json`.

## Participants

- `target` (required): the entity marked not interesting (any entity type).
- `user` (optional): the user for whom the target is not interesting (user scope).
- `view` (optional): the view in which the target is not interesting (view scope).

When neither `user` nor `view` is present, the interest applies globally.
