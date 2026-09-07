[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ZipPath,
    [Parameter(Mandatory)]
    [string] $RuntimeIdentifier
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $ZipPath -PathType Leaf))
{
    throw "Release zip not found: $ZipPath"
}

$archiveDirectory = Split-Path -Parent (Resolve-Path -LiteralPath $ZipPath).ProviderPath
$extractRoot = Join-Path $archiveDirectory ".mxc-validation-$([Guid]::NewGuid().ToString('N'))"
try
{
    [IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $extractRoot)
    & (Join-Path $PSScriptRoot 'Assert-MxcRuntimePayload.ps1') `
        -PayloadDirectory $extractRoot -RuntimeIdentifier $RuntimeIdentifier
}
finally
{
    if (Test-Path -LiteralPath $extractRoot)
    {
        Remove-Item -LiteralPath $extractRoot -Recurse -Force
    }
}

Write-Host "MXC release zip validation passed for $RuntimeIdentifier."
