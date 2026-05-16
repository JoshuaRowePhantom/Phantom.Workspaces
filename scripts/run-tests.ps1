param(
    [Parameter()]
    [string] $TestResultsPath = (Join-Path $PSScriptRoot 'test-results.log')
)

$ErrorActionPreference = 'Stop'

dotnet test .\Phantom.Workspaces.slnx --no-restore -v minimal *> $TestResultsPath
