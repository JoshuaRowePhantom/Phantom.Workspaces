<#
.SYNOPSIS
    Release-gate assertion that the FINAL packaged release zip preserves the nested Copilot CLI
    runtime layout (issue #1377, regression of #1376).

.DESCRIPTION
    `New-ReleaseZip.ps1` previously flattened the directory tree, dropping
    `runtimes\<rid>\native\copilot.exe` to the ZIP root. The self-updater faithfully preserves
    whatever the ZIP contains, so the installed payload lost the nested runtime path and the
    GitHub.Copilot.SDK provider - which resolves the CLI strictly from
    `AppContext.BaseDirectory\runtimes\<rid>\native\copilot.exe` - failed.

    This script extracts the produced ZIP to a temporary directory and reuses
    Assert-CopilotRuntimePayload.ps1 to assert the nested `runtimes\<rid>\native\copilot.exe` and
    LICENSE.md are present. It validates the SHIPPED artifact, not the publish directory, closing
    the gap that let the flatten reach releases.

.PARAMETER ZipPath
    The release zip produced by New-ReleaseZip.ps1.

.PARAMETER RuntimeIdentifier
    The runtime identifier the payload was published for (e.g. win-x64, win-arm64).

.PARAMETER SkipStartupSmoke
    Skip launching copilot.exe --version. Set this when validating a zip whose RID differs from the
    host architecture (e.g. asserting the win-arm64 zip on an x64 runner), because the bundled
    binary cannot execute on a mismatched CPU.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ZipPath,
    [Parameter(Mandatory)]
    [string] $RuntimeIdentifier,
    [Parameter()]
    [switch] $SkipStartupSmoke
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ZipPath))
{
    throw "Release zip not found: $ZipPath"
}

$assertPayload = Join-Path $PSScriptRoot 'Assert-CopilotRuntimePayload.ps1'
if (-not (Test-Path -LiteralPath $assertPayload))
{
    throw "Companion payload assertion script not found: $assertPayload"
}

$extractRoot = Join-Path ([System.IO.Path]::GetTempPath()) "phantom-zip-validate-$([Guid]::NewGuid().ToString('N'))"

try
{
    New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null

    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
    [System.IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $extractRoot)

    Write-Host "Extracted $ZipPath -> $extractRoot"

    & $assertPayload -PayloadDirectory $extractRoot -RuntimeIdentifier $RuntimeIdentifier -SkipStartupSmoke:$SkipStartupSmoke

    Write-Host "Release zip runtime payload validation passed for $RuntimeIdentifier ($ZipPath)."
}
finally
{
    if (Test-Path -LiteralPath $extractRoot)
    {
        Remove-Item -LiteralPath $extractRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
