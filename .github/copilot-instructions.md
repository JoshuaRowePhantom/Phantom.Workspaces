# Copilot instructions

## Test execution

When running tests in this repository:

1. Always use `.\scripts\run-tests.ps1`.
2. Do not call `dotnet test` directly unless the user explicitly requests it.
3. Use `-TestNames` for targeted test runs.
4. Read results from `scripts\test-results.log`.

## Sensitive information

1. NEVER put any sensitive information, such as API keys, usernames, or passwords, in files stored in this repository.
2. If sensitive information must be stored locally, ensure the file is gitignored and never staged with `git add`.
