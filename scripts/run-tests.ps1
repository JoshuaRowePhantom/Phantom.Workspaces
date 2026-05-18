param(
    [Parameter()]
    [string] $TestResultsPath = (Join-Path $PSScriptRoot 'test-results.log'),
    [Parameter()]
    [string[]] $TestNames,
    [Parameter()]
    [string] $PerTestHangTimeout = '15s',
    [Parameter()]
    [ValidateSet('full', 'fast')]
    [string] $Mode = 'full',
    [Parameter()]
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$solutionPath = Join-Path $repoRoot 'Phantom.Workspaces.slnx'

Set-Content -Path $TestResultsPath -Value '' -Encoding utf8

$dotnetArgs = @(
    'test',
    $solutionPath,
    '--no-restore',
    '--nologo',
    '-v',
    'minimal'
)

if ($NoBuild)
{
    $dotnetArgs += '--no-build'
}

if ($Mode -eq 'full')
{
    $dotnetArgs += @(
        '--blame-hang',
        '--blame-hang-timeout',
        $PerTestHangTimeout
    )
}

$filterClauses = @()
if ($TestNames -and $TestNames.Count -gt 0)
{
    $nameFilter = ($TestNames | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
    $filterClauses += "($nameFilter)"
}

if ($Mode -eq 'fast')
{
    $filterClauses += '(Category!=SlowGit)'
}

if ($filterClauses.Count -gt 0)
{
    $dotnetArgs += @('--filter', ($filterClauses -join '&'))
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
