[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ExpectedVersion,
    [Parameter(Mandatory)]
    [string] $ApplicationAssemblyPath,
    [Parameter(Mandatory)]
    [string] $ApplicationExecutablePath
)

$ErrorActionPreference = 'Stop'

if ($ExpectedVersion -notmatch '^(?<numeric>\d+\.\d+\.\d+)(?:[-+].*)?$')
{
    throw "Expected Phantom release version '$ExpectedVersion' is not semantic version text."
}
$expectedNumericVersion = $Matches.numeric

foreach ($path in @($ApplicationAssemblyPath, $ApplicationExecutablePath))
{
    if (-not (Test-Path -LiteralPath $path -PathType Leaf))
    {
        throw "Phantom release version input not found: '$path'."
    }
}

$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName(
    $ApplicationAssemblyPath).Version.ToString(3)
if ($assemblyVersion -ne $expectedNumericVersion)
{
    throw "Phantom application assembly version '$assemblyVersion' does not match release version '$expectedNumericVersion'."
}

foreach ($artifact in @($ApplicationAssemblyPath, $ApplicationExecutablePath))
{
    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($artifact)
    $fileVersion = ([Version] $versionInfo.FileVersion).ToString(3)
    if ($fileVersion -ne $expectedNumericVersion)
    {
        throw "Phantom application file version '$fileVersion' in '$artifact' does not match release version '$expectedNumericVersion'."
    }
    if ($versionInfo.ProductVersion -ne $ExpectedVersion -and
        -not $versionInfo.ProductVersion.StartsWith(
            "$ExpectedVersion+",
            [StringComparison]::Ordinal))
    {
        throw "Phantom application informational version '$($versionInfo.ProductVersion)' in '$artifact' does not match release version '$ExpectedVersion'."
    }
}

Write-Host "Phantom application release version validated: $ExpectedVersion"
