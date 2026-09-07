[CmdletBinding()]
param(
    [string] $NativeLibraryPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).ProviderPath
$managedProject = Join-Path $repositoryRoot 'microsoft\mxc\sdk\dotnet\Microsoft.Mxc.Sdk\Microsoft.Mxc.Sdk.csproj'
$cargoManifest = Join-Path $repositoryRoot 'microsoft\mxc\src\Cargo.toml'

[xml] $project = Get-Content -LiteralPath $managedProject -Raw
$managedVersion = ([string] $project.Project.PropertyGroup.Version).Trim()
$cargoText = Get-Content -LiteralPath $cargoManifest -Raw
if ($cargoText -notmatch '(?m)^version\s*=\s*"([^"]+)"')
{
    throw "Could not read the MXC workspace version from '$cargoManifest'."
}
$nativeSourceVersion = $Matches[1]
if ($managedVersion -ne $nativeSourceVersion)
{
    throw "MXC managed version '$managedVersion' does not match native workspace version '$nativeSourceVersion'."
}

if ($NativeLibraryPath)
{
    $nativeVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($NativeLibraryPath).ProductVersion
    if ($nativeVersion -and -not $nativeVersion.StartsWith($managedVersion, [StringComparison]::Ordinal))
    {
        throw "MXC native product version '$nativeVersion' does not match managed version '$managedVersion'."
    }
}

Write-Host "MXC managed/native source version validated: $managedVersion"
