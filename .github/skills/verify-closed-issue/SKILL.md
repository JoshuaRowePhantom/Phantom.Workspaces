---
name: verify-closed-issue
description: Use this skill to verify that a single closed GitHub issue is correctly implemented and tested on the features branch. Reports verified, superseded, or failed-verification.
---

# Skill: Verify closed issue

Inspect the `features` branch to confirm that a closed GitHub issue has a correct implementation and passing tests. Apply `failed-verification` or `superseded` labels when gaps are found.

---

## Prerequisites — ensure labels exist

Before running, confirm both labels exist in the repository. Create any that are missing:

```powershell
gh label create failed-verification --repo JoshuaRowePhantom/Phantom.Workspaces --description "Bug failed automated verification" --color "d93f0b"
gh label create superseded --repo JoshuaRowePhantom/Phantom.Workspaces --description "Issue superseded by a later work item" --color "cfd3d7"
```

(These commands are idempotent — they fail silently if the label already exists.)

---

## Step 1 — Read the issue

```powershell
gh issue view <NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces --json title,body,comments
```

- Extract the **specified behaviour**: what code should exist, what it should do, and what test cases are described or implied by the issue body and any comments.
- Note the issue title — used as a search keyword in Step 5.

---

## Step 2 — Find corresponding code on `features`

All inspection is done in `C:\dev\Phantom.Workspaces`, which tracks the `features` branch.

Search for the files, classes, and methods the issue describes:

```powershell
# Find commits referencing this issue
git --no-pager log features --grep="#<NUMBER>" --oneline

# Search for relevant identifiers (class names, method names, file names from the issue)
# Use grep and glob to locate implementation files
```

- Look for the implementation files mentioned or implied by the issue.
- Look for any new types, methods, or schema files the issue specifies.

**Conclude `code not found`** if no relevant implementation can be located that matches the described behaviour.

---

## Step 3 — Verify behaviour

Read the located implementation files. Assess:

- Does it implement the behaviour described in the issue?
- Are key fields, logic branches, and edge cases present?
- Are there obvious gaps (e.g. a schema file exists but a required field is absent; a method exists but a described code path is missing)?

**Conclude `behaviour not implemented`** if significant described behaviour is absent.

---

## Step 4 — Find and run tests

Search for test methods that exercise the described behaviour:

```powershell
# Search *.Tests projects for test methods related to the implementation found in Step 2
```

Identify the most relevant test class name(s), then run them:

```powershell
.\scripts\run-tests.ps1 -Mode fast -TestNames "<RelevantTestClassName>"
```

Read `scripts\test-results.log`. All suites must show `Failed: 0`.

**Conclude `tests missing`** if no tests validate the core described behaviour.

**Conclude `tests failing`** if relevant tests exist but `Failed:` is non-zero in the results log.

---

## Step 5 — Search for superseding work items (only on failure)

If Step 3 or Step 4 concluded failure, search for open or closed issues that supersede this one:

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

## Step 6 — Apply `failed-verification` label (only on failure with no superseder)

If verification failed and no superseding item was found:

1. Post a comment summarising what was checked, what was found, and what is missing:
   ```powershell
   gh issue comment <NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces --body "## Verification failed

   **Checked:** <describe what files/commits/tests were inspected>

   **Found:** <describe what was present>

   **Missing:** <describe the specific gap — code not found / behaviour not implemented / tests missing / tests failing>"
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

   **Verification gap:** <code not found / behaviour not implemented / tests missing / tests failing>

   **Detail:** <describe exactly what was checked and what is absent>"
   ```
4. **Report outcome: `failed-verification`.** Stop here.

---

## Step 7 — Report pass

If Steps 2–4 all pass (code found, behaviour implemented, tests present and green):

- Add no labels.
- **Report outcome: `✅ Verified — implementation found and tests pass`.**

---

## Rules

1. Never modify source code — this skill is read-only.
2. Never push (`git push`).
3. Never use `dotnet test` directly — always `.\scripts\run-tests.ps1`.
4. Always check for superseding issues before applying `failed-verification`.
5. Ensure `failed-verification` and `superseded` labels exist before attempting to apply them (see Prerequisites).
6. When filing a `next-up` bug in Step 6, be precise: name the specific file, method, or test class that is missing.
7. The skill receives exactly one input: the issue number. Issue selection is the caller's responsibility.
