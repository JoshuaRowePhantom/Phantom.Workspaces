# Phantom.Workspaces

WPF application for running LLM agents and accessing user data.

## Running tests

Use the repository test script instead of invoking `dotnet test` directly.

```powershell
.\scripts\run-tests.ps1
```

Run specific tests by name (matched against `FullyQualifiedName`):

```powershell
.\scripts\run-tests.ps1 -TestNames SchemaPopulatorTests
.\scripts\run-tests.ps1 -TestNames SchemaPopulatorTests,SchemaValidatingDataAccessLayerTests.Update_IsRejected_WhenEntityFailsValidation
```

The script writes output to `scripts\test-results.log` and omits timing information.
