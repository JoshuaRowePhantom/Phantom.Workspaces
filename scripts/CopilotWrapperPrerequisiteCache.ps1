function Assert-PrerequisiteOwnedPath {
    param([string] $Path, [string] $ProjectDirectory)

    $project = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($ProjectDirectory))
    $path = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path))
    if (-not $path.StartsWith("$project\", [StringComparison]::OrdinalIgnoreCase))
    {
        throw "Copilot wrapper prerequisite path is outside the Install.Tests project: '$path'."
    }
    $current = $project
    foreach ($part in $path.Substring($project.Length + 1).Split('\'))
    {
        $current = Join-Path $current $part
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint))
        {
            throw "Copilot wrapper prerequisite path contains a reparse point: '$current'."
        }
    }
    return $path
}

function Assert-PrerequisiteLayout {
    param(
        [string] $ProjectDirectory,
        [string] $BaseIntermediateOutputPath,
        [string] $CacheRoot,
        [string] $PathOutputFile,
        [string] $CopiedPathFile
    )

    $project = [IO.Path]::GetFullPath($ProjectDirectory)
    $base = if ([IO.Path]::IsPathFullyQualified($BaseIntermediateOutputPath)) {
        [IO.Path]::GetFullPath($BaseIntermediateOutputPath)
    } else {
        [IO.Path]::GetFullPath($BaseIntermediateOutputPath, $project)
    }
    $base = Assert-PrerequisiteOwnedPath $base $project
    if ($base -eq [IO.Path]::TrimEndingDirectorySeparator($project))
    {
        throw 'The intermediate output path cannot be the project directory.'
    }
    $expectedRoot = [IO.Path]::GetFullPath('mxcw', $base)
    $root = Assert-PrerequisiteOwnedPath $CacheRoot $project
    if ($root -ne $expectedRoot)
    {
        throw "Copilot wrapper prerequisite cache root '$root' is not the evaluated '$expectedRoot'."
    }
    $pointer = Assert-PrerequisiteOwnedPath $PathOutputFile $project
    if ([IO.Path]::GetFileName($pointer) -ne 'copilot-wrapper-prerequisite.path' -or
        -not $pointer.StartsWith("$base\", [StringComparison]::OrdinalIgnoreCase) -or
        $pointer.StartsWith("$root\", [StringComparison]::OrdinalIgnoreCase))
    {
        throw "Unsafe Copilot wrapper prerequisite pointer path '$pointer'."
    }
    if ($CopiedPathFile)
    {
        $copy = Assert-PrerequisiteOwnedPath $CopiedPathFile $project
        if ([IO.Path]::GetFileName($copy) -ne 'copilot-wrapper-prerequisite.path')
        {
            throw "Unsafe Copilot wrapper prerequisite output pointer '$copy'."
        }
        if ($copy.StartsWith("$root\", [StringComparison]::OrdinalIgnoreCase))
        {
            throw "Copilot wrapper prerequisite output pointer cannot be inside the cache root: '$copy'."
        }
    }
    return $root
}

function Open-PrerequisiteRootLease {
    param(
        [string] $ProjectDirectory,
        [string] $CacheRoot,
        [switch] $Shared
    )

    $lockPath = Join-Path (Split-Path -Parent $CacheRoot) 'mxcw.lock'
    Assert-PrerequisiteOwnedPath $lockPath $ProjectDirectory | Out-Null
    New-Item -ItemType Directory -Path (Split-Path -Parent $lockPath) -Force | Out-Null
    try
    {
        if ($Shared)
        {
            return [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate,
                [IO.FileAccess]::Read, [IO.FileShare]::Read)
        }
        return [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    }
    catch [IO.IOException]
    {
        throw "Copilot wrapper prerequisite cache is in use; Clean cannot remove it: $($_.Exception.Message)"
    }
}

function Assert-PrerequisiteTreeSafe {
    param([string] $Directory)

    foreach ($item in Get-ChildItem -LiteralPath $Directory -Force)
    {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)
        {
            throw "Copilot wrapper prerequisite cache contains a reparse point: '$($item.FullName)'."
        }
        if ($item.PSIsContainer)
        {
            Assert-PrerequisiteTreeSafe $item.FullName
        }
        else
        {
            $handle = [IO.File]::Open($item.FullName, [IO.FileMode]::Open,
                [IO.FileAccess]::Read, [IO.FileShare]::None)
            $handle.Dispose()
        }
    }
}
