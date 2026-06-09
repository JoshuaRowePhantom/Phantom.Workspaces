# Git Worktree Schema

Git worktree entities represent a git working directory instance, including its filesystem location and current branch state.

## Expected shape

```json
{
  "entity-id": "<stable deterministic id>",
  "entity-types": ["git-worktree"],
  "$schema": "https://schemas.workspaces.phantom.to/workspaces/data/core/git-worktree.json",
  "names": [["workspaces", "my-worktree"]],
  "filesystem-path": "<filesystem-path-entity-id>",
  "current-branch": {
    "name": "main",
    "filtered-log": [
      {
        "commit-hash": "abc123def456",
        "author": "Jane Smith",
        "message": "Feature implementation",
        "timestamp": "2024-01-15T10:30:00Z"
      }
    ]
  }
}
```

## Required Fields

- `filesystem-path`: Reference to a filesystem-path entity identifying the worktree location

## Properties

- `current-branch` (git-branch): The currently checked-out branch with its associated git log

## Guidance

- A git-worktree entity connects a filesystem location with git version control information
- Use this to represent isolated working directories within a workspace
- The filesystem-path should point to the root directory of the git repository
- The current-branch tracks which branch is currently checked out and includes its recent commit history
- Multiple git-worktree entities can reference different filesystem paths or different branches
