# Testing workflow

Always run tests through the repository script:

```powershell
.\scripts\run-tests.ps1
```

## Run fast vs full suites

Use fast mode for day-to-day iteration (excludes tests marked `Category=SlowGit`):

```powershell
.\scripts\run-tests.ps1 -Mode fast
```

Use full mode to include all tests, including slow Git tests:

```powershell
.\scripts\run-tests.ps1 -Mode full
```

## Run targeted tests

Pass one or more names to `-TestNames`. Each name is matched against `FullyQualifiedName`.

```powershell
.\scripts\run-tests.ps1 -TestNames SchemaPopulatorTests
.\scripts\run-tests.ps1 -TestNames SchemaPopulatorTests,SchemaValidatingDataAccessLayerTests.Update_WhenValidationFails_ErrorMessageIncludesDiagnosticDetails
```

## Output

- Results are written to `scripts\test-results.log`.
- Timing information is stripped from the log output to keep runs diff-friendly and easier to diagnose.
