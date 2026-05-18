# Core schema

Defines shared `$defs` reused by all other schemas.

## Key definitions

- `entity-id`: UUID string.
- `entity-type-id`: type name or type entity id string.
- `entity-name`: either `"single-string"` or `["multi","part","name"]`.
- `entity-reference`: either entity id or entity name.
- `local-string`: `"text"` or `{ "default": "text", "en-US": "...", ... }`.
- `field-path`: non-empty string array.
- `sort-field`: `{ "field-path": [...], "sort-direction": "ascending|descending" }`.

## Usage guidance

Derived schemas should reference these definitions instead of redefining equivalent structures.
