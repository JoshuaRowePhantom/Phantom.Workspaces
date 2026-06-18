# Shell Schema

Shell entities represent executable shell commands with working directory and arguments. They define shell sessions that can be launched on user-computer-profiles.

## Expected Shape

```json
{
  "entity-id": "<generated-guid>",
  "entity-types": ["shell"],
  "$schema": "https://schemas.workspaces.phantom.to/workspaces/data/core/shell.json",
  "names": [
    ["${USER}", "computers", "<computer-name-components>", "shells", "<shell-name>"]
  ],
  "display-name": {
    "default": "PowerShell 7"
  },
  "command": "pwsh",
  "command-arguments": ["-NoLogo"],
  "working-directory": "/home/user/projects"
}
```

## Properties

- `entity-types` (array, required): Must contain "shell"
- `names` (array, required): Shell identifiers following pattern `["${USER}", "computers", <computer-name-components>, "shells", <shell-name>]`
  - The `${USER}` placeholder expands to the current user's name components
  - Computer name components come from the user-computer-profile the shell belongs to
- `display-name` (local-string, optional): Human-readable name shown in UI
- `command` (string, required): Shell command or executable to run
  - Examples: `"pwsh"`, `"bash"`, `"cmd"`, `"zsh"`, `"python"`
- `command-arguments` (array of strings, optional): Arguments passed to the command
  - Examples: `["-NoLogo"]`, `["--norc"]`, `["-i"]`
- `working-directory` (string, optional): Working directory for the shell session
  - If not specified, uses the host's default working directory
  - Can be absolute or relative paths

## Common Shell Configurations

### PowerShell 7
```json
{
  "command": "pwsh",
  "command-arguments": ["-NoLogo"],
  "display-name": { "default": "PowerShell 7" }
}
```

### Bash
```json
{
  "command": "bash",
  "command-arguments": ["-i"],
  "display-name": { "default": "Bash" }
}
```

### Windows Command Prompt
```json
{
  "command": "cmd",
  "command-arguments": ["/K"],
  "display-name": { "default": "Command Prompt" }
}
```

### Python REPL
```json
{
  "command": "python",
  "command-arguments": ["-i"],
  "display-name": { "default": "Python Interactive" }
}
```

### Node.js REPL
```json
{
  "command": "node",
  "command-arguments": [],
  "display-name": { "default": "Node.js" }
}
```

## Naming Pattern

Shell entities are owned by user-computer-profiles and should be named under that profile:

```
["${USER}", "computers", <computer-components>, "shells", <shell-name>]
```

For a computer named `["computers", "hostname", "devbox"]` and user `["users", "dev", "alice"]`:

```
["users", "dev", "alice", "computers", "hostname", "devbox", "shells", "my-shell"]
```

The `${USER}` token in the name prefix will expand to the current user's name components.

## Relationships

Shell entities should have an `owned-by` relationship to their user-computer-profile:

```json
{
  "entity-types": ["relationship", "owned-by"],
  "participants": {
    "owner": "<user-computer-profile-id>",
    "target": "<shell-entity-id>"
  }
}
```

## LLM Configuration Guide

To create a shell entity that an LLM can use:

1. **Determine the host**: Identify which user-computer-profile will run this shell
2. **Choose command**: Select the shell executable (`pwsh`, `bash`, `cmd`, etc.)
3. **Set arguments**: Configure command-line arguments for the shell
4. **Set working directory**: Optionally specify where the shell should start
5. **Create ownership**: Link the shell to its user-computer-profile with an `owned-by` relationship

Example prompt for LLM:
```
Create a PowerShell 7 shell entity for user-computer-profile 
["users", "dev", "alice", "computers", "hostname", "devbox"]
with working directory "/home/alice/projects"
```

The LLM should:
- Generate a new entity-id (GUID)
- Set entity-types to ["shell"]
- Set names to ["users", "dev", "alice", "computers", "hostname", "devbox", "shells", "powershell-7"]
- Set command to "pwsh"
- Set command-arguments to ["-NoLogo"]
- Set working-directory to "/home/alice/projects"
- Set display-name to {"default": "PowerShell 7"}
- Create an owned-by relationship from the user-computer-profile to this shell

## Usage

Shell entities are launched by:
1. User selects a shell from their user-computer-profile
2. Application creates a PTY (pseudo-terminal) connection to the host
3. Shell command is executed with specified arguments and working directory
4. Terminal UI displays input/output stream from the shell session
