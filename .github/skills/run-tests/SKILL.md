---
name: run-tests
description: Use this skill when running repository tests. Enforces the approved test script invocation and log-reading workflow.
---

# Skill: Run tests

Use the repository test script for all test execution.

## Commands

Run all tests:

```powershell
.\scripts\run-tests.ps1
```

Run targeted tests by name (`FullyQualifiedName` contains match):

```powershell
.\scripts\run-tests.ps1 -TestNames SchemaPopulatorTests
.\scripts\run-tests.ps1 -TestNames SchemaPopulatorTests,SchemaValidatingDataAccessLayerTests.Update_WhenValidationFails_ErrorMessageIncludesDiagnosticDetails
```

## Rules

1. Always use `.\scripts\run-tests.ps1`.
2. Do not call `dotnet test` directly unless explicitly requested by the user.
3. Review output in `scripts\test-results.log`.
4. The script output omits timing information.
5. Do not prefix test commands with `Set-Location`.
6. Do not prefix the script invocation with `&`.
7. For targeted runs, use `.\scripts\run-tests.ps1 -TestNames ...`.
