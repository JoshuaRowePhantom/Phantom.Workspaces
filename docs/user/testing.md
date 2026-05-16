# Testing workflow

Always run tests through the repository script:

```powershell
.\scripts\run-tests.ps1
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
