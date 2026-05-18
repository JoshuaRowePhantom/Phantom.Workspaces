# Note schema

Defines note entities used for documentation and authored content.

## Expected shape

```json
{
  "entity-id": "<uuid>",
  "entity-types": ["note", ...],
  "names": [["documentation", "topic"]],
  "title": { "default": "Title" },
  "content": {
    "default": {
      "mime-type": "text/markdown",
      "url": "documentation/topic.md"
    }
  }
}
```

## Guidance

- `content` is required.
- Use `mime-attachment` schema rules for either inline content or URL-backed content.
