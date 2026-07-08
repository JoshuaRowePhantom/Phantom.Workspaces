# Shared: Code Verification Criteria

Apply these criteria when inspecting newly written or modified code and tests. Every criterion is evaluated against the code and tests in the branch under review.

---

## Verification criteria

### Code coverage — fail verification if any of these are violated

- **Feature coverage:** Every feature described in the issue must be represented by code. If the issue describes a behaviour and no code in the repository implements that behaviour, the issue fails verification.
- **Test coverage of described cases:** Every test described or implied in the issue must be written. If the issue lists specific test cases, each must exist as an actual test. If the issue describes conditional behaviour, each branch must be covered by a test.
- **Test coverage of non-trivial logic:** Every public class and every public method must almost always have at least one test, except trivial record types, auto-properties, and pure accessors. Missing test coverage on non-trivial logic is a verification failure.
- **Branch coverage:** Every conditional branch in new code should be represented by a test. Missing branch coverage on newly implemented logic is a verification failure.

### Code quality — fail verification if any of these are present

- **Disabled or quarantined tests:** No tests introduced as part of the implementation may be marked `[Skip]`, `xunit.skip`, commented out, or placed in a category that is excluded from the standard fast test run. Such tests indicate untested code.
- **Unresolved TODOs:** No unresolved TODOs in new code unless each TODO is backed by a filed open issue. A TODO without a corresponding open issue is a verification failure. If TODOs exist and are backed by issues, note the issue numbers in the verification comment.
- **Timing-dependent tests:** No tests that use `Task.Delay`, `Thread.Sleep`, fixed timeouts, or polling loops as their primary synchronization mechanism. Tests must succeed deterministically using event-driven or state-driven synchronization.
- **No fixed dispatcher-pass pumping:** Tests must not synchronise by calling a fixed number of background dispatcher passes (repeated `RunAsync(DispatcherPriority.Background, …)` or equivalent). Use event-driven `TaskCompletionSource` synchronisation instead, anchored to an observable state change.

### Code quality — file a bug but do not fail verification

- **Code duplication:** If the implementation introduces duplicated logic that should be extracted into a shared helper, file a new bug to track the refactor but do not fail verification on this basis alone.

---

## Verify behaviour

Read the implementation files. Assess:

- Does it implement every feature described in the issue?
- Are key fields, logic branches, and edge cases present?
- Are there obvious gaps (e.g. a schema file exists but a required field is absent; a method exists but a described code path is missing)?
- Does new non-trivial logic have corresponding tests for each public class/method and each conditional branch?
- Are there any disabled/quarantined tests, unresolved TODOs without backing issues, or timing-dependent tests?
- Is there any duplicated logic that should be extracted (note for follow-up bug filing, does not fail verification)?

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

## Find and run tests

Search for test methods that exercise the described behaviour:

```powershell
# Search *.Tests projects for test methods related to the implementation
```

Identify the most relevant test class name(s), then run them:

```powershell
.\scripts\run-tests.ps1 -Mode fast -TestNames "<RelevantTestClassName>"
```

Read `scripts\test-results.log`. All suites must show `Failed: 0`.

**Conclude `tests missing`** if no tests validate the core described behaviour.

**Conclude `tests failing`** if relevant tests exist but `Failed:` is non-zero in the results log.
