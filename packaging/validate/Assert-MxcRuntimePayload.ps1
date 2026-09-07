[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PayloadDirectory,
    [Parameter(Mandatory)]
    [string] $RuntimeIdentifier
)

$ErrorActionPreference = 'Stop'

if ($RuntimeIdentifier -ne 'win-x64')
{
    throw "Microsoft MXC payload validation supports only win-x64, not '$RuntimeIdentifier'."
}

$nativeDirectory = Join-Path $PayloadDirectory "runtimes\$RuntimeIdentifier\native"
$requiredFiles = @('mxc_ffi.dll', 'plm.exe', 'MXC-LICENSE.md')
foreach ($fileName in $requiredFiles)
{
    $path = Join-Path $nativeDirectory $fileName
    if (-not (Test-Path -LiteralPath $path -PathType Leaf))
    {
        throw "MXC runtime payload is incomplete: expected '$path'."
    }
}

$licensePath = Join-Path $nativeDirectory 'MXC-LICENSE.md'
$upstreamLicensePath = Join-Path $PSScriptRoot '..\..\microsoft\mxc\LICENSE.md'
$licenseHash = (Get-FileHash -LiteralPath $licensePath -Algorithm SHA256).Hash
$upstreamLicenseHash = (Get-FileHash -LiteralPath $upstreamLicensePath -Algorithm SHA256).Hash
if ($licenseHash -ne $upstreamLicenseHash)
{
    throw 'MXC-LICENSE.md does not contain the unmodified upstream MIT license.'
}
if (Test-Path -LiteralPath (Join-Path $nativeDirectory 'mxc.lic'))
{
    throw 'Unexpected mxc.lic found; MXC is redistributed under its MIT license.'
}

& (Join-Path $PSScriptRoot 'Assert-MxcSdkVersion.ps1') `
    -NativeLibraryPath (Join-Path $nativeDirectory 'mxc_ffi.dll')

Write-Host "MXC runtime payload validation passed for $RuntimeIdentifier."
