---
name: fix-github-issue
description: Use this skill to fix a GitHub issue end-to-end: read the issue, resolve design questions, then delegate the full implementation to a sub-agent to keep the main context light.
---

# Skill: Fix GitHub issue

Fix a GitHub issue completely by delegating implementation to a `general-purpose` sub-agent. The main agent reads the issue and checks for open questions; the sub-agent does all the heavy lifting.

## Step 1 — Read the issue (main agent)

> **Important**: When an issue number is explicitly provided by the caller, use it directly. Do **not** fetch the `next-up` label list or select a different issue. Issue selection is the orchestrator's responsibility; this skill is a pure executor for the assigned issue.

```powershell
gh issue view <NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces
```

- Read the full body and all comments.
- Note the reporter's login — needed if there are open questions.
- Check for linked design docs under `docs/design/`.

## Step 2 — Check for open questions (main agent)

If the issue is ambiguous or missing information needed to implement safely:

1. Post a comment documenting the questions:
   ```powershell
   gh issue comment <NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces --body "..."
   ```
2. Assign the issue back to the reporter:
   ```powershell
   gh issue edit <NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces --assignee <REPORTER_LOGIN>
   ```
3. **Stop.** Report back to the user that questions need answering before implementation can proceed.

If the issue is clear, proceed to Step 3.

## Step 3 — Delegate to a sub-agent

Launch a `general-purpose` sub-agent with a prompt that includes the full issue text and instructs it to follow the implementation protocol below. **Do not implement in the main context** — keep the main context light.

The sub-agent prompt must include:
- The full issue text (copy it in verbatim).
   - The repository path: e.g. `c:\dev\phantom.workspaces-design\worktrees\1`.
   - The design doc path: e.g. `c:\dev\phantom.workspaces-design\worktrees\1\docs\design\`.
- The implementation protocol (phases 1–5 below).

---

## Sub-agent implementation protocol

The sub-agent follows these phases in order:

### Phase 1 — Design and document

Post a comment on the issue summarising:
- Root cause (bugs) or chosen approach (enhancements).
- Design decisions made and alternatives considered.
- List of files that will change.

```powershell
gh issue comment <NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces --body "## Design\n\n..."
```

Update any relevant design doc in `docs/design/` if the change affects documented design.

### Phase 2 — Plan and document tests

Before writing any code, enumerate the tests to write. For each:
- Name (use `Method_Condition_ExpectedResult` convention)
- What it verifies
- Which test project it belongs to

Post the list as a comment:
```powershell
gh issue comment <NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces --body "## Tests\n\n- ..."
```

### Phase 3 — Write tests first

Write the planned tests before any implementation code. Tests may initially fail to compile — that is expected.

Patterns to follow:
- Unit tests → appropriate `*.Tests` project
- Integration tests → `Phantom.Workspaces.Tests\MainWindowIntegrationTests.cs` or a nearby focused file
- Deterministic synchronisation only — no `Task.Delay` or timing-based waits
- Simple test doubles for interfaces; no Moq unless already present in that project

### Phase 4 — Implement

Make the minimal changes to make the tests pass. Do not fix unrelated issues.

- No defensive fallback logic — fix the root cause
- No `Debug.WriteLine` calls
- No `dotnet test` — always use the script

### Phase 5 — Run tests and commit

Always run the fast suite from within the worktree, unless the only changes you are making are .md file changes, in which case there is no need to run tests:

```powershell
c:\dev\phantom.workspaces-design\worktrees\1\scripts\run-tests.ps1 -Mode fast
```

Read `scripts\test-results.log`. All suites must show `Failed: 0`. Fix any failures before committing.

If a test failure appears unrelated to the current changes (e.g. a pre-existing race condition,
timing sensitivity, or infrastructure dependency), treat it as a flaky test:

1. Re-run the test suite once to confirm the failure is non-deterministic:
   ```powershell
   .\scripts\run-tests.ps1 -Mode fast
   ```
2. If it passes on re-run, proceed — the failure was transient.
3. If it fails consistently, investigate whether the current changes are the cause before proceeding.
4. File a next-up GitHub issue for the flaky test regardless of whether you proceed:
   ```powershell
   gh issue create --repo JoshuaRowePhantom/Phantom.Workspaces \
     --title "Bug: flaky test — <TestName>" \
     --label "bug,next-up" \
     --body "## Flaky test report\n\n**Test:** <FullyQualifiedTestName>\n**Failure message:**\n\`\`\`\n<paste error output here>\n\`\`\`\n**Observed during:** fix for issue #<ORIGINAL_NUMBER>\n**Why it appears unrelated:** <explain>"
   ```
5. Add a note to the commit message or the original issue referencing the filed bug.

Do not attempt to fix flaky tests that are outside the scope of the current issue.

Run the full suite only when the change touches the filesystem or Git repository layers:
```powershell
.\scripts\run-tests.ps1 -Mode full
```

Once tests pass, commit and close:
```powershell
git add -A
git commit -m "Fix #<NUMBER>: <short description>

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
gh issue close <NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces
```

**Do NOT push.**

---

## Rules (both main agent and sub-agent)

1. Never push (`git push`) — commit only.
2. Never commit without passing tests.
3. Never use `dotnet test` directly — always `.\scripts\run-tests.ps1`.
4. Each issue gets its own commit. Do not batch multiple issues into one commit.
5. Always include the `Co-authored-by: Copilot` trailer in every commit message.
6. No `Debug.WriteLine`, timing waits, or defensive fallback logic.
7. Tests must be written before or alongside implementation — never skipped.
8. If there are open questions, assign back to the reporter and stop — do not guess.
9. **Never fetch the `next-up` label list.** Work only on the issue number provided. Issue selection is the orchestrator's job.
