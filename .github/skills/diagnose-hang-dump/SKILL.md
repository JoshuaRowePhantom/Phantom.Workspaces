---
name: diagnose-hang-dump
description: Use this read-only skill to analyse a .dmp hang dump produced by a test run. Runs dotnet-dump to collect async state machines, parallel stacks, and hung-thread stacks; synthesises a root-cause hypothesis; searches for a related existing bug (adding a comment if found) or files a new one; archives the dump to dumps\<bugnumber>\; then deletes the original dump file.
---

# Skill: Diagnose hang dump

Analyse a `.dmp` file produced when the test host crashes or times out, file or update a bug with the analysis, then clean up the dump.

---

## Prerequisites

Install `dotnet-dump` once if it is not already present:

```powershell
dotnet tool install -g dotnet-dump
```

---

## Trigger conditions

This skill is invoked when:

- A test run (via `.\scripts\run-tests.ps1`) produces one or more `.dmp` files in `TestResults/` or the working directory, OR
- The user explicitly asks to diagnose a dump file.

---

## Step 1 — Locate the dump file

Find all `.dmp` files under the current directory, most recent first:

```powershell
$dumps = Get-ChildItem -Path . -Recurse -Filter "*.dmp" | Sort-Object LastWriteTime -Descending
$dump = $dumps | Select-Object -First 1
Write-Host "Analysing: $($dump.FullName)"
```

If no dump files are found, report that no `.dmp` files exist and stop.

---

## Step 2 — Collect async state machines

Run `dumpasync` filtered to non-completed state machines to find what is still suspended:

```powershell
$asyncOut = dotnet-dump analyze $dump.FullName --command "dumpasync --completed false; exit" 2>&1
```

---

## Step 3 — Collect parallel stacks

Group all thread stacks by similarity to spot threads waiting on the same thing:

```powershell
$stacksOut = dotnet-dump analyze $dump.FullName --command "parallelstacks; exit" 2>&1
```

---

## Step 4 — Collect thread list and hung-thread stack

List all managed threads, identify the thread(s) with the deepest managed stack or longest wait, then collect the full stack for each hung thread:

```powershell
$threadsOut = dotnet-dump analyze $dump.FullName --command "threads; exit" 2>&1

# For each thread of interest (replace <N> with the thread index from the threads output):
$stackOut = dotnet-dump analyze $dump.FullName --command "setthread <N>; clrstack; exit" 2>&1
```

---

## Step 5 — Synthesise a root-cause hypothesis

From the `dumpasync` output, identify:

- **Which test class/method is stuck** — look for `*Tests` type names in the async state machine names.
- **Which `await` expression it is blocked on** — inspect the `await` field reported for each suspended state machine.
- **Whether multiple threads wait on the same lock or completion source** — look for repeated `TaskCompletionSource`, `SemaphoreSlim`, or `Monitor` entries across threads in `parallelstacks`.

Formulate a concise hypothesis, e.g.:

> `MainWindowIntegrationTests.SomeTest` is suspended awaiting `_semaphore.WaitAsync()`. Two other threads also wait on the same semaphore, suggesting a deadlock caused by a missing `Release()` call on an early-exit path.

---

## Step 6 — Search for a related existing bug

Before filing anything new, search for an open issue that already documents this hang:

```powershell
$keyword = "<stuck-class-or-method>"   # extracted from dumpasync output in Step 5
$existing = gh issue list --repo JoshuaRowePhantom/Phantom.Workspaces `
    --state open --search "$keyword hang OR hang dump OR flaky OR deadlock" `
    --json number,title | ConvertFrom-Json | Select-Object -First 5
```

- **If a matching open issue is found:** add a comment to it. Do **not** file a new issue. Include the following in the comment:
  - The root-cause hypothesis.
  - If the async state machines or parallel stacks show a meaningfully different call pattern from what is already documented in the existing issue: include the complete `dumpasync --completed false` output and the full `threads` + `clrstack` output for every thread of interest, plus the archived dump path (see Step 8 for the path).
  - If the pattern is identical to prior occurrences: note "Pattern consistent with prior occurrence — no new call stacks" and omit the full dump output. Still include the archived dump path.
  - Proceed to Step 8 using `$bugNumber = $existing[0].number`.
- **If no matching issue is found:** proceed to Step 7.

---

## Step 7 — File a new bug (only if no existing issue was found)

Include the complete `dumpasync --completed false` output and the full `threads` + `clrstack` output for every thread of interest — not a truncated summary. The archived dump path (see Step 8) must also appear in the body.

```powershell
$newIssue = gh issue create --repo JoshuaRowePhantom/Phantom.Workspaces `
    --title "Hang: <stuck-class> stuck at <await-expression>" `
    --label "bug,next-up" `
    --body "## Hang dump analysis

**Test:** <FullyQualifiedTestName>
**Dump file:** <filename>
**Archived dump:** ``<full path to dumps\<bugnumber>\<filename>.dmp>``
**Observed during:** <branch / issue context>

### Async state machines (dumpasync --completed false)

``````
$asyncOut
``````

### Parallel stacks

``````
$stacksOut
``````

### Thread list and hung-thread stacks

``````
$threadsOut
``````

``````
$stackOut
``````

### Root cause hypothesis

<synthesised from the above — which await is blocked and why>
" | ConvertFrom-Json
$bugNumber = $newIssue.number
```

---

## Step 8 — Archive the dump and update .gitignore

After the bug number is known, copy the dump to a dedicated directory keyed by the bug number, then ensure `dumps/` is in `.gitignore`:

```powershell
$dumpArchiveDir = Join-Path (git rev-parse --show-toplevel) "dumps\$bugNumber"
New-Item -ItemType Directory -Force -Path $dumpArchiveDir | Out-Null
Copy-Item $dump.FullName -Destination $dumpArchiveDir
$archivedDumpPath = Join-Path $dumpArchiveDir (Split-Path $dump.FullName -Leaf)
Write-Host "Archived dump to: $archivedDumpPath"

$gitignorePath = Join-Path (git rev-parse --show-toplevel) ".gitignore"
$gitignoreContent = Get-Content $gitignorePath -Raw -ErrorAction SilentlyContinue
if ($gitignoreContent -notmatch '(?m)^dumps/$') {
    Add-Content $gitignorePath "`ndumps/"
    Write-Host "Added dumps/ to .gitignore"
}
```

If the issue was filed in Step 7, update the body to replace the `<full path to dumps\…>` placeholder with `$archivedDumpPath` (the issue body template already contains the placeholder; use `gh issue edit` to patch it if needed).

---

## Step 9 — Delete the original dump file

Dump files can be hundreds of MB. Always delete the original after archiving to avoid disk pressure:

```powershell
Remove-Item $dump.FullName -Force
Write-Host "Deleted $($dump.FullName)"
```
