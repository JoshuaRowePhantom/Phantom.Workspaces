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
- Check for linked design docs under `docs/design`.

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

## Step 3 — Create or reuse a worktree in `worktrees/`

Worktrees are named with plain numbers: `1`, `2`, `3`, etc. Pick the lowest-numbered worktree that has **no associated branch** (i.e. is checked out to `features` or detached HEAD — meaning it is free to use).

List existing worktrees:
```powershell
git worktree list
```

A worktree is **free** when its line shows `(detached HEAD)`. A worktree is **occupied** when it shows a branch name like `[fix/something]`.

If a free worktree exists (path `worktrees/<N>`), reuse it by checking out the new branch inside it:
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

## Step 4 — Create a branch from `features`

The current working directory must stay in **detached HEAD** state at all times — never check out `features` or any feature branch there. Create the new branch directly from the features branch:

Choose a short, descriptive branch name that includes the bug number (e.g. `fix/555-tab-icons`, `feat/104-default-workspace`).

```powershell
cd worktree-directory-name-here
git checkout -b <branch-name> features
```

## Step 5 — Design and document

Post a comment on the issue summarising:
- Root cause (bugs) or chosen approach (enhancements).
- Design decisions made and alternatives considered.
- List of files that will change.

```powershell
gh issue comment <NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces --body "## Design`n`n..."
```

Update any relevant design doc in `docs/design` within the worktree if the change affects documented design.

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

## Step 9 — Build and run tests

### 9a — Build the full solution first (mandatory)

```powershell
dotnet build --no-incremental 2>&1 | Select-String -Pattern "error " | Select-Object -First 20
```

All lines matching `error ` must be zero. **If any build errors appear, stop here and fix them before running tests or committing. Do not proceed past this point with a broken build.**

This step is required because `.\scripts\run-tests.ps1` only compiles projects that have test assemblies. Library projects with no corresponding test project (e.g. `Phantom.Workspaces.Data.Web.Client`) are not compiled by the test runner — compile errors there go undetected unless this build step is run first.

### 9b — Run the fast test suite

```powershell
.\scripts\run-tests.ps1 -Mode fast
```

### 9c — Check for hang dumps before reading results

After the test run completes, before reading `test-results.log`, check for `.dmp` files produced by a crashed or timed-out test host:

```powershell
$dumps = Get-ChildItem -Path . -Recurse -Filter "*.dmp" -ErrorAction SilentlyContinue
if ($dumps) {
    # Invoke the diagnose-hang-dump skill before doing anything else.
    # The skill will analyse the dump, file or update a bug, and delete the dump file.
}
```

If dumps are present, invoke the `diagnose-hang-dump` skill now and record the resulting issue number before continuing.

### 9d — Search for related bugs before rerunning

Read `scripts\test-results.log`. All suites must show `Failed: 0`. Fix any failures before proceeding.

Before attempting any rerun of a failing test, check whether an open bug already documents the failure as a known flake:

```powershell
$failingTest = "<short-name-extracted-from-test-results.log>"
$related = gh issue list --repo JoshuaRowePhantom/Phantom.Workspaces `
    --state open --search "$failingTest" `
    --json number,title | ConvertFrom-Json | Select-Object -First 3
if ($related) {
    Write-Host "Related open issues found:"
    $related | ForEach-Object { Write-Host "  #$($_.number): $($_.title)" }
    # If the failure matches a known flake documented in an existing bug,
    # skip the rerun and proceed, recording the issue number in the commit message.
}
```

If a matching open issue is found that documents the failure as a known flake, skip the rerun and proceed (recording the issue number).

If a test fails, perform root cause analysis before classifying it as transient:

1. **Read the failure output and the affected files.** Determine whether the failure is plausibly related to the current changes by examining the failing test name, error message, and the files touched in this branch.
2. **If the failure could be related to the current changes** — treat it as a real failure. Fix it before proceeding. Do not re-run to escape it.
3. **Only if the failure is clearly unrelated** (different subsystem, a pre-existing known flake, infrastructure crash) — attempt a second run:
   ```powershell
   .\scripts\run-tests.ps1 -Mode fast
   ```
4. **If it passes on the second run** — the failure was transient. File a next-up bug (see below) and proceed.
5. **If it fails again on the second run** — investigate further: read the test code, identify the specific mechanism causing the failure (e.g. missing `await`, shared static state, timer dependency, missing `Dispose`). "Non-deterministic" is not an acceptable root cause. Either fix the test or document the specific mechanism in a filed bug before proceeding.
6. **Only proceed past a failing test** when the root cause is confirmed to be outside the scope of the current change and is documented in a filed bug with a specific diagnosis.

File a next-up bug for any test failure that you proceed past. Before filing, check whether an open bug already exists for this test to avoid duplicates:
```powershell
$existingBug = gh issue list --repo JoshuaRowePhantom/Phantom.Workspaces `
    --state open --label bug --search "<TestMethodShortName>" `
    --json number,title | ConvertFrom-Json |
    Where-Object { $_.title -like "*<TestMethodShortName>*" } |
    Select-Object -First 1
if ($existingBug) {
    # An open bug already exists for this test — skip filing to avoid duplicates
} else {
    gh issue create --repo JoshuaRowePhantom/Phantom.Workspaces `
      --title "Bug: flaky test — <TestName>" `
      --label "bug,next-up" `
      --body "## Flaky test report`n`n**Test:** <FullyQualifiedTestName>`n**Failure message:**`n``````n<paste error output here>`n``````n**Observed during:** fix/feature branch for issue #<ORIGINAL_NUMBER>`n**Why it appears unrelated:** <explain>`n**Root cause diagnosis:** <specific mechanism — e.g. missing await on async setup, shared static counter not reset between tests>"
}
```

Add a note to the original issue comment or commit message referencing the filed bug.

Run the full suite only when the change touches the filesystem or Git repository layers:
```powershell
.\scripts\run-tests.ps1 -Mode full
```

## Step 9e — Verify code quality

Apply the shared code verification criteria from `.github/skills/shared/CODE-VERIFICATION.md`.
Inspect the code you just wrote against every criterion in that file.

- If a **fail-verification criterion** is triggered (feature coverage gap, missing test coverage, disabled test, unresolved TODO without a backing issue, or timing-dependent test), fix the code or tests before proceeding to Step 10.
- Note any **code duplication** for a follow-up bug but do not block on it.
- If a TODO has no backing issue, file one before committing.

## Step 10 — Commit

```powershell
git add -A
git commit -m "Fix #<NUMBER>: <short description>

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

## Step 10b — Validate implementation

Spawn a `verify-closed-issue` subagent for this issue. In the subagent prompt, specify:

- **Working directory:** the current worktree path (e.g. `C:\dev\phantom.workspaces-design\worktrees\<N>`)
- **Branch under review:** the feature branch name (e.g. `fix/…` or `feat/…`)
- Instruct the subagent to `cd` into the worktree and use the branch-diff commands from Step 2 of `verify-closed-issue` to identify commits and file changes unique to this branch before searching for implementation

The subagent will report its outcome as `OUTCOME: verified`, `OUTCOME: failed-verification`, or `OUTCOME: superseded`. **No labels will be applied to the issue** — `verify-closed-issue` is read-only in this context; labels are applied by `verify-closed-issues` after the issue is closed.

- If the subagent reports `OUTCOME: verified` → proceed to Step 11.
- If the subagent reports `OUTCOME: failed-verification` → fix all identified gaps and re-run validation before continuing. **Do not proceed to Step 11 if validation fails.**

## Step 11 — Post a resolution comment

After the commit SHA is known, post a comment that makes the issue thread a self-contained record of the fix:

```powershell
$sha = git rev-parse HEAD
gh issue comment <NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces --body "## Resolution

**Root cause:** <confirmed root cause for bugs; or chosen approach for enhancements>

**Changes:**
- \`<file>\` — <brief description of what changed and why>
- ...

**Deviations from design:** <note any decisions that differ from the Step 5 design comment, or 'None'>

**Commit:** $sha"
```

## Step 12 — Merge `features` into the branch

Pull any upstream changes from `features` before merging back:

```powershell
git merge features --no-edit
```

Resolve any conflicts, then **build the full solution and run the fast test suite**:

```powershell
dotnet build --no-incremental 2>&1 | Select-String -Pattern "error " | Select-Object -First 20
.\scripts\run-tests.ps1 -Mode fast
```

All `error ` lines from the build must be zero. Read `scripts\test-results.log`. If either the build or any tests fail:
1. Diagnose the failure — it may be a merge conflict residual, a test that now clashes with upstream changes, or a regression introduced by the merge.
2. If the failure is unrelated to the merge or your changes (pre-existing flaky test), apply the root cause analysis process from Step 9 — read the failure, confirm it is clearly unrelated, attempt a second run, and document the specific root cause in a filed next-up bug before proceeding. Otherwise fix the failing test or code.
3. Run tests again.
4. Repeat until `Failed: 0` across all suites.

**Do not proceed to Step 13 until all tests pass.**

## Step 13 — Fast-forward `features` to the feature branch

Use `git fetch` to fast-forward the `features` ref without checking it out, so other worktrees remain free to update it:

```powershell
$branchName = git branch --show-current
git fetch . "$($branchName):features"

# Fast-forward succeeded — close the issue now that features is updated
gh issue close <NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces

# Free the worktree by detaching HEAD so it has no associated branch and can be reused
git checkout --detach

Pop-Location
```

`git fetch . <branch>:features` fast-forwards `features` to the tip of the feature branch without a checkout. It fails (non-fast-forward) if `features` is not a direct ancestor — if that happens, return to step 12.

---

## Rules

1. Always branch from `features`, never from `main` directly.
2. Worktree names are plain integers (`1`, `2`, …) — never descriptive names.
3. Never create a worktree that is already checked out to a feature branch held by another worktree.
4. All work (file edits, builds, tests, commits) runs from inside the worktree directory. Never edit files directly in `C:\dev\phantom.workspaces-design`.
5. `C:\dev\phantom.workspaces-design` must always remain in **detached HEAD** state. Never check out `features` or any feature branch there.
6. Tests must pass before committing (step 9 before step 10).
7. After merging `features` into the branch (step 12), always build the full solution and run tests; fix any failures before fast-forwarding.
8. Use `git fetch . "<branch>:features"` to update the `features` ref (step 13); if it fails (non-fast-forward), return to step 12.
9. At the end of step 13, always `git checkout --detach` inside the worktree to free it for reuse (leaves it in detached HEAD state with no associated branch).
10. Do not push any branch unless explicitly instructed.
11. Never commit without passing tests.
12. Never use `dotnet test` directly — always `.\scripts\run-tests.ps1`.
13. Each issue gets its own commit. Do not batch multiple issues into one commit.
14. If there are open questions, assign back to the reporter and stop — do not guess.
15. Always include the `Co-authored-by: Copilot` trailer in every commit message.
16. The full-solution `dotnet build --no-incremental` (Step 9a) must report zero `error ` lines before `run-tests.ps1` is invoked. A passing test run does not substitute for a clean build — library projects with no test assembly will not be compiled by the test runner.
17. After committing (Step 10), validation via a `verify-closed-issue` subagent must pass before posting the resolution comment or closing the issue.
