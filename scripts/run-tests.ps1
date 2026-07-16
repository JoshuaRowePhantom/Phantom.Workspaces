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
    # Integration tests exercise real network paths, require a GitHub token with tunnel scope
    # (PHANTOM_INTEGRATION_GITHUB_TOKEN), and may incur dev-tunnel relay costs.  They are excluded
    # by default; pass -IncludeIntegration to opt in.
    [Parameter()]
    [switch] $IncludeIntegration,
    [Parameter()]
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$solutionPath = Join-Path $repoRoot 'Phantom.Workspaces.slnx'

Set-Content -Path $TestResultsPath -Value '' -Encoding utf8

# Remove stale crash dumps from previous runs
Get-ChildItem -Path $repoRoot -Filter '*.dmp' -Recurse -ErrorAction SilentlyContinue |
    Remove-Item -Force

$dotnetArgs = @(
    'test',
    $solutionPath,
    '--no-restore',
    '--nologo',
    '-v',
    'minimal',
    '--logger',
    'trx'
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

# Integration tests require network access, a valid GitHub token with tunnel scope
# (PHANTOM_INTEGRATION_GITHUB_TOKEN), and may incur dev-tunnel relay costs.  Excluded by default.
if (-not $IncludeIntegration -and (-not $TestNames -or $TestNames.Count -eq 0))
{
    $filterClauses += '(Category!=Integration)'
}

if ($IncludeIntegration)
{
    Write-Warning @'
Integration tests are enabled.

WARNING: Integration tests require network access, a valid GitHub token with tunnel scope
(PHANTOM_INTEGRATION_GITHUB_TOKEN), and may incur dev-tunnel relay costs. Press Ctrl+C to abort.
'@
    Start-Sleep -Seconds 5
}

if ($filterClauses.Count -gt 0)
{
    $dotnetArgs += @('--filter', ($filterClauses -join '&'))
}

# Restore once, up front, so the subsequent `dotnet test --no-restore` build works from a
# deterministic project.assets.json. Skipping this (relying on whatever restore happened to run
# last) is what allowed a parallel --no-restore build to intermittently drop a configuration-
# conditional PackageReference and silently skip a whole test project (see #1050). When -NoBuild
# is set the projects are already built, so no restore is needed.
if (-not $NoBuild)
{
    $restoreOutput = & dotnet restore $solutionPath --nologo 2>&1
    if ($LASTEXITCODE -ne 0)
    {
        $restoreOutput | ForEach-Object { $_.ToString() } | Set-Content -Path $TestResultsPath -Encoding utf8
        Write-Host 'FAIL: dotnet restore failed — see test-results.log'
        $restoreOutput | ForEach-Object { Write-Host $_ }
        exit 1
    }
}

$runStart = Get-Date
$rawOutput = & dotnet @dotnetArgs 2>&1
$dotnetExitCode = $LASTEXITCODE

# Write full dotnet output to log before TRX parsing
$cleanOutput = $rawOutput | ForEach-Object {
    $_.ToString().Replace("`r`n", "`n").Replace("`r", "`n")
} | ForEach-Object {
    ($_ -replace '\(\d+(\.\d+)?s\)', '') -replace 'duration:\s*\d+(\.\d+)?s', 'duration: <omitted>'
}

$cleanOutput | Set-Content -Path $TestResultsPath -Encoding utf8

# Find TRX files produced by this run (search recursively in all project TestResults subdirectories)
$trxFiles = Get-ChildItem -Path $repoRoot -Filter '*.trx' -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -ge $runStart }

$failures = @()
$totalExecuted = 0
$totalPassed = 0
$totalFailed = 0
$benignAbortsDetected = $false

# Detect projects that failed to BUILD. Under `dotnet test --no-restore`, a project that fails
# to compile produces no TRX and its entire test assembly is silently skipped while dotnet still
# exits non-zero. Surface this as a distinct, loud failure (rather than a generic "unexpected exit
# code") so a build-time skip can never be mistaken for — or mask — a normal test result (#1050).
$buildErrorLines = $rawOutput |
    ForEach-Object { $_.ToString() } |
    Where-Object { $_ -match ': error ' -or $_ -match 'Build FAILED' }
if ($buildErrorLines)
{
    $errorBlock = ($buildErrorLines | Select-Object -First 30) -join "`n  "
    $failures += "FAIL: BUILD FAILED — one or more projects did not compile; a test project may have been silently skipped:`n  $errorBlock"
}

# Check if any TRX files were produced
if (-not $trxFiles -or $trxFiles.Count -eq 0)
{
    $failures += "FAIL: No TRX output files were produced"
}
else
{
    # Parse each TRX file
    foreach ($trxFile in $trxFiles)
    {
        $xml = [xml](Get-Content $trxFile.FullName -Raw)
        $run = $xml.TestRun
        $trxOutcome = $run.ResultSummary.outcome
        $counters = $run.ResultSummary.Counters
        $total = [int]$counters.total
        $executed = [int]$counters.executed
        $passed = [int]$counters.passed
        $failed = [int]$counters.failed
        $assembly = [System.IO.Path]::GetFileNameWithoutExtension($trxFile.Name)

        $totalExecuted += $executed
        $totalPassed += $passed
        $totalFailed += $failed

        # Apply failure rules
        if ($trxOutcome -eq 'Aborted')
        {
            if ($total -gt 0)
            {
                # Crashed mid-run
                $failures += "FAIL [$assembly]: test host aborted with $total tests started"
            }
            elseif ($total -eq 0)
            {
                # Check if this is the benign empty-match crash
                $emptyMatchPattern = 'Could not find files for the given pattern'
                $isBenignEmptyMatch = $rawOutput | Where-Object { $_ -match $emptyMatchPattern }
                
                if (-not $isBenignEmptyMatch)
                {
                    # Unknown abort reason
                    $failures += "FAIL [$assembly]: test host aborted (reason unknown)"
                }
                else
                {
                    # Track that we found a benign abort
                    $benignAbortsDetected = $true
                }
            }
        }

        if ($trxOutcome -eq 'Failed' -and $failed -eq 0)
        {
            # Test run marked as failed but no individual tests failed - this is a crash
            $failures += "FAIL [$assembly]: test host crashed (outcome=Failed but no test failures)"
        }

        if ($failed -gt 0)
        {
            # Collect failing test names
            $failingTests = $run.Results.UnitTestResult | Where-Object { $_.outcome -eq 'Failed' } | ForEach-Object { $_.testName }
            $testList = $failingTests -join "`n  "
            $failures += "FAIL [$assembly]: $failed test(s) failed:`n  $testList"
        }
    }
}

# Check if no tests were executed
if ($totalExecuted -eq 0)
{
    $failures += "FAIL: No tests were executed"
}

# Check if dotnet exit code is non-zero and unexplained
# If we detected benign aborts and dotnet exited with code 1, that's expected - don't fail
if ($dotnetExitCode -ne 0 -and $failures.Count -eq 0 -and -not ($dotnetExitCode -eq 1 -and $benignAbortsDetected))
{
    $failures += "FAIL: dotnet test exited with code $dotnetExitCode (unexpected)"
}

# Emit summary block
$summary = @"

=== Test Run Summary ===
Executed : $totalExecuted
Passed   : $totalPassed
Failed   : $totalFailed
"@

Write-Host $summary
Add-Content -Path $TestResultsPath -Value $summary -Encoding utf8

# Exit with appropriate code
if ($failures.Count -gt 0)
{
    $failures | ForEach-Object { Write-Host $_ }
    exit 1
}

exit 0
