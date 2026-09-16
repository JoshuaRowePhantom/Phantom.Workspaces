<#
.SYNOPSIS
    Release-gate assertions that the published/installed payload bundles the GitHub Copilot CLI
    runtime and its license (issue #1376).

.DESCRIPTION
    GitHub.Copilot.SDK resolves the Copilot CLI strictly from
    `AppContext.BaseDirectory\runtimes\<rid>\native\copilot.exe` and does NOT search PATH. The
    single-file publish previously dropped that Content-registered binary, so the installed payload
    had no runtime and the provider failed with "Copilot runtime not found". These checks fail the
    build if the loose runtime and its required license are absent, and (unless -SkipStartupSmoke)
    launch the bundled `copilot.exe` without server-mode arguments and confirm it reaches the
    runtime's expected argument validation.

    Implements the CI / packaging checks documented in docs/design/build-and-installation.md:
      - Publish_IncludesCopilotRuntime_ForEachRid
      - Distribution_IncludesCopilotCliLicense
      - InstalledPayload_StartsCopilotProvider_Smoke

.PARAMETER PayloadDirectory
    The published/installed payload directory (the folder containing Phantom.Workspaces.exe). This
    directory is AppContext.BaseDirectory at runtime.

.PARAMETER RuntimeIdentifier
    The runtime identifier the payload was published for (e.g. win-x64, win-arm64).

.PARAMETER SkipStartupSmoke
    Skip launching copilot.exe. Set this when validating a payload whose RID differs from
    the host architecture (e.g. asserting the win-arm64 payload on an x64 runner), because the
    bundled binary cannot execute on a mismatched CPU.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PayloadDirectory,
    [Parameter(Mandatory)]
    [string] $RuntimeIdentifier,
    [Parameter()]
    [switch] $SkipStartupSmoke
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $PayloadDirectory))
{
    throw "Payload directory not found: $PayloadDirectory"
}

$nativeDir = Join-Path $PayloadDirectory "runtimes\$RuntimeIdentifier\native"

# Publish_IncludesCopilotRuntime_ForEachRid
$copilotExe = Join-Path $nativeDir 'copilot.exe'
if (-not (Test-Path -LiteralPath $copilotExe))
{
    throw "Copilot runtime missing from payload: expected loose file '$copilotExe' (issue #1376). " +
        "GitHub.Copilot.SDK resolves the CLI from AppContext.BaseDirectory\runtimes\<rid>\native\copilot.exe."
}
Write-Host "OK  Copilot runtime present: $copilotExe"

$wrapperExe = Join-Path $nativeDir 'phantom-copilot-wrapper.exe'
if (-not (Test-Path -LiteralPath $wrapperExe -PathType Leaf))
{
    throw "Copilot MXC wrapper missing from payload: expected loose file '$wrapperExe' (issue #1476)."
}
Write-Host "OK  Copilot MXC wrapper present: $wrapperExe"

foreach ($mxcFile in @('mxc_ffi.dll', 'plm.exe', 'MXC-LICENSE.md'))
{
    $mxcPath = Join-Path $nativeDir $mxcFile
    if (-not (Test-Path -LiteralPath $mxcPath -PathType Leaf))
    {
        throw "Copilot containment dependency missing from payload: expected '$mxcPath' (issue #1476)."
    }
}

# Distribution_IncludesCopilotCliLicense
$licenseFile = Join-Path $nativeDir 'LICENSE.md'
if (-not (Test-Path -LiteralPath $licenseFile))
{
    throw "GitHub Copilot CLI LICENSE.md missing from payload: expected '$licenseFile' (issue #1376). " +
        "The CLI is redistributed under the GitHub Copilot CLI License, which requires shipping a copy of LICENSE.md."
}
$licenseText = Get-Content -LiteralPath $licenseFile -Raw
if ($licenseText -notmatch 'GitHub Copilot CLI License')
{
    throw "LICENSE.md at '$licenseFile' does not look like the GitHub Copilot CLI License (issue #1376)."
}
Write-Host "OK  Copilot CLI LICENSE.md present: $licenseFile"

# InstalledPayload_StartsCopilotProvider_Smoke — confirm the bundled runtime actually launches.
if ($SkipStartupSmoke)
{
    Write-Host "SKIP Startup smoke for $RuntimeIdentifier (cross-RID payload)."
}
else
{
    $previousNativeErrorPreference = $PSNativeCommandUseErrorActionPreference
    try
    {
        $PSNativeCommandUseErrorActionPreference = $false
        try
        {
            $startup = & $copilotExe 2>&1
            $startupExitCode = $LASTEXITCODE
        }
        catch
        {
            throw "Bundled copilot.exe could not be launched: $($_.Exception.Message) (issue #1376)."
        }
    }
    finally
    {
        $PSNativeCommandUseErrorActionPreference = $previousNativeErrorPreference
    }

    if ($startupExitCode -ne 1 -or
        ($startup | Out-String) -notmatch 'SDK server mode requires --server or --headless')
    {
        throw "Bundled copilot.exe did not reach expected server-mode argument validation (exit $startupExitCode): $startup (issue #1376)."
    }
    Write-Host "OK  Copilot runtime launches and validates server mode."
}

Write-Host "Copilot runtime payload validation passed for $RuntimeIdentifier."

# GitHub's pwsh runner exits with the last native exit code after the generated step script ends.
# Exit 1 is the expected no-argument probe result, so do not leak that accepted state to the caller.
$global:LASTEXITCODE = 0
