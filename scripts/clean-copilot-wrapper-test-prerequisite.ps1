param(
    [Parameter(Mandatory)][string] $ProjectDirectory,
    [Parameter(Mandatory)][string] $BaseIntermediateOutputPath,
    [Parameter(Mandatory)][string] $CacheRoot,
    [Parameter(Mandatory)][string] $PathOutputFile,
    [Parameter(Mandatory)][string] $CopiedPathFile
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CopilotWrapperPrerequisiteCache.ps1')
$root = Assert-PrerequisiteLayout `
    $ProjectDirectory $BaseIntermediateOutputPath $CacheRoot $PathOutputFile $CopiedPathFile
$lease = Open-PrerequisiteRootLease $ProjectDirectory $root
try
{
    # Preflight every deletion before removing even the first pointer or entry.
    foreach ($pointer in @($PathOutputFile, $CopiedPathFile))
    {
        Assert-PrerequisiteOwnedPath $pointer $ProjectDirectory | Out-Null
        $item = Get-Item -LiteralPath $pointer -Force -ErrorAction SilentlyContinue
        if ($item -and (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            $item.PSIsContainer))
        {
            throw "Unsafe Copilot wrapper prerequisite pointer: '$pointer'."
        }
        if ($item)
        {
            $handle = [IO.File]::Open($pointer, [IO.FileMode]::Open,
                [IO.FileAccess]::Read, [IO.FileShare]::None)
            $handle.Dispose()
        }
    }
    if (Test-Path -LiteralPath $root)
    {
        Assert-PrerequisiteTreeSafe $root
        foreach ($item in Get-ChildItem -LiteralPath $root -Force)
        {
            if (-not $item.PSIsContainer -or
                $item.Name -notmatch '^(?:[0-9a-f]{16}|[0-9a-f]{16}\.staging-[0-9]+)$')
            {
                throw "Unexpected entry in Copilot wrapper prerequisite cache: '$($item.FullName)'."
            }
        }
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction Stop
    }
    foreach ($pointer in @($PathOutputFile, $CopiedPathFile))
    {
        if (Test-Path -LiteralPath $pointer)
        {
            Remove-Item -LiteralPath $pointer -Force -ErrorAction Stop
        }
    }
}
finally
{
    $lease.Dispose()
}
