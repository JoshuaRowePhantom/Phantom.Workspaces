<#
.SYNOPSIS
    One-line installer for Phantom.Workspaces (`irm <url>/install.ps1 | iex`).

.DESCRIPTION
    Downloads the latest GitHub release asset for the current architecture, verifies its SHA256
    against the published `.sha256`, extracts it, and runs `Phantom.Workspaces.exe --install
    --silent` to bootstrap the per-user managed layout (no elevation required). Defaults to the
    production repository and the `releases/latest` API; both are overridable for testing.

.PARAMETER Repository
    The `owner/repo` slug to install from. Defaults to JoshuaRowePhantom/Phantom.Workspaces.

.PARAMETER Version
    A specific version tag (e.g. v0.2.0). Defaults to the latest release.

.PARAMETER RuntimeIdentifier
    The architecture asset to fetch. Defaults to the current process architecture.

.PARAMETER InstallRoot
    Overrides the managed install root (passed through as --install-root). For sandboxed testing.
#>
[CmdletBinding()]
param(
    [string] $Repository = 'JoshuaRowePhantom/Phantom.Workspaces',
    [string] $Version,
    [string] $RuntimeIdentifier,
    [string] $InstallRoot
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Resolve-RuntimeIdentifier
{
    if ($RuntimeIdentifier) { return $RuntimeIdentifier }
    switch ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture)
    {
        'Arm64' { 'win-arm64' }
        default { 'win-x64' }
    }
}

function Get-Sha256DigestFromChecksumContent
{
    <#
    .SYNOPSIS
        Extracts a lowercased 64-hex SHA256 digest from the raw body of a `.sha256` asset.

    .DESCRIPTION
        Accepts either a [byte[]] (as returned by Invoke-WebRequest against an asset served with
        Content-Type: application/octet-stream) or a [string]. Handles common layouts:
        `hash`, `hash  file` (sha256sum), `hash *file` (BSD binary), with optional BOM/CRLF/
        leading/trailing whitespace. Throws a descriptive error if no 64-hex digest is present.
    #>
    param(
        [Parameter(Mandatory)] $Content,
        [string] $SourceName = 'checksum content'
    )

    if ($null -eq $Content)
    {
        throw "Could not locate SHA256 digest in $SourceName."
    }

    $text = if ($Content -is [byte[]])
    {
        [System.Text.Encoding]::ASCII.GetString($Content)
    }
    else
    {
        [string] $Content
    }

    if ($text -notmatch '[A-Fa-f0-9]{64}')
    {
        throw "Could not locate SHA256 digest in $SourceName."
    }

    return $Matches[0].ToLowerInvariant()
}

if ($MyInvocation.InvocationName -eq '.')
{
    # Dot-sourced (e.g. by Pester tests) — expose the helper functions but skip the installer flow.
    return
}

$assetPrefix = 'Phantom.Workspaces'
$rid = Resolve-RuntimeIdentifier

$releaseApi = if ($Version)
{
    "https://api.github.com/repos/$Repository/releases/tags/$Version"
}
else
{
    "https://api.github.com/repos/$Repository/releases/latest"
}

Write-Host "Resolving release from $releaseApi ..."
$headers = @{ 'User-Agent' = 'phantom-workspaces-installer'; 'Accept' = 'application/vnd.github+json' }
$release = Invoke-RestMethod -Uri $releaseApi -Headers $headers

$assetName = "$assetPrefix-$($release.tag_name.TrimStart('v'))-$rid.zip"
$asset = $release.assets | Where-Object { $_.name -eq $assetName }
if (-not $asset)
{
    # Fall back to a tag-named asset when the version component differs from the trimmed tag.
    $asset = $release.assets | Where-Object { $_.name -like "$assetPrefix-*-$rid.zip" } | Select-Object -First 1
}
if (-not $asset)
{
    throw "No $rid asset found in release $($release.tag_name)."
}

$checksumAsset = $release.assets | Where-Object { $_.name -eq "$($asset.name).sha256" }

$workDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "phantom-workspaces-install-$([guid]::NewGuid())"
New-Item -ItemType Directory -Force -Path $workDirectory | Out-Null
try
{
    $zipPath = Join-Path $workDirectory $asset.name
    Write-Host "Downloading $($asset.name) ..."
    Invoke-WebRequest -Uri $asset.browser_download_url -Headers $headers -OutFile $zipPath

    if ($checksumAsset)
    {
        Write-Host 'Verifying SHA256 ...'
        $checksumResponse = Invoke-WebRequest -Uri $checksumAsset.browser_download_url -Headers $headers
        $expected = Get-Sha256DigestFromChecksumContent -Content $checksumResponse.Content -SourceName $checksumAsset.name
        $actual = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($expected -ne $actual)
        {
            throw "Checksum mismatch: expected $expected, got $actual."
        }
    }
    else
    {
        Write-Warning 'No .sha256 asset found; skipping checksum verification.'
    }

    $extractDirectory = Join-Path $workDirectory 'payload'
    Expand-Archive -LiteralPath $zipPath -DestinationPath $extractDirectory -Force

    $executable = Join-Path $extractDirectory 'Phantom.Workspaces.exe'
    if (-not (Test-Path -LiteralPath $executable))
    {
        throw "Extracted payload is missing Phantom.Workspaces.exe."
    }

    $installArguments = @('--install', '--silent')
    if ($InstallRoot)
    {
        $installArguments += @('--install-root', $InstallRoot)
    }

    Write-Host 'Installing into the per-user managed layout ...'
    $process = Start-Process -FilePath $executable -ArgumentList $installArguments -Wait -PassThru -NoNewWindow
    if ($process.ExitCode -ne 0)
    {
        throw "Installation failed with exit code $($process.ExitCode)."
    }

    Write-Host "Phantom.Workspaces $($release.tag_name) installed."
}
finally
{
    Remove-Item -LiteralPath $workDirectory -Recurse -Force -ErrorAction SilentlyContinue
}
