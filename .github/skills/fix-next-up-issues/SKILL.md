---
name: fix-next-up-issues
description: Use this skill to fetch all open next-up labelled issues and fix them sequentially, each in its own sub-agent, using the fix-github-issue protocol.
---

# Skill: Fix next-up issues

Fetch every open issue labelled `next-up` and fix them one at a time, each delegated to a `general-purpose` sub-agent following the fix-github-issue protocol. The main agent orchestrates the sequence and keeps its own context light.

## Step 1 — Fetch the list

```powershell
gh issue list --repo JoshuaRowePhantom/Phantom.Workspaces --label next-up --state open --json number,title,url
```

Print the list so the user can see what will be worked on.

## Step 2 — Fix each issue in series

For each issue in the list (in ascending number order):

1. Read the full issue:
   ```powershell
   gh issue view <NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces
   ```

2. Launch a `general-purpose` sub-agent in **background** mode with:
   - The full issue text pasted verbatim into the prompt.
   - The repository path: `c:\dev\phantom.workspaces-llm`.
   - The design doc path: `c:\dev\phantom.workspaces-design\docs\design\`.
   - The full fix-github-issue sub-agent protocol (phases 1–5 from that skill).

3. **Wait for the sub-agent to complete** before starting the next one. Do not run issues in parallel — each fix must be committed and verified before the next begins.

4. Read the sub-agent result. If it reports open questions (i.e., it assigned the issue back to the reporter and stopped), note that and skip to the next issue. Do not block the whole queue on one ambiguous issue.

5. After each sub-agent completes successfully, re-fetch the next-up list to pick up any new issues added since the run started:
   ```powershell
   gh issue list --repo JoshuaRowePhantom/Phantom.Workspaces --label next-up --state open --json number,title,url
   ```
   Work the list from lowest number not yet fixed.

## Step 3 — Report

When no open next-up issues remain (or all remaining ones are blocked on questions), report a summary to the user:
- Issues fixed (with commit references)
- Issues skipped due to open questions (with links)

## Rules

1. Process issues in ascending number order.
2. One issue per sub-agent; one sub-agent at a time.
3. Wait for each sub-agent to finish before launching the next.
4. If a sub-agent reports open questions, skip that issue and continue with the next.
5. Never push. Never batch multiple issues into one commit.
6. Re-read the next-up list after each fix to catch newly added issues.
7. The main agent does not implement anything itself — all code changes happen inside sub-agents.
