# Entity type view schema

Defines presentation and traversal behavior for entities of a type.

## Expected shape

```json
{
  "entity-id": "<uuid>",
  "entity-types": ["entity-type-view", ...],
  "names": [["entity-type-views", "<name>"], ...],
  "fields": [{ "field-path": ["a","b"], "display-format": "optional format" }],
  "sort-order": [{ "field-path": ["x"], "sort-direction": "ascending" }],
  "traverse-relationships": [{ "relationship-type-ids": ["relationship"], "max-depth": 1 }],
  "traversed-entity-display-disposition": "collapsed|expanded",
  "parent-hierarchy-relationships": [{ ... }]
}
```

## Guidance

- `field-path` and `sort-order` should target stable properties.
- Prefer constrained traversal (`relationship-type-ids`, role names, depth) to avoid unbounded graph expansion.
