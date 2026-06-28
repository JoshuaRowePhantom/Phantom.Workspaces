param(
    [Parameter()]
    [string] $TestResultsPath = (Join-Path $PSScriptRoot 'test-results.log'),
    [Parameter()]
    [string[]] $TestNames,
    [Parameter()]
    [string] $PerTestHangTimeout = '90s',
    [Parameter()]
    [ValidateSet('full', 'fast')]
    [string] $Mode = 'full',
    [Parameter()]
    [switch] $IncludeWebView,
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

$dotnetArgs += @(
    '--blame-hang',
    '--blame-hang-timeout',
    $PerTestHangTimeout
)

$filterClauses = @()
if ($TestNames -and $TestNames.Count -gt 0)
{
    $nameFilter = ($TestNames | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
    $filterClauses += "($nameFilter)"
}

if ($Mode -eq 'fast')
{
    $filterClauses += '(Category!=SlowGit)'
    $filterClauses += '(Category!=SlowDocker)'
}

# WebView integration tests require a real desktop browser host (native WebView2) and are
# excluded by default. Run them with -IncludeWebView (or by targeting them with -TestNames)
# whenever you touch WebView/browser-hosted rendering code.
if (-not $IncludeWebView -and (-not $TestNames -or $TestNames.Count -eq 0))
{
    $filterClauses += '(Category!=WebView)'
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
