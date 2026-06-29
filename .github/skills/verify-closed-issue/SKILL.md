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

Apply these criteria when inspecting the implementation in Step 3. Every criterion is evaluated against the code and tests found on the `features` branch.

### Code coverage — fail verification if any of these are violated

- **Feature coverage:** Every feature described in the issue must be represented by code. If the issue describes a behaviour and no code in the repository implements that behaviour, the issue fails verification.
- **Test coverage of described cases:** Every test described or implied in the issue must be written. If the issue lists specific test cases, each must exist as an actual test. If the issue describes conditional behaviour, each branch must be covered by a test.
- **Test coverage of non-trivial logic:** Every public class and every public method must almost always have at least one test, except trivial record types, auto-properties, and pure accessors. Missing test coverage on non-trivial logic is a verification failure.
- **Branch coverage:** Every conditional branch in new code should be represented by a test. Missing branch coverage on newly implemented logic is a verification failure.

### Code quality — fail verification if any of these are present

- **Disabled or quarantined tests:** No tests introduced as part of the implementation may be marked `[Skip]`, `xunit.skip`, commented out, or placed in a category that is excluded from the standard fast test run. Such tests indicate untested code.
- **Unresolved TODOs:** No unresolved TODOs in new code unless each TODO is backed by a filed open issue. A TODO without a corresponding open issue is a verification failure. If TODOs exist and are backed by issues, note the issue numbers in the verification comment.
- **Timing-dependent tests:** No tests that use `Task.Delay`, `Thread.Sleep`, fixed timeouts, or polling loops as their primary synchronization mechanism. Tests must succeed deterministically using event-driven or state-driven synchronization.

### Code quality — file a bug but do not fail verification

- **Code duplication:** If the implementation introduces duplicated logic that should be extracted into a shared helper, file a new bug to track the refactor but do not fail verification on this basis alone.

---

## Step 1 — Read the issue

```powershell
gh issue view <NUMBER> --repo JoshuaRowePhantom/Phantom.Workspaces --json title,body,comments
```

- Extract the **specified behaviour**: what code should exist, what it should do, and what test cases are described or implied by the issue body and any comments.
- Note the issue title — used as a search keyword in Step 4.

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

Read the located implementation files. Apply the **Code coverage** and **Code quality** criteria from the [Verification criteria](#verification-criteria) section above. Assess:

- Does it implement every feature described in the issue?
- Are key fields, logic branches, and edge cases present?
- Are there obvious gaps (e.g. a schema file exists but a required field is absent; a method exists but a described code path is missing)?
- Does new non-trivial logic have corresponding tests for each public class/method and each conditional branch?
- Are there any disabled/quarantined tests, unresolved TODOs without backing issues, or timing-dependent tests?
- Is there any duplicated logic that should be extracted (note for Step 6 bug filing, does not fail verification)?

**Data-flow issues — end-to-end tracing (apply when the issue describes a value produced in one layer and consumed in another):**

Determine whether the issue involves a data pipeline (e.g. a value set on a model object, written to storage, read back, and displayed). If it does, trace every link in the chain explicitly:

1. **Produced** — identify where the value is set or created (e.g. where is `Timestamp` assigned on a message object?).
2. **Persisted** — confirm the value is written to storage (database schema includes the field; serialisation code writes it).
3. **Reloaded** — confirm the value is read back from storage and mapped onto the in-memory model.
4. **Forwarded** — confirm the value is passed through every intermediate layer to the output/rendering layer.
5. **Rendered** — confirm the rendering layer actually reads and uses the value (not just that rendering code exists).

If any link in the chain is absent or disconnected, **conclude `behaviour not implemented`** — do not declare success because code that looks relevant happens to exist nearby.

For issues that do not involve a data pipeline (e.g. a pure UI layout change, a refactor, an API surface addition with no storage), skip the data-flow trace and apply only the general assessment above.

**Conclude `behaviour not implemented`** if significant described behaviour is absent.

**Conclude `criteria violation`** if any fail-verification criterion from the Verification criteria section is triggered. List every violation found.

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
