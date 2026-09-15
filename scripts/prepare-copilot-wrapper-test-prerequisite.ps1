param(
    [Parameter(Mandatory)]
    [string] $RepositoryRoot,
    [Parameter(Mandatory)]
    [string] $CacheRoot,
    [Parameter(Mandatory)]
    [string] $PathOutputFile
)

$ErrorActionPreference = 'Stop'

$configuration = 'Release'
$runtimeIdentifier = 'win-x64'
$nativeTarget = 'x86_64-pc-windows-msvc'
$nativeProfile = 'release'
$nativePackages = @('mxc_ffi', 'plm')
$nativeFeatures = @('dotnetsdk')

$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
$CacheRoot = [IO.Path]::GetFullPath($CacheRoot)
$PathOutputFile = [IO.Path]::GetFullPath($PathOutputFile)

function Add-HashText {
    param(
        [Security.Cryptography.IncrementalHash] $Hasher,
        [string] $Value
    )

    $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
    $Hasher.AppendData([BitConverter]::GetBytes($bytes.Length))
    $Hasher.AppendData($bytes)
}

function Get-Sha256 {
    param([string] $Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Test-CompletedCache {
    param(
        [string] $Directory,
        [string] $ExpectedCacheKey
    )

    $manifestPath = Join-Path $Directory 'prerequisite.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf))
    {
        return $false
    }

    try
    {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ($manifest.SchemaVersion -ne 1 -or $manifest.CacheKey -ne $ExpectedCacheKey)
        {
            return $false
        }

        foreach ($artifact in $manifest.Artifacts)
        {
            $artifactPath = Join-Path $Directory $artifact.RelativePath
            if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf))
            {
                return $false
            }
            if ((Get-Sha256 $artifactPath) -ne $artifact.Sha256)
            {
                return $false
            }
        }

        return $true
    }
    catch
    {
        return $false
    }
}

$rustcIdentity = (& rustc -vV 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0)
{
    throw "rustc -vV failed while identifying the MXC prerequisite toolchain."
}

$cargoIdentity = (& cargo --version 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0)
{
    throw "cargo --version failed while identifying the MXC prerequisite toolchain."
}

$dotnetSdkIdentity = (& dotnet --version 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0)
{
    throw "dotnet --version failed while identifying the wrapper prerequisite toolchain."
}
$msbuildIdentity = (& dotnet msbuild -version -nologo 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0)
{
    throw "dotnet msbuild -version failed while identifying the wrapper prerequisite toolchain."
}
$dotnetIdentity = ".NET SDK $dotnetSdkIdentity; MSBuild $msbuildIdentity"

$trackedFiles = @(
    & git -C $RepositoryRoot ls-files --recurse-submodules
)
if ($LASTEXITCODE -ne 0)
{
    throw "git ls-files failed while fingerprinting the wrapper prerequisite."
}

$untrackedFiles = @(
    & git -C $RepositoryRoot ls-files --others --exclude-standard
)
if ($LASTEXITCODE -ne 0)
{
    throw "git ls-files failed while fingerprinting untracked wrapper sources."
}

$sourceFiles = @(
    $trackedFiles
    $untrackedFiles
    [IO.Path]::GetRelativePath($RepositoryRoot, $PSCommandPath)
) | Sort-Object -Unique

$hasher = [Security.Cryptography.IncrementalHash]::CreateHash(
    [Security.Cryptography.HashAlgorithmName]::SHA256)
try
{
    Add-HashText $hasher 'copilot-wrapper-test-prerequisite-v1'
    Add-HashText $hasher $configuration
    Add-HashText $hasher $runtimeIdentifier
    Add-HashText $hasher $nativeTarget
    Add-HashText $hasher $nativeProfile
    Add-HashText $hasher ($nativePackages -join ',')
    Add-HashText $hasher ($nativeFeatures -join ',')
    Add-HashText $hasher $rustcIdentity
    Add-HashText $hasher $cargoIdentity
    Add-HashText $hasher $dotnetIdentity

    foreach ($relativePath in $sourceFiles)
    {
        $fullPath = Join-Path $RepositoryRoot $relativePath
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf))
        {
            continue
        }

        Add-HashText $hasher ($relativePath.Replace('\', '/'))
        $hasher.AppendData([IO.File]::ReadAllBytes($fullPath))
    }

    $cacheKey = [Convert]::ToHexString($hasher.GetHashAndReset()).ToLowerInvariant()
}
finally
{
    $hasher.Dispose()
}

$cacheDirectory = Join-Path $CacheRoot $cacheKey.Substring(0, 16)
$mutex = [System.Threading.Mutex]::new(
    $false,
    "Local\Phantom.Workspaces.CopilotWrapperPrerequisite.$cacheKey")
$ownsMutex = $false
$stagingDirectory = $null
try
{
    try
    {
        $ownsMutex = $mutex.WaitOne()
    }
    catch [Threading.AbandonedMutexException]
    {
        $ownsMutex = $true
    }

    if (-not (Test-CompletedCache $cacheDirectory $cacheKey))
    {
        if (Test-Path -LiteralPath $cacheDirectory)
        {
            Remove-Item -LiteralPath $cacheDirectory -Recurse -Force
        }

        New-Item -ItemType Directory -Path $CacheRoot -Force | Out-Null
        Get-ChildItem -LiteralPath $CacheRoot -Directory -Filter "$($cacheKey.Substring(0, 16)).staging-*" |
            Remove-Item -Recurse -Force

        $stagingDirectory = Join-Path `
            $CacheRoot `
            "$($cacheKey.Substring(0, 16)).staging-$PID"
        $cargoTargetDirectory = Join-Path $stagingDirectory 'cargo'
        $dotnetArtifactsDirectory = Join-Path $stagingDirectory 'dotnet'
        $publishDirectory = Join-Path $stagingDirectory 'publish'
        $preparedDirectory = Join-Path $stagingDirectory 'prepared'
        New-Item -ItemType Directory -Path $preparedDirectory -Force | Out-Null

        Write-Host "Preparing immutable Copilot wrapper prerequisite $cacheKey before VSTest starts; the real cargo build -p mxc_ffi -p plm may remain output-quiet during codegen/link."

        $savedCargoTargetDirectory = $env:CARGO_TARGET_DIR
        try
        {
            $env:CARGO_TARGET_DIR = $cargoTargetDirectory
            $publishArguments = @(
                'msbuild'
                '--disable-build-servers'
                (Join-Path $RepositoryRoot 'Phantom.Workspaces\Phantom.Workspaces.csproj')
                '-restore'
                '-nologo'
                '-m:1'
                '-t:PublishCopilotWrapperLoose'
                '/nodeReuse:false'
                "-p:Configuration=$configuration"
                "-p:RuntimeIdentifier=$runtimeIdentifier"
                '-p:SelfContained=true'
                '-p:PublishReadyToRun=false'
                '-p:UseSharedCompilation=false'
                '-p:UseArtifactsOutput=true'
                "-p:ArtifactsPath=$dotnetArtifactsDirectory"
                "-p:PublishDir=$publishDirectory\"
            )
            & dotnet @publishArguments
            if ($LASTEXITCODE -ne 0)
            {
                throw "The real Copilot wrapper prerequisite publish failed with exit code $LASTEXITCODE."
            }
        }
        finally
        {
            $env:CARGO_TARGET_DIR = $savedCargoTargetDirectory
        }

        $wrapperSource = Join-Path `
            $publishDirectory `
            'runtimes\win-x64\native\phantom-copilot-wrapper.exe'
        $mxcFfiSource = Join-Path `
            $cargoTargetDirectory `
            "$nativeTarget\$nativeProfile\mxc_ffi.dll"
        $plmSource = Join-Path `
            $cargoTargetDirectory `
            "$nativeTarget\$nativeProfile\plm.exe"
        $bindingsSource = Join-Path `
            $RepositoryRoot `
            'microsoft\mxc\sdk\dotnet\Microsoft.Mxc.Sdk\Native\NativeMethods.g.cs'
        $runtimeConfigSource = Get-ChildItem `
                -LiteralPath (Join-Path $dotnetArtifactsDirectory 'bin\Phantom.Workspaces.Containers') `
                -Filter 'Phantom.Workspaces.Containers.runtimeconfig.json' `
                -File `
                -Recurse |
            Where-Object {
                $_.FullName.Contains(
                    [IO.Path]::Combine('release_win-x64', ''),
                    [StringComparison]::OrdinalIgnoreCase)
            } |
            Select-Object -First 1 -ExpandProperty FullName

        $requiredSources = @(
            $wrapperSource
            $mxcFfiSource
            $plmSource
            $bindingsSource
            $runtimeConfigSource
        )
        foreach ($requiredSource in $requiredSources)
        {
            if ([string]::IsNullOrWhiteSpace($requiredSource))
            {
                throw "The wrapper prerequisite did not report every required artifact."
            }
            if (-not (Test-Path -LiteralPath $requiredSource -PathType Leaf))
            {
                throw "The wrapper prerequisite did not produce required artifact '$requiredSource'."
            }
        }

        $artifactSources = [ordered]@{
            'prepared/phantom-copilot-wrapper.exe' = $wrapperSource
            'prepared/mxc_ffi.dll' = $mxcFfiSource
            'prepared/plm.exe' = $plmSource
            'prepared/NativeMethods.g.cs' = $bindingsSource
            'prepared/Phantom.Workspaces.Containers.runtimeconfig.json' = $runtimeConfigSource
        }
        $artifacts = foreach ($entry in $artifactSources.GetEnumerator())
        {
            $destination = Join-Path $stagingDirectory $entry.Key
            Copy-Item -LiteralPath $entry.Value -Destination $destination
            [ordered]@{
                RelativePath = $entry.Key
                Sha256 = Get-Sha256 $destination
            }
        }

        $manifest = [ordered]@{
            SchemaVersion = 1
            CacheKey = $cacheKey
            Configuration = $configuration
            RuntimeIdentifier = $runtimeIdentifier
            NativeTarget = $nativeTarget
            NativeProfile = $nativeProfile
            NativePackages = $nativePackages
            NativeFeatures = $nativeFeatures
            ContainerRuntimeConfigGraphPath = [IO.Path]::GetRelativePath(
                $dotnetArtifactsDirectory,
                $runtimeConfigSource).Replace('\', '/')
            RustcIdentity = $rustcIdentity
            CargoIdentity = $cargoIdentity
            DotNetIdentity = $dotnetIdentity
            Artifacts = $artifacts
        }
        $manifest | ConvertTo-Json -Depth 5 |
            Set-Content -LiteralPath (Join-Path $stagingDirectory 'prerequisite.json') -Encoding utf8

        Remove-Item -LiteralPath $cargoTargetDirectory -Recurse -Force
        Remove-Item -LiteralPath $dotnetArtifactsDirectory -Recurse -Force
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force

        [IO.Directory]::Move($stagingDirectory, $cacheDirectory)
        $stagingDirectory = $null
        Write-Host "Prepared Copilot wrapper prerequisite $cacheKey."
    }
    else
    {
        Write-Host "Using verified Copilot wrapper prerequisite $cacheKey."
    }

    $pathOutputDirectory = Split-Path -Parent $PathOutputFile
    New-Item -ItemType Directory -Path $pathOutputDirectory -Force | Out-Null
    $temporaryPathOutput = "$PathOutputFile.$PID.$([Guid]::NewGuid().ToString('N'))"
    Set-Content -LiteralPath $temporaryPathOutput -Value $cacheDirectory -Encoding utf8NoBOM
    [IO.File]::Move($temporaryPathOutput, $PathOutputFile, $true)
}
finally
{
    if ($stagingDirectory -and (Test-Path -LiteralPath $stagingDirectory))
    {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
    if ($ownsMutex)
    {
        $mutex.ReleaseMutex()
    }
    $mutex.Dispose()
}
