# JSON Schema Entity Schema

JSON schema entities store reusable JSON Schema definitions that are referenced by entity-type definitions. They provide the validation and structure contracts for entity data.

## Expected Shape

```json
{
  "entity-id": "<generated-guid>",
  "entity-types": ["json-schema"],
  "$schema": "https://schemas.workspaces.phantom.to/workspaces/data/core/json-schema.json",
  "names": [
    ["json-schemas", "https://schemas.workspaces.phantom.to/workspaces/data/core/my-schema.json"],
    ["entity-types", "my-type"]
  ],
  "display-name": {
    "default": "My Schema"
  },
  "schema": {
    "$id": "https://schemas.workspaces.phantom.to/workspaces/data/core/my-schema.json",
    "type": "object",
    "properties": {
      "my-property": {
        "type": "string"
      }
    }
  }
}
```

## Properties

- `entity-types` (array, required): Must contain "json-schema"
- `names` (array, required): One or more identifiers for the schema, following patterns:
  - **JSON schema name**: `["json-schemas", <schema-id>]` where schema-id is typically a URL like the schema's `$id`
  - **Entity type name**: `["entity-types", <type-name>]` when the schema defines an entity type
- `schema` (object, required): The JSON Schema document (Draft 2020-12)
  - Must include `$id` field with the schema's canonical URL
  - Defines validation rules, properties, types, and constraints
- `display-name` (local-string, optional): Human-readable name shown in UI

## Name Patterns

### Schema URL Name
```
["json-schemas", "https://schemas.workspaces.phantom.to/workspaces/data/core/workspace.json"]
```

### Entity Type Name
```
["entity-types", "workspace"]
```

### Both Names Together
Most json-schema entities have both names:
```json
{
  "names": [
    ["json-schemas", "https://schemas.workspaces.phantom.to/workspaces/data/core/workspace.json"],
    ["entity-types", "workspace"]
  ]
}
```

This allows the schema to be referenced by either its URL or its entity-type name.

## Schema Structure

JSON schemas in this system:
- **Follow JSON Schema Draft 2020-12**
- **Include `$id`** — The canonical URL for the schema
- **Reference entity.json** — Most entity schemas use `"allOf": [{"$ref": "entity.json"}]` to inherit base entity properties
- **Define entity-types constraint** — Use `"entity-types": {"type": "array", "contains": {"const": "<type-name>"}}`
- **Document properties** — Each property should have a `description` field

Example minimal entity schema:
```json
{
  "$id": "https://schemas.workspaces.phantom.to/workspaces/data/core/my-type.json",
  "description": "Schema for my-type entities.",
  "allOf": [
    {
      "$ref": "entity.json"
    }
  ],
  "type": "object",
  "properties": {
    "entity-types": {
      "type": "array",
      "contains": {
        "const": "my-type"
      }
    },
    "my-property": {
      "type": "string",
      "description": "Description of my property."
    }
  },
  "required": ["entity-types", "my-property"]
}
```

## Purpose

JSON schema entities:
1. **Validate entity data** — Ensure entities conform to their type's structure
2. **Define entity types** — Establish what properties and constraints entities must satisfy
3. **Enable introspection** — Allow tools and UIs to discover entity structure
4. **Document contracts** — Serve as machine-readable documentation
5. **Support composition** — Schemas can reference other schemas via `$ref`

## Schema References

Schemas can be referenced:
- **By URL**: `{"$ref": "https://schemas.workspaces.phantom.to/workspaces/data/core/entity.json"}`
- **By relative path**: `{"$ref": "entity.json"}` (resolved relative to the current schema's `$id`)
- **By pointer**: `{"$ref": "#/$defs/my-definition"}` (within the same schema)

## Relationships

JSON schemas are associated with entity-type entities:

```json
{
  "entity-types": ["entity-type"],
  "names": [["entity-types", "my-type"]],
  "schema": {
    "$ref": "/JsonSchemas/my-type.json"
  }
}
```

The entity-type entity references the json-schema entity, creating the binding between the type name and its validation rules.

## LLM Configuration Guide

To create a json-schema entity that an LLM can use:

1. **Define the schema document**: Write the JSON Schema with `$id`, `type`, `properties`, `required`
2. **Set dual names**: Include both the `json-schemas` URL name and `entity-types` type name
3. **Inherit from entity.json**: Use `allOf` to include base entity properties
4. **Document thoroughly**: Add `description` fields to the schema and all properties
5. **Create entity-type entity**: Link the schema to an entity-type entity

Example prompt for LLM:
```
Create a JSON schema entity for a "task" entity type with properties: title (required string), completed (boolean), due-date (optional string)
```

The LLM should:
- Generate a new entity-id (GUID)
- Set entity-types to ["json-schema"]
- Set names to include:
  - ["json-schemas", "https://schemas.workspaces.phantom.to/workspaces/data/core/task.json"]
  - ["entity-types", "task"]
- Set schema to a complete JSON Schema document with:
  - `$id`: "https://schemas.workspaces.phantom.to/workspaces/data/core/task.json"
  - `description`: "Schema for task entities."
  - `allOf`: [{"$ref": "entity.json"}]
  - `properties`: title, completed, due-date with types and descriptions
  - `required`: ["entity-types", "title"]
  - `entity-types` constraint: `{"contains": {"const": "task"}}`
- Create a corresponding entity-type entity that references this schema

## Usage

JSON schema entities are used to:
1. **Validate creates and updates** — Reject invalid entity data
2. **Generate UI forms** — Build property editors based on schema
3. **Enable autocomplete** — Suggest valid properties and values
4. **Document structure** — Provide schema introspection for tools
5. **Enforce constraints** — Check required fields, types, formats, patterns

The schema validation layer queries json-schema entities by name to retrieve and apply validation rules.

## Guidance

- Keep `names` and `schema.$id` aligned to the same canonical URI.
- Use this shape for standalone schema entities that are discovered by schema validation.
- Always include `entity-types` name for entity-type schemas to enable type-based lookups.
- Document all properties with clear descriptions for LLM and human understanding.
