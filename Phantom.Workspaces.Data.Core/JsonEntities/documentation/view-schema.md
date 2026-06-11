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
    {
      "disposition": "collapsed",
      "get-entity": [
        { "entity-type-names": ["agent-session"] }
      ]
    }
  ]
}
```

## Guidance

- `title` and `sub-views` are required.
- `sub-views` can mix reference-style and inline definitions.
- Inline definitions can include `entity-type-views` references to shape rendering.
- Inline data selection uses `get-entity` requests in the workspace DAL request shape.
- `get-entity` items support `entity-id`, `entity-name`, `enumerate-children`, `entity-type-names`, and `relationships-to-return`.
