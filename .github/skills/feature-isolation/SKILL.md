---
name: feature-isolation
description: Use this skill to implement a feature in an isolated git worktree on a dedicated branch off "features". Handles branch creation, worktree reuse, edit/compile/test work, commit, and fast-forward merge back to "features".
---

# Skill: Feature isolation

Work on a feature in a dedicated branch inside a numbered worktree, then fast-forward `features` when done.

## Step-by-step procedure

### 1. Create a branch from `features`

```powershell
cd C:\dev\phantom.workspaces-design
git checkout features
git checkout -b <branch-name>
```

Choose a short, descriptive branch name for the feature (e.g. `fix/tab-icons`, `feat/default-workspace`).

### 2. Create or reuse a worktree in `worktrees/`

Worktrees are named with plain numbers: `1`, `2`, `3`, etc. Pick the lowest-numbered worktree that is **not** already checked out to a branch.

List existing worktrees:
```powershell
git worktree list
```

If a free worktree exists (path `worktrees/<N>` with no active branch), reuse it by checking out the new branch inside it:
```powershell
Push-Location worktrees\<N>; git checkout <branch-name>; Pop-Location
```

If no free worktree exists, add the next number:
```powershell
$n = (git worktree list | Select-String 'worktrees\\(\d+)' |
      ForEach-Object { [int]$_.Matches[0].Groups[1].Value } |
      Measure-Object -Maximum).Maximum + 1
if (-not $n) { $n = 1 }
git worktree add "worktrees\$n" <branch-name>
```

### 3. Perform the work in the worktree

All edit / compile / test commands run from inside the worktree directory, which is an independent checkout of the repository:

```powershell
Push-Location C:\dev\phantom.workspaces-design\worktrees\<N>
# ... make changes ...
Pop-Location
```

### 4. Compile and test

```powershell
Push-Location C:\dev\phantom.workspaces-design\worktrees\<N>
.\scripts\run-tests.ps1 -Mode fast
Pop-Location
```

Tests must pass before committing. See the `run-tests` skill for full options.

### 5. Commit

```powershell
Push-Location C:\dev\phantom.workspaces-design\worktrees\<N>
git add -A
git commit -m "<conventional commit message>

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
Pop-Location
```

### 6. Merge `features` into the branch

Pull any upstream changes from `features` into the feature branch before merging back:

```powershell
Push-Location C:\dev\phantom.workspaces-design\worktrees\<N>
git merge features --no-edit
Pop-Location
```

Resolve any conflicts, then run tests again to confirm correctness.

### 7. Fast-forward `features` to the feature branch

```powershell
cd C:\dev\phantom.workspaces-design
git checkout features
git merge --ff-only <branch-name>
```

This succeeds only if `features` is a direct ancestor of the feature branch. If step 6 was done correctly, this should always fast-forward cleanly.

---

## Rules

1. Always branch from `features`, never from `main` directly.
2. Worktree names are plain integers (`1`, `2`, …) — never descriptive names.
3. Never create a worktree that is already checked out to a branch held by another worktree.
4. All build and test commands run from inside the worktree directory.
5. Tests must pass before committing (step 4 before step 5).
6. Use `--ff-only` when updating `features` (step 7); if it fails, return to step 6.
7. Do not push any branch unless explicitly instructed.
