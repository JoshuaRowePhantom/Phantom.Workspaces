---
name: feature-isolation
description: Use this skill to implement a GitHub issue in an isolated git worktree on a dedicated branch off "features". Covers reading the issue, design, tests, implementation, commit, and fast-forward merge back to "features".
---

# Skill: Feature isolation

Fix a GitHub issue in a dedicated branch inside a numbered worktree, then fast-forward `features` when done.

---

## Step 1 — Read the issue

```powershell
gh issue view <NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces
```

- Read the full body and all comments.
- Note the reporter's login — needed if there are open questions.
- Check for linked design docs under `c:\dev\phantom.workspaces-design\docs\design\`.

## Step 2 — Check for open questions

If the issue is ambiguous or missing information needed to implement safely:

1. Post a comment documenting the questions:
   ```powershell
   gh issue comment <NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces --body "..."
   ```
2. Assign the issue back to the reporter:
   ```powershell
   gh issue edit <NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces --assignee <REPORTER_LOGIN>
   ```
3. **Stop.** Report that questions need answering before implementation can proceed.

If the issue is clear, continue to Step 3.

## Step 3 — Create a branch from `features`

```powershell
cd C:\dev\phantom.workspaces-design
git checkout features
git checkout -b <branch-name>
```

Choose a short, descriptive branch name (e.g. `fix/tab-icons`, `feat/default-workspace`).

## Step 4 — Create or reuse a worktree in `worktrees/`

Worktrees are named with plain numbers: `1`, `2`, `3`, etc. Pick the lowest-numbered worktree that is **not** already checked out to a branch.

List existing worktrees:
```powershell
git worktree list
```

If a free worktree exists (path `worktrees/<N>` with no active branch), reuse it:
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

All subsequent work runs from inside the worktree:
```powershell
Push-Location C:\dev\phantom.workspaces-design\worktrees\<N>
```

## Step 5 — Design and document

Post a comment on the issue summarising:
- Root cause (bugs) or chosen approach (enhancements).
- Design decisions made and alternatives considered.
- List of files that will change.

```powershell
gh issue comment <NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces --body "## Design`n`n..."
```

Update any relevant design doc in `c:\dev\phantom.workspaces-design\docs\design\` if the change affects documented design.

## Step 6 — Plan and document tests

Before writing any code, enumerate the tests to write. For each:
- Name (use `Method_Condition_ExpectedResult` convention)
- What it verifies
- Which test project it belongs to

Post the list as a comment:
```powershell
gh issue comment <NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces --body "## Tests`n`n- ..."
```

## Step 7 — Write tests first

Write the planned tests before any implementation code. Tests may initially fail to compile — that is expected.

Patterns to follow:
- Unit tests → appropriate `*.Tests` project
- Integration tests → `Phantom.Workspaces.Tests\MainWindowIntegrationTests.cs` or a nearby focused file
- Deterministic synchronisation only — no `Task.Delay` or timing-based waits
- Simple test doubles for interfaces; no Moq unless already present in that project

## Step 8 — Implement

Make the minimal changes to make the tests pass. Do not fix unrelated issues.

- No defensive fallback logic — fix the root cause
- No `Debug.WriteLine` calls
- No `dotnet test` — always use the script

## Step 9 — Run tests

```powershell
.\scripts\run-tests.ps1 -Mode fast
```

Read `scripts\test-results.log`. All suites must show `Failed: 0`. Fix any failures before proceeding.

Run the full suite only when the change touches the filesystem or Git repository layers:
```powershell
.\scripts\run-tests.ps1 -Mode full
```

## Step 10 — Commit

```powershell
git add -A
git commit -m "Fix #<NUMBER>: <short description>

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

## Step 11 — Close the issue

```powershell
gh issue close <NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces
```

## Step 12 — Merge `features` into the branch

Pull any upstream changes from `features` before merging back:

```powershell
git merge features --no-edit
```

Resolve any conflicts, then **build and run the full fast test suite**:

```powershell
.\scripts\run-tests.ps1 -Mode fast
```

Read `scripts\test-results.log`. If any tests fail:
1. Diagnose the failure — it may be a merge conflict residual, a test that now clashes with upstream changes, or a regression introduced by the merge.
2. Fix the failing test or code.
3. Run tests again.
4. Repeat until `Failed: 0` across all suites.

**Do not proceed to Step 13 until all tests pass.**

## Step 13 — Fast-forward `features` to the feature branch

```powershell
Pop-Location
cd C:\dev\phantom.workspaces-design
git checkout features
git merge --ff-only <branch-name>
```

This succeeds only if `features` is a direct ancestor of the feature branch. If step 12 was done correctly this should always fast-forward cleanly. If it fails, return to step 12.

---

## Rules

1. Always branch from `features`, never from `main` directly.
2. Worktree names are plain integers (`1`, `2`, …) — never descriptive names.
3. Never create a worktree that is already checked out to a branch held by another worktree.
4. All build and test commands run from inside the worktree directory.
5. Tests must pass before committing (step 9 before step 10).
6. After merging `features` into the branch (step 12), always build and run tests; fix any failures before fast-forwarding.
7. Use `--ff-only` when updating `features` (step 13); if it fails, return to step 12.
7. Do not push any branch unless explicitly instructed.
8. Never commit without passing tests.
9. Never use `dotnet test` directly — always `.\scripts\run-tests.ps1`.
10. Each issue gets its own commit. Do not batch multiple issues into one commit.
11. If there are open questions, assign back to the reporter and stop — do not guess.
12. Always include the `Co-authored-by: Copilot` trailer in every commit message.
