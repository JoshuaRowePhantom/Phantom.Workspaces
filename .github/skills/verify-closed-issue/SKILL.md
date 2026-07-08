---
name: verify-closed-issue
description: Use this skill to verify that a single closed GitHub issue is correctly implemented and tested on the features branch. Reports verified, superseded, or failed-verification.
---

# Skill: Verify closed issue

Inspect the `features` branch to confirm that a closed GitHub issue has a correct implementation and passing tests. Apply `failed-verification` or `superseded` labels when gaps are found.

---

## Prerequisites — ensure labels exist

Before running, confirm all required labels exist in the repository. Create any that are missing:

```powershell
gh label create failed-verification --repo JoshuaRowePhantom/Phantom.Workspaces --description "Bug failed automated verification" --color "d93f0b"
gh label create superseded --repo JoshuaRowePhantom/Phantom.Workspaces --description "Issue superseded by a later work item" --color "cfd3d7"
gh label create verified --repo JoshuaRowePhantom/Phantom.Workspaces --description "Issue implementation verified" --color "0e8a16"
```

(These commands are idempotent — they fail silently if the label already exists.)

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

All inspection is done in `C:\dev\Phantom.Workspaces-Main`, which tracks the `main` branch.

Search for the files, classes, and methods the issue describes:

```powershell
# Find commits referencing this issue
git --no-pager log main --grep="#<NUMBER>" --oneline

# Search for relevant identifiers (class names, method names, file names from the issue)
# Use grep and glob to locate implementation files
```

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

1. Post a comment on the original issue:
   ```powershell
   gh issue comment <NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces --body "Verification note: this issue appears to be superseded by #<SUPERSEDER> — <brief reason>."
   ```
2. Apply the `superseded` label:
   ```powershell
   gh issue edit <NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces --add-label "superseded"
   ```
3. **Report outcome: `superseded`.** Stop here.

---

## Step 5 — Apply `failed-verification` label (only on failure with no superseder)

If verification failed and no superseding item was found:

1. Post a comment summarising what was checked, what was found, and what is missing:
   ```powershell
   gh issue comment <NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces --body "## Verification failed

   **Checked:** <describe what files/commits/tests were inspected>

   **Found:** <describe what was present>

   **Missing:** <describe the specific gap — code not found / behaviour not implemented / tests missing>"
   ```
2. Apply the label:
   ```powershell
   gh issue edit <NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces --add-label "failed-verification"
   ```
3. File a new `next-up` bug describing what is missing:
   ```powershell
   gh issue create --repo JoshuaRowePhantom/Phantom.Workspaces `
     --title "Bug: issue #<NUMBER> failed verification — <specific gap>" `
     --label "bug,next-up" `
     --body "## Missing implementation

   **Original issue:** #<NUMBER> — <title>

   **Verification gap:** <code not found / behaviour not implemented / tests missing>

   **Detail:** <describe exactly what was checked and what is absent>"
   ```
4. **Report outcome: `failed-verification`.** Stop here.

---

## Step 6 — Report pass

If Steps 2–3 all pass (code found, behaviour implemented, all fail-verification criteria satisfied):

1. Apply the `verified` label:
   ```powershell
   gh issue edit <NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces --add-label "verified"
   ```
2. If any code-duplication instances were noted in Step 3, file a new bug for each:
   ```powershell
   gh issue create --repo JoshuaRowePhantom/Phantom.Workspaces `
     --title "Refactor: duplicated logic in <description>" `
     --label "bug,next-up" `
     --body "## Code duplication

   **Found during verification of:** #<NUMBER>

   **Detail:** <describe exactly which files/methods contain the duplicated logic and what should be extracted>"
   ```
3. Post a comment summarising what was verified and which follow-up bugs were filed (if any):
   ```powershell
   gh issue comment <NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces --body "## Verification passed

   **Checked:** <describe what files/commits/tests were inspected>

   **Result:** Implementation found, all verification criteria satisfied.

   <If duplication bugs were filed:> **Follow-up bugs filed:** #<N>, ..."
   ```
4. **Report outcome: `✅ Verified — implementation found and criteria satisfied`.**

---

## Rules

1. Never modify source code — this skill is read-only.
2. Never push (`git push`).
3. Always check for superseding issues before applying `failed-verification`.
4. Ensure `failed-verification`, `superseded`, and `verified` labels exist before attempting to apply them (see Prerequisites).
5. When filing a `next-up` bug in Step 5, be precise: name the specific file, method, or test class that is missing.
6. The skill receives exactly one input: the issue number. Issue selection is the caller's responsibility.
