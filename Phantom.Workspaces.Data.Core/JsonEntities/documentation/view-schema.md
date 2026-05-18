# View schema

Defines view entities that drive query-backed navigation surfaces.

## Expected shape

```json
{
  "entity-id": "<uuid>",
  "entity-types": ["view", ...],
  "names": [["views", "<name>"]],
  "title": { "default": "View title" },
  "sub-views": [
    { "view-entity-id": ["views", "other"], "disposition": "expanded" },
    { "disposition": "collapsed", "query": { "clauses": [] } }
  ]
}
```

## Guidance

- `title` and `sub-views` are required.
- `sub-views` can mix reference-style and inline definitions.
- Inline definitions can include `entity-type-views` references to shape rendering.
