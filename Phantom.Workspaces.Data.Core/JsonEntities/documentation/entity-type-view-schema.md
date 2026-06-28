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
  "traverse-relationships": [
    { "relationship-type-ids": ["relationship"], "max-depth": 1 },
    { "relationship-type": "ancestor", "entity-type-names": ["my-type"], "name-prefix-length": 3 }
  ],
  "traversed-entity-display-disposition": "collapsed|expanded",
  "parent-hierarchy-relationships": [{ ... }]
}
```

## Guidance

- `field-path` and `sort-order` should target stable properties.
- Prefer constrained traversal (`relationship-type-ids`, role names, depth) to avoid unbounded graph expansion.
- `relationship-type: "ancestor"` entries synthesize virtual group nodes by extracting the first `name-prefix-length` segments of each matched entity's primary name. No entity is stored; only the relationship object is synthesized. Entities whose primary name length is ≤ `name-prefix-length` are not grouped.
