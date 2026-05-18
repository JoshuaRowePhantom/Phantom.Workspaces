# MIME attachment schema

Defines attachment values used for rich content fields (notes, docs, etc.).

## Supported forms

1. Single attachment object:
```json
{ "mime-type": "text/markdown", "content": { "text": "# Title" } }
```
or
```json
{ "mime-type": "text/markdown", "url": "documentation/file.md" }
```
2. Localized attachment map:
```json
{
  "default": { "mime-type": "text/markdown", "url": "documentation/default.md" },
  "fr-FR": { "mime-type": "text/markdown", "content": { "text": "# Bonjour" } }
}
```

## Rules

- `content` requires `mime-type`.
- At least one of `content` or `url` must be present.
