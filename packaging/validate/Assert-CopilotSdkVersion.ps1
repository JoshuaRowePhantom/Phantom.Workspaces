<#
.SYNOPSIS
    Asserts GitHub.Copilot.SDK is pinned to the expected version in Directory.Packages.props
    (issue #1376: PackageVersions_CopilotSdk_IsExpectedPinnedVersion).

.DESCRIPTION
    The redistributed Copilot CLI version is pinned 1:1 by the SDK NuGet version (SDK 1.0.11 ->
    CLI 1.0.79). Bundling the CLI is only compliant/correct when the SDK — and therefore the CLI
    it downloads and we ship — stays at the reviewed version. This guard fails the build if the
    central package pin drifts unexpectedly.

.PARAMETER RepositoryRoot
    Repository root containing Directory.Packages.props. Defaults to two levels above this script.

.PARAMETER ExpectedVersion
    The expected pinned GitHub.Copilot.SDK version. Defaults to 1.0.11.
#>
[CmdletBinding()]
param(
    [Parameter()]
    [string] $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [Parameter()]
    [string] $ExpectedVersion = '1.0.11'
)

$ErrorActionPreference = 'Stop'

$propsPath = Join-Path $RepositoryRoot 'Directory.Packages.props'
if (-not (Test-Path -LiteralPath $propsPath))
{
    throw "Directory.Packages.props not found at: $propsPath"
}

[xml] $props = Get-Content -LiteralPath $propsPath -Raw
$node = $props.Project.ItemGroup.PackageVersion |
    Where-Object { $_.Include -eq 'GitHub.Copilot.SDK' } |
    Select-Object -First 1

if ($null -eq $node)
{
    throw "GitHub.Copilot.SDK PackageVersion not found in $propsPath (issue #1376)."
}

if ($node.Version -ne $ExpectedVersion)
{
    throw "GitHub.Copilot.SDK is pinned at '$($node.Version)' but expected '$ExpectedVersion' (issue #1376)."
}

Write-Host "OK  GitHub.Copilot.SDK pinned at $ExpectedVersion in Directory.Packages.props."
