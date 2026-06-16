# Workspace Entity Tool Instruction Details

## Tool summary

1. `workspaces_entity_get` reads entities. Input is a full `get-request` object.
2. `workspaces_entity_update` adds/replaces/deletes entities. Input is a full `update-request` object.
3. `workspaces_entity_generate_guid` creates a GUID for explicit `entity-id` assignment.

## High-level entity model

1. Entities are JSON objects.
2. The `entity-types` property on an entity declares which entity types that entity is.
3. Each declared entity type corresponds to a schema definition.
4. Effective validation is schema composition:
   - the base `entity` schema always applies,
   - each declared entity type schema is composed into the validator,
   - unknown or unevaluated properties are rejected by the composed schema rules.
5. Schema validation is enforced by the data access layer during updates.
6. Entity type definitions are stored as entities under `["entity-types", "<type-name>"]`.

## `workspaces_entity_get` shape and semantics

`workspaces_entity_get` accepts:

```json
{
  "get-entity": [
    {
      "entity-id": "optional-guid",
      "entity-name": ["optional", "name", "components"],
      "entity-type-names": ["optional-type-filter"],
      "relationships-to-return": [
        {
          "relationship-type-names": ["optional-relationship-type-filter"],
          "relationship-role-names": ["optional-role-filter"]
        }
      ],
      "properties": ["optional", "json.path", "filters"]
    }
  ],
  "relationships-to-return": [],
  "timestamps": [null],
  "properties": ["optional", "json.path", "filters"]
}
```

Pass arrays and objects as raw JSON values. Do not JSON-encode them into strings (for example, do not send `"get-entity": "[{...}]"`).

Property filtering behavior:

1. `properties` at request level applies to all returned entities.
2. If request-level `properties` is omitted and exactly one entity query is present, that query's `properties` is used.
3. A property path like `"content.default.content.text"` returns only that nested branch in `data`.
4. Unknown paths are ignored.
5. Use `properties` only when filtering many entities, or when you only need a small set of short content properties.
6. For single entity-type reads, prefer omitting `properties` and reading the full entity.

## Entity type names vs. display names (avoid a common mistake)

When filtering by entity type (the `entity-type-names` field in a `get-request`, or a query's
entity-type clause), each value must be the entity type's **canonical name** — a single component
taken from the entity type's `names` property (for example `task`, `note`, `entity-type`) — or the
entity type's **entity id**.

Do **not** use an entity type's **display-name**. The display-name is a human-facing label (for
example `"Work Item"`) and is **not interchangeable** with the entity type's name. Passing a
display-name as an `entity-type-names` value will not match anything.

- Correct: `"entity-type-names": ["task"]`
- Incorrect: `"entity-type-names": ["Task Item"]`  (this is a display-name, not a name)

To discover valid `entity-type-names` values, list the entity types and read their `names`
(see "list all entity types and their names" below); use a value from `names`, not from
`display-name`.

## `workspaces_entity_update` shape and semantics

Use one tool for add, replace, and delete. Pass:

```json
{
  "update-metadata": {
    "comment": {
      "text": "why this change is happening"
    }
  },
  "changes": [
    {
      "entity-id": "optional-guid",
      "concurrency-tag": "required for safe replace/delete",
      "entity-change-mode": "replace",
      "data": { "full entity JSON for add/replace, or null for delete" }
    }
  ]
}
```

Update behaviors:

1. Add: `data` has entity JSON and `entity-id` may be omitted to auto-generate.
2. Replace: send full replacement `data` and current `concurrency-tag`.
3. Delete: set `data` to `null` and send current `concurrency-tag`.

## Relationship reason-note rule

Every relationship you create or replace — any entity whose `data` carries a `participants` object —
**must** include a non-empty `note` property stating *why* the relationship is being applied. Updates
that add or replace a relationship without a `note` reason are rejected. Example:

```json
{
  "entity-types": ["assigned-to", "relationship"],
  "participants": { "target": "<task-guid>", "user": "<user-guid>" },
  "note": "Assigned to the user on the project board."
}
```

## GUID generation rule

Do not call `workspaces_entity_generate_guid` for normal single-entity adds.

Only call `workspaces_entity_generate_guid` when you must pre-assign IDs (for example, creating multiple related entities in one update where they must reference each other).

## Exact query: get one entity type explicitly

To get one entity type named `<type-name>`, call `workspaces_entity_get` with:

```json
{
  "get-entity": [
    {
      "entity-name": ["entity-types", "<type-name>"]
    }
  ]
}
```

## Exact query: get schema for one entity type

To get only the schema for one entity type named `<type-name>`, call `workspaces_entity_get` with:

```json
{
  "get-entity": [
    {
      "entity-name": ["entity-types", "<type-name>"]
    }
  ]
}
```

Then read `entity.data.schema` from the returned entity.

## Exact query: list all entity types and their names

Use this `workspaces_entity_get` request to list each entity type with its display name and entity names:

```json
{
  "get-entity": [
    {
      "entity-type-names": ["entity-type"]
    }
  ],
  "properties": ["display-name", "names"]
}
```

When you later filter by entity type, use a value from each type's `names` (its canonical name),
never its `display-name`.
