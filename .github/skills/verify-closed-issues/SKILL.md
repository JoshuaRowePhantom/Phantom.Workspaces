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

List all closed issues that do not have any of the verification outcome labels (`verified`, `failed-verification`, `superseded`), and skip issues labelled `next-up`, `wontfix`, or `duplicate` (not yet implemented or intentionally skipped):

```powershell
$skipLabels = @("verified", "failed-verification", "superseded", "next-up", "wontfix", "duplicate")

$issues = gh issue list --repo JoshuaRowePhantom/Phantom.Workspaces `
    --state closed `
    --json number,title,labels `
    --limit 200 |
    ConvertFrom-Json |
    Where-Object {
        $issueLabels = $_.labels.name
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
   - The repository path: `C:\dev\Phantom.Workspaces`.
   - The design repo path: `C:\dev\phantom.workspaces-design`.
   - The full `verify-closed-issue` skill protocol.

2. **Wait for the subagent to complete** before starting the next one (sequential processing keeps GitHub API usage predictable and avoids label-write races).

3. Read the subagent result. Capture the outcome: `verified`, `failed-verification`, or `superseded`.

4. **Report progress immediately** after each subagent completes, before launching the next:

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
4. Skip issues labelled `next-up`, `wontfix`, or `duplicate` — do not attempt to verify them.
5. Skip issues already labelled `verified`, `failed-verification`, or `superseded` — they are already done.
6. Never push. Never modify code.
7. The main agent does not verify anything itself — all inspection and labelling happens inside subagents.
8. If a subagent errors out or cannot determine an outcome, record it as `error` in the summary table and continue with the next issue.
