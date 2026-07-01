---
name: verify-closed-issue
description: Use this skill to verify that a single closed GitHub issue is correctly implemented and tested on the features branch. Reports verified, superseded, or failed-verification.
---

# Skill: Verify closed issue

Inspect the `features` branch to confirm that a closed GitHub issue has a correct implementation and passing tests. Reports the outcome to the caller — does **not** apply labels or post verification-outcome comments.

> **Note for callers:** This skill is read-only with respect to the subject issue (except for informational superseder comments in Step 4). All label writes and verification-outcome comments are the responsibility of the caller (see `verify-closed-issues` for the bulk caller that handles labelling).

---

## Verification criteria

Apply the shared code verification criteria:
see `.github/skills/shared/CODE-VERIFICATION.md`

---

## Step 1 — Read the issue

```powershell
gh issue view <NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces --json title,body,comments,stateReason
```

- Extract the **specified behaviour**: what code should exist, what it should do, and what test cases are described or implied by the issue body and any comments.
- Note the issue title — used as a search keyword in Step 4.
- **Check `stateReason`.** If it is not `"completed"` (e.g. `"duplicate"` or `"not_planned"`), **stop immediately** — do not verify and do not apply any labels. Report: `Skipped — issue closed as <stateReason>, not as fixed.`

---

## Step 2 — Find corresponding code on `features`

This step handles two cases:

**Default case — inspecting the `features` tip in `C:\dev\Phantom.Workspaces`:**

All inspection is done in `C:\dev\Phantom.Workspaces`, which tracks the `features` branch.

Search for the files, classes, and methods the issue describes:

```powershell
# Find commits referencing this issue
git --no-pager log features --grep="#<NUMBER>" --oneline

# Search for relevant identifiers (class names, method names, file names from the issue)
# Use grep and glob to locate implementation files
```

**Worktree/branch case — invoked from a feature-isolation worktree:**

When the caller supplies a worktree path and branch name, `cd` into the worktree first, then use these commands to identify what is unique to the branch before searching for implementation:

```powershell
# Show commits unique to this branch (reachable from HEAD but not from features)
git --no-pager log features..HEAD --oneline --stat

# Show which files were changed on this branch relative to the merge base
git --no-pager diff --name-status features...HEAD

# Full diff if detail is needed
git --no-pager diff features...HEAD
```

The triple-dot form (`features...HEAD`) diffs against the merge base, so results are correct even if `features` has received new commits since this branch was created. The double-dot form (`features..HEAD`) lists commits by reachability — commits in HEAD not reachable from `features`.

- Look for the implementation files mentioned or implied by the issue.
- Look for any new types, methods, or schema files the issue specifies.

**Conclude `code not found`** if no relevant implementation can be located that matches the described behaviour.

---

## Step 3 — Verify behaviour

Apply the **Verify behaviour** section from `.github/skills/shared/CODE-VERIFICATION.md`, using the implementation files found in Step 2.

**Conclude `behaviour not implemented`** if significant described behaviour is absent.

**Conclude `criteria violation`** if any fail-verification criterion is triggered. List every violation found.

---

## Step 4 — Search for superseding work items (only on failure)

If Step 3 concluded failure, search for open or closed issues that supersede this one:

```powershell
gh issue list --repo JoshuaRowePhantom/Phantom.Workspaces --state all --search "supersedes #<NUMBER>"
gh issue list --repo JoshuaRowePhantom/Phantom.Workspaces --state all --search "replaces #<NUMBER>"
gh issue list --repo JoshuaRowePhantom/Phantom.Workspaces --state all --search "<keyword from issue title>"
```

A credible superseding issue is one that:
- References this issue number (e.g. "supersedes #N", "replaces #N", "see #N"), **or**
- Describes a broader design that would make this issue's implementation unnecessary or obsolete.

**If a credible superseding issue is found:**

1. Post an informational comment on the original issue:
   ```powershell
   gh issue comment <NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces --body "Verification note: this issue appears to be superseded by #<SUPERSEDER> — <brief reason>."
   ```
2. **Report outcome and stop:**
   ```
   OUTCOME: superseded — superseded by #<SUPERSEDER>
   ```

---

## Step 5 — Report failed-verification (only on failure with no superseder)

If verification failed and no superseding item was found, report the outcome to the caller with enough detail for them to act:

```
OUTCOME: failed-verification

**Checked:** <describe what files/commits/tests were inspected>

**Found:** <describe what was present>

**Missing:** <describe the specific gap — code not found / behaviour not implemented / tests missing>
```

Stop here. The caller is responsible for applying labels, posting a verification-outcome comment, and filing a follow-up bug.

---

## Step 6 — Report pass

If Steps 2–3 all pass (code found, behaviour implemented, all fail-verification criteria satisfied):

1. If any code-duplication instances were noted in Step 3, file a new bug for each (these are new artifacts, not labels on the subject issue):
   ```powershell
   gh issue create --repo JoshuaRowePhantom/Phantom.Workspaces `
     --title "Refactor: duplicated logic in <description>" `
     --label "bug,next-up" `
     --body "## Code duplication

   **Found during verification of:** #<NUMBER>

   **Detail:** <describe exactly which files/methods contain the duplicated logic and what should be extracted>"
   ```
2. **Report outcome:**
   ```
   OUTCOME: verified

   **Checked:** <describe what files/commits/tests were inspected>

   **Result:** Implementation found, all verification criteria satisfied.

   <If duplication bugs were filed:> **Follow-up bugs filed:** #<N>, ...
   ```

---

## Rules

1. Never modify source code — this skill is read-only.
2. Never push (`git push`).
3. Always check for superseding issues before reporting `failed-verification`.
4. When reporting `failed-verification`, include enough detail (checked files, found items, specific gap) for the caller to write an accurate comment and file a useful bug.
5. The skill receives exactly one input: the issue number. Issue selection is the caller's responsibility.
6. Never apply labels or post verification-outcome comments — these are the caller's responsibility.
