<#
.SYNOPSIS
    Assembles the portable release zip from a dotnet publish output and emits its SHA256.

.DESCRIPTION
    Takes the self-contained single-file publish output for one runtime identifier and packages
    it into the stable-named release asset `Phantom.Workspaces-<version>-<rid>.zip`, alongside a
    `<asset>.sha256` file (`<hash>  <asset-name>`). These are the exact assets the in-app updater,
    the tray notifier, the README "latest" link, and a future winget manifest consume.

.PARAMETER PublishDirectory
    The `dotnet publish` output directory containing Phantom.Workspaces.exe and any loose assets.

.PARAMETER Version
    The release version (e.g. 0.1.0), used in the asset name.

.PARAMETER RuntimeIdentifier
    The runtime identifier the payload was published for (e.g. win-x64, win-arm64).

.PARAMETER OutputDirectory
    Where to write the zip and its .sha256. Created if absent.

.PARAMETER AssetPrefix
    The asset-name prefix. Defaults to Phantom.Workspaces.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PublishDirectory,
    [Parameter(Mandatory)]
    [string] $Version,
    [Parameter(Mandatory)]
    [string] $RuntimeIdentifier,
    [Parameter(Mandatory)]
    [string] $OutputDirectory,
    [Parameter()]
    [string] $AssetPrefix = 'Phantom.Workspaces'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $PublishDirectory))
{
    throw "Publish directory not found: $PublishDirectory"
}

$executable = Join-Path $PublishDirectory 'Phantom.Workspaces.exe'
if (-not (Test-Path -LiteralPath $executable))
{
    throw "Expected published executable not found: $executable"
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$assetName = "$AssetPrefix-$Version-$RuntimeIdentifier.zip"
$assetPath = Join-Path $OutputDirectory $assetName
if (Test-Path -LiteralPath $assetPath)
{
    Remove-Item -LiteralPath $assetPath -Force
}

# Package the publish output, excluding symbol files (shipped separately as a CI artifact).
$itemsToPack = Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File |
    Where-Object { $_.Extension -ne '.pdb' }

Compress-Archive -LiteralPath $itemsToPack.FullName -DestinationPath $assetPath -CompressionLevel Optimal

$hash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumPath = "$assetPath.sha256"
# `<hash>  <asset-name>` — the standard sha256sum format the updater verifies against.
Set-Content -LiteralPath $checksumPath -Value "$hash  $assetName" -Encoding ascii -NoNewline

Write-Host "Created $assetPath"
Write-Host "SHA256  $hash"

[pscustomobject]@{
    AssetName    = $assetName
    AssetPath    = $assetPath
    ChecksumPath = $checksumPath
    Sha256       = $hash
}
