---
name: diagnose-hang-dump
description: Use this read-only skill to analyse a .dmp hang dump produced by a test run. Runs dotnet-dump to collect async state machines, parallel stacks, and hung-thread stacks; synthesises a root-cause hypothesis; searches for a related existing bug (adding a comment if found) or files a new one; then deletes the dump file.
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

- **If a matching open issue is found:** add a comment to it with the dump analysis (async state machines output, parallel stacks output, and root-cause hypothesis). Do **not** file a new issue. Proceed to Step 8.
- **If no matching issue is found:** proceed to Step 7.

---

## Step 7 — File a new bug (only if no existing issue was found)

```powershell
gh issue create --repo JoshuaRowePhantom/Phantom.Workspaces `
    --title "Hang: <stuck-class> stuck at <await-expression>" `
    --label "bug,next-up" `
    --body "## Hang dump analysis

**Test:** <FullyQualifiedTestName>
**Dump file:** <filename> (deleted after analysis)
**Observed during:** <branch / issue context>

### Async state machines (dumpasync --completed false)

``````
$asyncOut
``````

### Parallel stacks

``````
$stacksOut
``````

### Root cause hypothesis

<synthesised from the above — which await is blocked and why>
"
```

---

## Step 8 — Delete the dump file

Dump files can be hundreds of MB. Always delete after analysis to avoid disk pressure:

```powershell
Remove-Item $dump.FullName -Force
Write-Host "Deleted $($dump.FullName)"
```
