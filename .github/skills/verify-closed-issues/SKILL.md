---
name: verify-closed-issues
description: Use this skill to bulk-verify all recently closed GitHub issues that have not yet been verified. Finds unverified closed issues and dispatches a verify-closed-issue subagent for each one.
---

# Skill: Verify closed issues (bulk)

Fetch every closed issue that has not yet received a verification outcome label and verify each one using the `verify-closed-issue` skill as a subagent.

---

## Step 0 — Ensure labels exist

Before running, confirm the required labels exist in the repository. Create any that are missing (these commands are idempotent — they fail silently if the label already exists):

```powershell
gh label create verified --repo JoshuaRowePhantom/Phantom.Workspaces --description "Issue implementation verified on features branch" --color "0e8a16"
gh label create failed-verification --repo JoshuaRowePhantom/Phantom.Workspaces --description "Bug failed automated verification" --color "d93f0b"
gh label create superseded --repo JoshuaRowePhantom/Phantom.Workspaces --description "Issue superseded by a later work item" --color "cfd3d7"
```

---

## Step 1 — Fetch unverified closed issues

List all closed issues that do not have any of the verification outcome labels (`verified`, `failed-verification`, `superseded`), and only include issues closed as fixed (`stateReason: completed`) — skip issues closed as `not_planned`, `duplicate`, or any other reason:

```powershell
$skipLabels = @("verified", "failed-verification", "superseded", "wontfix", "duplicate")

$issues = gh issue list --repo JoshuaRowePhantom/Phantom.Workspaces `
    --state closed `
    --json number,title,labels,stateReason `
    --limit 200 |
    ConvertFrom-Json |
    Where-Object {
        $issueLabels = $_.labels.name
        $_.stateReason -eq "completed" -and
        -not ($skipLabels | Where-Object { $issueLabels -contains $_ })
    }

Write-Host "Found $($issues.Count) unverified closed issues:"
$issues | ForEach-Object { Write-Host "  #$($_.number): $($_.title)" }
```

Print the list so the user can see what will be verified.

---

## Step 2 — Verify each issue via subagent

For each unverified issue (in ascending number order):

1. Launch a `general-purpose` subagent in **background** mode with:
   - A clear statement at the top: **"Verify issue #NUMBER only. Do not fetch the closed-issues list."**
   - The full issue number and title.
   - The repository path: `C:\dev\Phantom.Workspaces-features`.
   - The design repo path: `C:\dev\phantom.workspaces-design`.
   - The full `verify-closed-issue` skill protocol.

2. **Wait for the subagent to complete** before starting the next one (sequential processing keeps GitHub API usage predictable and avoids label-write races).

3. Read the subagent result. Capture the outcome from the `OUTCOME:` line: `verified`, `failed-verification`, or `superseded`.

4. **Apply label, post comment, and file bugs based on the outcome:**

   **If `verified`:**
   ```powershell
   gh issue edit <NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces --add-label "verified"
   gh issue comment <NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces --body "## Verification passed

   **Checked:** <relay the 'Checked' detail from the subagent's OUTCOME block>

   **Result:** Implementation found, all verification criteria satisfied.

   <If the subagent noted duplication bugs:> **Follow-up bugs filed:** #<N>, ..."
   ```

   **If `failed-verification`:**
   ```powershell
   gh issue edit <NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces --add-label "failed-verification"
   gh issue comment <NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces --body "## Verification failed

   **Checked:** <relay from subagent>

   **Found:** <relay from subagent>

   **Missing:** <relay the specific gap from the subagent's OUTCOME block>"
   # File a next-up bug describing the missing implementation:
   gh issue create --repo JoshuaRowePhantom/Phantom.Workspaces `
     --title "Bug: issue #<NUMBER> failed verification — <specific gap from subagent>" `
     --label "bug,next-up" `
     --body "## Missing implementation

   **Original issue:** #<NUMBER> — <title>

   **Verification gap:** <code not found / behaviour not implemented / tests missing>

   **Detail:** <relay exactly what was checked and what is absent from the subagent's OUTCOME block>"
   ```

   **If `superseded`:**
   ```powershell
   gh issue edit <NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces --add-label "superseded"
   ```
   (The informational comment on the subject issue was already posted by `verify-closed-issue` in Step 4 of that skill.)

5. **Report progress immediately** after each subagent completes and labels are applied, before launching the next:

   After a successful verification:
   ```
   ✅ #<NUMBER>: <title>
      Outcome: verified
   ```

   After a failed verification:
   ```
   ❌ #<NUMBER>: <title>
      Outcome: failed-verification — <one-line reason>
   ```

   After a superseded issue:
   ```
   ⏭ #<NUMBER>: <title>
      Outcome: superseded — <one-line reason>
   ```

---

## Step 3 — Report summary

After all subagents complete, report a markdown table to stdout:

```
| Issue | Title | Outcome |
|-------|-------|---------|
| #N    | …     | verified / failed-verification / superseded |
```

If a designated tracking issue exists, post the table as a comment on it:
```powershell
gh issue comment <TRACKING_ISSUE_NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces --body "## Verification run results`n`n<table>"
```

---

## Rules

1. Process issues in ascending number order.
2. One issue per subagent; one subagent at a time (sequential).
3. Wait for each subagent to finish before launching the next.
4. Only verify issues closed as `completed` (fixed). Skip all other close reasons (`not_planned`, `duplicate`, or anything else) — do not attempt to verify them.
5. Skip issues already labelled `verified`, `failed-verification`, or `superseded` — they are already done.
6. Never push. Never modify code.
7. This agent applies all labels and posts all verification-outcome comments — `verify-closed-issue` subagents do not apply labels.
8. If a subagent errors out or cannot determine an outcome, record it as `error` in the summary table and continue with the next issue.
