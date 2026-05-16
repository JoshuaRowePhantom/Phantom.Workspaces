param(
    [Parameter()]
    [string] $TestResultsPath = (Join-Path $PSScriptRoot 'test-results.log'),
    [Parameter()]
    [string[]] $TestNames
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$solutionPath = Join-Path $repoRoot 'Phantom.Workspaces.slnx'

$dotnetArgs = @(
    'test',
    $solutionPath,
    '--no-restore',
    '--nologo',
    '-v',
    'minimal'
)

if ($TestNames -and $TestNames.Count -gt 0)
{
    $filter = ($TestNames | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
    $dotnetArgs += @('--filter', $filter)
}

$rawOutput = & dotnet @dotnetArgs 2>&1
$exitCode = $LASTEXITCODE

$cleanOutput = $rawOutput | ForEach-Object {
    $_.ToString().Replace("`r`n", "`n").Replace("`r", "`n")
} | ForEach-Object {
    ($_ -replace '\(\d+(\.\d+)?s\)', '') -replace 'duration:\s*\d+(\.\d+)?s', 'duration: <omitted>'
}

$cleanOutput | Set-Content -Path $TestResultsPath -Encoding utf8

exit $exitCode
