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
