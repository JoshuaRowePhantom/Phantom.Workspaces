# Copilot instructions

## Test execution

When running tests in this repository:

1. Always use `.\scripts\run-tests.ps1`.
2. Do not call `dotnet test` directly unless the user explicitly requests it.
3. Use `-TestNames` for targeted test runs.
4. Read results from `scripts\test-results.log`.
