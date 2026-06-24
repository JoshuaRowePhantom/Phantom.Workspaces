<#
.SYNOPSIS
    End-to-end build/install verification for Phantom.Workspaces in a throwaway sandbox.

.DESCRIPTION
    Exercises the whole publish -> package -> install -> update -> uninstall flow against the real
    packaged executable, without creating a GitHub release or touching the developer's real
    per-user install. The managed install root is redirected to a temporary sandbox via the
    --install-root override, so nothing under %LOCALAPPDATA% is modified.

    Each stage prints PASS/FAIL; the script exits non-zero on the first failure and always cleans
    up the sandbox (unless -KeepSandbox is given).

.PARAMETER RuntimeIdentifier
    The publish runtime identifier. Defaults to the current process architecture.

.PARAMETER FastPublish
    Skip ReadyToRun and single-file compression for quicker iteration.

.PARAMETER Sandbox
    The managed install root to use. Defaults to a new temporary directory.

.PARAMETER SkipUpdate
    Stop after the install stage (skip the simulated update).

.PARAMETER KeepSandbox
    Do not delete the sandbox on completion (for inspection).
#>
[CmdletBinding()]
param(
    [string] $RuntimeIdentifier,
    [switch] $FastPublish,
    [string] $Sandbox,
    [switch] $SkipUpdate,
    [switch] $KeepSandbox
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$guiProject = Join-Path $repoRoot 'Phantom.Workspaces\Phantom.Workspaces.csproj'
$packagingScript = Join-Path $repoRoot 'packaging\zip\New-ReleaseZip.ps1'

function Resolve-Rid
{
    if ($RuntimeIdentifier) { return $RuntimeIdentifier }
    switch ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture)
    {
        'Arm64' { 'win-arm64' }
        default { 'win-x64' }
    }
}

$failures = 0
function Assert-That([bool] $condition, [string] $message)
{
    if ($condition)
    {
        Write-Host "PASS  $message" -ForegroundColor Green
    }
    else
    {
        Write-Host "FAIL  $message" -ForegroundColor Red
        $script:failures++
        throw "Assertion failed: $message"
    }
}

function Invoke-Exe([string] $exePath, [string[]] $arguments)
{
    # Phantom.Workspaces.exe is a GUI-subsystem (WinExe) process, so calling it with the call
    # operator returns immediately. Start-Process -Wait blocks until the headless management mode
    # finishes and exposes the real exit code.
    $process = Start-Process -FilePath $exePath -ArgumentList $arguments -Wait -PassThru -NoNewWindow
    return $process.ExitCode
}

function Invoke-Publish([string] $version, [string] $rid, [string] $outputDirectory)
{
    $publishArgs = @(
        'publish', $guiProject,
        '-c', 'Release',
        '-r', $rid,
        "-p:Version=$version",
        '-o', $outputDirectory
    )
    if ($FastPublish)
    {
        $publishArgs += @('-p:PublishReadyToRun=false', '-p:EnableCompressionInSingleFile=false')
    }

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0)
    {
        throw "dotnet publish failed for version $version ($rid)."
    }
}

function Get-LinkTargetVersion([string] $linkPath)
{
    $item = Get-Item -LiteralPath $linkPath -ErrorAction Stop
    $target = $item.Target
    if (-not $target) { return $null }
    if ($target -is [array]) { $target = $target[0] }
    return Split-Path -Leaf $target.TrimEnd('\', '/')
}

$rid = Resolve-Rid
if (-not $Sandbox)
{
    $Sandbox = Join-Path ([System.IO.Path]::GetTempPath()) "phantom-install-test-$([Guid]::NewGuid().ToString('N'))"
}

$staging = Join-Path ([System.IO.Path]::GetTempPath()) "phantom-install-staging-$([Guid]::NewGuid().ToString('N'))"
$publishA = Join-Path $staging 'publish-A'
$publishB = Join-Path $staging 'publish-B'
$packageOut = Join-Path $staging 'packages'
$versionA = '0.0.1'
$versionB = '0.0.2'

try
{
    New-Item -ItemType Directory -Force -Path $staging, $Sandbox | Out-Null
    Write-Host "Sandbox install root: $Sandbox"
    Write-Host "Runtime identifier:   $rid"

    # Stage 1: Publish version A.
    Write-Host "`n== Stage 1: publish $versionA =="
    Invoke-Publish -version $versionA -rid $rid -outputDirectory $publishA
    $exeA = Join-Path $publishA 'Phantom.Workspaces.exe'
    Assert-That (Test-Path -LiteralPath $exeA) "Published executable exists ($versionA)."

    # Stage 2: Package the portable zip + checksum.
    Write-Host "`n== Stage 2: package $versionA =="
    $package = & $packagingScript -PublishDirectory $publishA -Version $versionA -RuntimeIdentifier $rid -OutputDirectory $packageOut
    Assert-That (Test-Path -LiteralPath $package.AssetPath) "Release zip created."
    Assert-That (Test-Path -LiteralPath $package.ChecksumPath) "Checksum file created."

    # Stage 4: Install into the sandbox.
    Write-Host "`n== Stage 3: install $versionA into sandbox =="
    $installExit = Invoke-Exe $exeA @('--install', '--silent', '--install-root', $Sandbox)
    Assert-That ($installExit -eq 0) "--install --silent exited 0."
    $currentLink = Join-Path $Sandbox 'current'
    Assert-That (Test-Path -LiteralPath (Join-Path $Sandbox "versions\$versionA")) "versions\$versionA created."
    Assert-That ((Get-LinkTargetVersion $currentLink) -eq $versionA) "current resolves to $versionA."

    if (-not $SkipUpdate)
    {
        # Stage 5: Simulate an update to version B via --apply-update.
        Write-Host "`n== Stage 4: publish + stage $versionB =="
        Invoke-Publish -version $versionB -rid $rid -outputDirectory $publishB
        $stagedB = Join-Path $Sandbox "versions\$versionB"
        New-Item -ItemType Directory -Force -Path $stagedB | Out-Null
        Copy-Item -Path (Join-Path $publishB '*') -Destination $stagedB -Recurse -Force

        Write-Host "`n== Stage 5: apply update to $versionB =="
        # Launch the apply-update process from the staged version (not through `current`), so it
        # does not hold the `current` junction open while repointing it.
        $stagedExe = Join-Path $stagedB 'Phantom.Workspaces.exe'
        $applyExit = Invoke-Exe $stagedExe @('--apply-update', $stagedB, '--install-root', $Sandbox)
        Assert-That ($applyExit -eq 0) "--apply-update exited 0."
        Assert-That ((Get-LinkTargetVersion $currentLink) -eq $versionB) "current resolves to $versionB."
        Assert-That (Test-Path -LiteralPath (Join-Path $Sandbox "versions\$versionA")) "previous version $versionA retained for rollback."
    }

    # Stage 7: Uninstall + purge.
    Write-Host "`n== Stage 6: uninstall =="
    # Run uninstall from the original publish output (outside the sandbox) so no sandbox file is
    # locked while the managed tree is deleted.
    $uninstallExit = Invoke-Exe $exeA @('--uninstall', '--purge', '--install-root', $Sandbox)
    Assert-That ($uninstallExit -eq 0) "--uninstall --purge exited 0."
    Assert-That (-not (Test-Path -LiteralPath $currentLink)) "managed app tree removed."

    Write-Host "`nAll stages passed." -ForegroundColor Green
}
finally
{
    if (Test-Path -LiteralPath $staging)
    {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }

    if ($KeepSandbox)
    {
        Write-Host "Sandbox retained at: $Sandbox"
    }
    elseif (Test-Path -LiteralPath $Sandbox)
    {
        Remove-Item -LiteralPath $Sandbox -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($failures -gt 0)
{
    exit 1
}
