# Git Schema

Git schema entities represent version control information for git repositories.

## Expected shape

```json
{
  "entity-id": "<stable deterministic id>",
  "entity-types": ["git"],
  "$schema": "https://schemas.workspaces.phantom.to/workspaces/data/core/git.json",
  "names": [["prefix", "repository-name"]],
  "branches": [
    {
      "name": "main",
      "filtered-log": [
        {
          "commit-hash": "abc123def456",
          "author": "John Doe",
          "message": "Initial commit",
          "timestamp": "2024-01-01T00:00:00Z"
        }
      ]
    }
  ],
  "remotes": [
    {
      "name": "origin",
      "url": "https://github.com/user/repo.git"
    }
  ]
}
```

## Defined Types

### git-log-entry
Represents a single commit in git history.

- `commit-hash` (string): The commit SHA hash
- `author` (string): The commit author name
- `message` (string): The commit message
- `timestamp` (string, date-time): When the commit was created

### git-log
An array of git-log-entry objects representing a sequence of commits.

### git-branch
Represents a git branch with its associated commit history.

- `name` (string): The branch name (e.g., "main", "develop")
- `filtered-log` (git-log): The log entries for this branch

### git-remote
Represents a remote repository reference.

- `name` (string): The remote name (e.g., "origin", "upstream")
- `url` (string): The remote repository URL

## Guidance

- Use git entities to track version control context
- git-branch entities include filtered log entries specific to that branch
- git-remote entities store external repository references
- These are typically used as nested properties in higher-level entities like git-worktree
