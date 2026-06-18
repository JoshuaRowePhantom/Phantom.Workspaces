# Filesystem Path Schema

Filesystem path entities map workspace concepts to local or remote filesystem paths. They bridge abstract workspace entities to concrete locations on computers.

## Expected Shape

```json
{
  "entity-id": "<generated-guid>",
  "entity-types": ["filesystem-path"],
  "$schema": "https://schemas.workspaces.phantom.to/workspaces/data/core/filesystem-path.json",
  "names": [
    ["filesystem-paths", "<workspace-name-components>", "path"]
  ],
  "path": "/home/alice/projects/my-app",
  "computer-reference": ["computers", "hostname", "devbox"]
}
```

## Properties

- `entity-types` (array, required): Must contain "filesystem-path"
- `names` (array, required): Path identifiers following workspace naming
- `path` (string, required): The filesystem path on the computer
  - Can be absolute: `"/home/alice/projects/my-app"`, `"C:\\Users\\Alice\\Projects\\my-app"`
  - Can be relative (interpreted relative to a context)
- `computer-reference` (array, required): Reference to the computer entity where this path exists
  - Example: `["computers", "hostname", "devbox"]`

## Purpose

Filesystem paths serve as:

1. **Workspace locations** — Map workspace entities to directories
2. **Repository paths** — Link git repositories to cloned locations
3. **File references** — Reference specific files or directories
4. **Computer-specific paths** — Each computer may have different paths for the same workspace

## Relationships

Filesystem paths are typically linked to workspaces or repositories via relationships:

### Workspace Path
```json
{
  "entity-types": ["relationship", "path"],
  "participants": {
    "source": "<workspace-entity-id>",
    "target": "<filesystem-path-entity-id>"
  }
}
```

### Repository Path
```json
{
  "entity-types": ["relationship", "path"],
  "participants": {
    "source": "<git-repository-entity-id>",
    "target": "<filesystem-path-entity-id>"
  }
}
```

## Naming Pattern

Filesystem paths are typically named after the workspace or entity they represent:

```
["filesystem-paths", <workspace-name-components>, "path"]
```

For a workspace named `["workspaces", "my-app"]`:

```
["filesystem-paths", "workspaces", "my-app", "path"]
```

## Multiple Computers

The same workspace can have different paths on different computers:

```json
[
  {
    "entity-id": "path-1",
    "path": "/home/alice/projects/my-app",
    "computer-reference": ["computers", "hostname", "laptop"]
  },
  {
    "entity-id": "path-2",
    "path": "C:\\Users\\Alice\\Projects\\my-app",
    "computer-reference": ["computers", "hostname", "desktop"]
  }
]
```

Both paths point to the same workspace but on different machines.

## LLM Configuration Guide

To create a filesystem path entity that an LLM can use:

1. **Identify the target entity**: Determine what workspace, repository, or resource this path represents
2. **Determine the computer**: Identify which computer this path exists on
3. **Set the path**: Provide the absolute or relative filesystem path
4. **Create relationship**: Link the path to its target entity with a `path` relationship

Example prompt for LLM:
```
Create a filesystem path entity for workspace ["workspaces", "my-app"] 
located at "/home/alice/projects/my-app" on computer ["computers", "hostname", "devbox"]
```

The LLM should:
- Generate a new entity-id (GUID)
- Set entity-types to ["filesystem-path"]
- Set names to ["filesystem-paths", "workspaces", "my-app", "path"]
- Set path to "/home/alice/projects/my-app"
- Set computer-reference to ["computers", "hostname", "devbox"]
- Create a "path" relationship from the workspace to this path entity

## Usage

Filesystem paths are used to:
1. **Resolve workspace locations** — Find where a workspace is stored
2. **Navigate to directories** — Open terminals or file browsers at specific paths
3. **Configure tool roots** — Tell git, build tools, or IDEs where files are located
4. **Computer-specific operations** — Perform filesystem operations on the correct machine

The system queries filesystem-path entities by workspace and computer to find the correct local path.
