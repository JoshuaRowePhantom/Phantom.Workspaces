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
$appReleaseVersion = '0.0.21'
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

function Get-PreparedCacheKey {
    param(
        [string] $SourceFingerprint,
        [string] $CopilotSdkPackageVersion,
        [string] $CopilotSdkPackageSha512,
        [string] $CopilotCliVersion,
        [string] $CopilotCliPlatform,
        [string] $CopilotCliDownloadUrl,
        [string] $CopilotCliChecksumsUrl,
        [string] $CopilotCliArchiveSha256,
        [string] $CopilotCliChecksumsSha256,
        [object[]] $Artifacts
    )

    $hasher = [Security.Cryptography.IncrementalHash]::CreateHash(
        [Security.Cryptography.HashAlgorithmName]::SHA256)
    try
    {
        Add-HashText $hasher 'copilot-wrapper-test-prerequisite-v5-cache'
        foreach ($value in @(
            $SourceFingerprint
            $CopilotSdkPackageVersion
            $CopilotSdkPackageSha512
            $CopilotCliVersion
            $CopilotCliPlatform
            $CopilotCliDownloadUrl
            $CopilotCliChecksumsUrl
            $CopilotCliArchiveSha256
            $CopilotCliChecksumsSha256))
        {
            Add-HashText $hasher $value
        }
        $normalizedArtifacts = foreach ($artifact in $Artifacts)
        {
            if ($artifact -is [Collections.IDictionary])
            {
                [pscustomobject]@{
                    RelativePath = [string] $artifact['RelativePath']
                    Sha256 = [string] $artifact['Sha256']
                }
            }
            else
            {
                [pscustomobject]@{
                    RelativePath = [string] $artifact.RelativePath
                    Sha256 = [string] $artifact.Sha256
                }
            }
        }
        foreach ($artifact in $normalizedArtifacts)
        {
            Add-HashText $hasher $artifact.RelativePath
            Add-HashText $hasher $artifact.Sha256
        }

        return [Convert]::ToHexString($hasher.GetHashAndReset()).ToLowerInvariant()
    }
    finally
    {
        $hasher.Dispose()
    }
}

function Test-CompletedCache {
    param(
        [string] $Directory,
        [string] $ExpectedCacheRoot,
        [string] $ExpectedSourceFingerprint
    )

    if ([string]::IsNullOrWhiteSpace($Directory) -or
        [IO.Path]::GetFullPath((Split-Path -Parent $Directory)) -ne
            [IO.Path]::GetFullPath($ExpectedCacheRoot))
    {
        return $false
    }

    $manifestPath = Join-Path $Directory 'prerequisite.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf))
    {
        return $false
    }

    try
    {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ($manifest.SchemaVersion -ne 5 -or
            $manifest.SourceFingerprint -ne $ExpectedSourceFingerprint -or
            $manifest.CacheKey -notmatch '^[0-9a-f]{64}$' -or
            (Split-Path -Leaf $Directory) -ne $manifest.CacheKey.Substring(0, 16))
        {
            return $false
        }
        $fingerprintPath = Join-Path $Directory 'prerequisite.fingerprint'
        if (-not (Test-Path -LiteralPath $fingerprintPath -PathType Leaf) -or
            (Get-Content -LiteralPath $fingerprintPath -Raw).Trim() -ne $manifest.CacheKey)
        {
            return $false
        }

        $expectedArtifacts = @(
            'prepared/LICENSE.md'
            'prepared/MXC-LICENSE.md'
            'prepared/Microsoft.Mxc.Sdk.dll'
            'prepared/NativeMethods.g.cs'
            'prepared/Phantom.Workspaces.Containers.runtimeconfig.json'
            'prepared/Phantom.Workspaces.Llm.Core.dll'
            'prepared/Phantom.Workspaces.dll'
            'prepared/copilot.exe'
            'prepared/copilot_runtime.dll'
            'prepared/mxc_ffi.dll'
            'prepared/phantom-copilot-wrapper.exe'
            'prepared/phantom-copilot-wrapper.dll'
            'prepared/plm.exe'
        )
        $artifacts = @($manifest.Artifacts)
        if ($artifacts.Count -ne $expectedArtifacts.Count -or
            (Compare-Object `
                $expectedArtifacts `
                @($artifacts.RelativePath | Sort-Object -Unique)))
        {
            return $false
        }

        foreach ($artifact in $artifacts)
        {
            if ($artifact.Sha256 -notmatch '^[0-9a-f]{64}$' -or
                [IO.Path]::IsPathRooted($artifact.RelativePath) -or
                $artifact.RelativePath -match '(^|[\\/])\.\.([\\/]|$)')
            {
                return $false
            }
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

        $computedCacheKey = Get-PreparedCacheKey `
            -SourceFingerprint $manifest.SourceFingerprint `
            -CopilotSdkPackageVersion $manifest.CopilotSdkPackageVersion `
            -CopilotSdkPackageSha512 $manifest.CopilotSdkPackageSha512 `
            -CopilotCliVersion $manifest.CopilotCliVersion `
            -CopilotCliPlatform $manifest.CopilotCliPlatform `
            -CopilotCliDownloadUrl $manifest.CopilotCliDownloadUrl `
            -CopilotCliChecksumsUrl $manifest.CopilotCliChecksumsUrl `
            -CopilotCliArchiveSha256 $manifest.CopilotCliArchiveSha256 `
            -CopilotCliChecksumsSha256 $manifest.CopilotCliChecksumsSha256 `
            -Artifacts $artifacts
        return $computedCacheKey -eq $manifest.CacheKey
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

$packagesPath = Join-Path $RepositoryRoot 'Directory.Packages.props'
[xml] $packages = Get-Content -LiteralPath $packagesPath -Raw
$copilotSdkPackageNodes = @(
    $packages.Project.ItemGroup.PackageVersion |
        Where-Object { $_.Include -eq 'GitHub.Copilot.SDK' }
)
if ($copilotSdkPackageNodes.Count -ne 1)
{
    throw "Directory.Packages.props must declare exactly one GitHub.Copilot.SDK version."
}
$copilotSdkPackageVersion = [string] $copilotSdkPackageNodes[0].Version

$globalPackagesOutput = (& dotnet nuget locals global-packages --list 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or
    $globalPackagesOutput -notmatch '(?m)^global-packages:\s*(?<path>.+?)\s*$')
{
    throw "dotnet nuget locals could not resolve the global packages directory."
}
$globalPackagesDirectory = [IO.Path]::GetFullPath($Matches.path)
$copilotSdkPackageDirectory = Join-Path `
    $globalPackagesDirectory `
    "github.copilot.sdk\$copilotSdkPackageVersion"
if (-not (Test-Path -LiteralPath $copilotSdkPackageDirectory -PathType Container))
{
    throw "GitHub.Copilot.SDK $copilotSdkPackageVersion was not restored before prerequisite preparation."
}
$copilotSdkPackageHashPath = Join-Path `
    $copilotSdkPackageDirectory `
    "github.copilot.sdk.$copilotSdkPackageVersion.nupkg.sha512"
$copilotSdkPropsPath = Join-Path `
    $copilotSdkPackageDirectory `
    'build\GitHub.Copilot.SDK.props'
$copilotSdkTargetsPath = Join-Path `
    $copilotSdkPackageDirectory `
    'build\GitHub.Copilot.SDK.targets'
foreach ($packageFile in @(
    $copilotSdkPackageHashPath
    $copilotSdkPropsPath
    $copilotSdkTargetsPath))
{
    if (-not (Test-Path -LiteralPath $packageFile -PathType Leaf))
    {
        throw "Restored GitHub.Copilot.SDK provenance file is missing: '$packageFile'."
    }
}
$copilotSdkPackageSha512 = (
    Get-Content -LiteralPath $copilotSdkPackageHashPath -Raw).Trim()
[xml] $copilotSdkProps = Get-Content -LiteralPath $copilotSdkPropsPath -Raw
$copilotCliVersion = [string] $copilotSdkProps.Project.PropertyGroup.CopilotCliVersion
if ([string]::IsNullOrWhiteSpace($copilotCliVersion))
{
    throw "GitHub.Copilot.SDK did not declare CopilotCliVersion."
}
$copilotCliPlatform = 'win32-x64'
$copilotCliReleaseBaseUrl = if (
    [string]::IsNullOrWhiteSpace($env:COPILOT_CLI_DOWNLOAD_BASE_URL))
{
    'https://github.com/github/copilot-cli/releases/download'
}
else
{
    $env:COPILOT_CLI_DOWNLOAD_BASE_URL.TrimEnd('/')
}

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
    Add-HashText $hasher 'copilot-wrapper-test-prerequisite-v5-source'
    Add-HashText $hasher $configuration
    Add-HashText $hasher $runtimeIdentifier
    Add-HashText $hasher $appReleaseVersion
    Add-HashText $hasher $nativeTarget
    Add-HashText $hasher $nativeProfile
    Add-HashText $hasher ($nativePackages -join ',')
    Add-HashText $hasher ($nativeFeatures -join ',')
    Add-HashText $hasher $rustcIdentity
    Add-HashText $hasher $cargoIdentity
    Add-HashText $hasher $dotnetIdentity
    Add-HashText $hasher $copilotSdkPackageVersion
    Add-HashText $hasher $copilotSdkPackageSha512
    Add-HashText $hasher (Get-Sha256 $copilotSdkPropsPath)
    Add-HashText $hasher (Get-Sha256 $copilotSdkTargetsPath)
    Add-HashText $hasher $copilotCliVersion
    Add-HashText $hasher $copilotCliPlatform
    Add-HashText $hasher $copilotCliReleaseBaseUrl

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

    $sourceFingerprint = [Convert]::ToHexString(
        $hasher.GetHashAndReset()).ToLowerInvariant()
}
finally
{
    $hasher.Dispose()
}

$mutex = [System.Threading.Mutex]::new(
    $false,
    "Local\Phantom.Workspaces.CopilotWrapperPrerequisite.$sourceFingerprint")
$ownsMutex = $false
$stagingDirectory = $null
$productionIntermediateDirectory = $null
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

    $cacheDirectory = $null
    if (Test-Path -LiteralPath $PathOutputFile -PathType Leaf)
    {
        $candidateCacheDirectory = (
            Get-Content -LiteralPath $PathOutputFile -Raw).Trim()
        if (-not [string]::IsNullOrWhiteSpace($candidateCacheDirectory) -and
            (Test-CompletedCache `
                $candidateCacheDirectory `
                $CacheRoot `
                $sourceFingerprint))
        {
            $cacheDirectory = $candidateCacheDirectory
            $cacheKey = (
                Get-Content `
                    -LiteralPath (Join-Path $cacheDirectory 'prerequisite.fingerprint') `
                    -Raw).Trim()
        }
    }

    if ($null -eq $cacheDirectory)
    {
        New-Item -ItemType Directory -Path $CacheRoot -Force | Out-Null
        Get-ChildItem `
                -LiteralPath $CacheRoot `
                -Directory `
                -Filter "$($sourceFingerprint.Substring(0, 16)).staging-*" |
            Remove-Item -Recurse -Force

        $stagingDirectory = Join-Path `
            $CacheRoot `
            "$($sourceFingerprint.Substring(0, 16)).staging-$PID"
        $cargoTargetDirectory = Join-Path $stagingDirectory 'cargo'
        $dotnetArtifactsDirectory = Join-Path $stagingDirectory 'dotnet'
        $publishDirectory = Join-Path $stagingDirectory 'publish'
        $releaseAssetsDirectory = Join-Path $stagingDirectory 'release-assets'
        $productionIntermediateDirectory = Join-Path `
            $RepositoryRoot `
            "artifacts\cw-$PID-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
        $preparedDirectory = Join-Path $stagingDirectory 'prepared'
        New-Item -ItemType Directory -Path $preparedDirectory -Force | Out-Null

        Write-Host "Preparing immutable Copilot wrapper prerequisite for source $sourceFingerprint before VSTest starts; the real cargo build -p mxc_ffi -p plm may remain output-quiet during codegen/link."

        $savedCargoTargetDirectory = $env:CARGO_TARGET_DIR
        try
        {
            $env:CARGO_TARGET_DIR = $cargoTargetDirectory
            $publishArguments = @(
                'publish'
                (Join-Path $RepositoryRoot 'Phantom.Workspaces\Phantom.Workspaces.csproj')
                '--disable-build-servers'
                '-nologo'
                '-m:1'
                '/nodeReuse:false'
                '-c'
                $configuration
                '-r'
                $runtimeIdentifier
                '-o'
                $publishDirectory
                "-p:PhantomReleaseVersion=$appReleaseVersion"
                '-p:UseSharedCompilation=false'
                '-p:UseArtifactsOutput=true'
                "-p:ArtifactsPath=$dotnetArtifactsDirectory"
                "-p:CopilotWrapperPublishIntermediateRoot=$productionIntermediateDirectory\"
            )
            $publishLog = @(& dotnet @publishArguments 2>&1)
            $publishLog | Write-Host
            if ($LASTEXITCODE -ne 0)
            {
                throw "The real production Release publish failed with exit code $LASTEXITCODE."
            }

            $appAssemblySource = Join-Path `
                $dotnetArtifactsDirectory `
                'bin\Phantom.Workspaces\release_win-x64\Phantom.Workspaces.dll'
            $mxcSdkAssemblySource = Join-Path `
                $dotnetArtifactsDirectory `
                'bin\Microsoft.Mxc.Sdk\release\Microsoft.Mxc.Sdk.dll'
            $wrapperAssemblySource = Join-Path `
                $dotnetArtifactsDirectory `
                'bin\Phantom.Workspaces.Copilot.Cli.Wrapper\release_win-x64\phantom-copilot-wrapper.dll'
            $llmCoreAssemblySource = Join-Path `
                $dotnetArtifactsDirectory `
                'bin\Phantom.Workspaces.Llm.Core\release_win-x64\Phantom.Workspaces.Llm.Core.dll'
            $appExecutableSource = Join-Path $publishDirectory 'Phantom.Workspaces.exe'
            [xml] $mxcSdkProject = Get-Content -LiteralPath (
                Join-Path $RepositoryRoot `
                    'microsoft\mxc\sdk\dotnet\Microsoft.Mxc.Sdk\Microsoft.Mxc.Sdk.csproj') -Raw
            $mxcSdkProjectVersions = @(
                $mxcSdkProject.Project.PropertyGroup |
                    ForEach-Object { [string] $_.Version } |
                    Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
            )
            if ($mxcSdkProjectVersions.Count -ne 1)
            {
                throw 'Microsoft.Mxc.Sdk.csproj must declare exactly one project version.'
            }
            $mxcSdkProjectVersion = $mxcSdkProjectVersions[0].Trim()
            & (Join-Path $RepositoryRoot 'packaging\validate\Assert-PhantomReleaseVersion.ps1') `
                -ExpectedVersion $appReleaseVersion `
                -ApplicationAssemblyPath $appAssemblySource `
                -ApplicationExecutablePath $appExecutableSource

            $parentPrefix = 'Copilot wrapper child PublishDir: '
            $childPrefix = 'Copilot wrapper child confirmed parent PublishDir: '
            $parentPublishDirectories = @(
                $publishLog |
                    ForEach-Object { [string] $_ } |
                    Where-Object { $_.Contains($parentPrefix, [StringComparison]::Ordinal) } |
                    ForEach-Object {
                        $_.Substring($_.IndexOf(
                            $parentPrefix,
                            [StringComparison]::Ordinal) + $parentPrefix.Length).Trim().TrimEnd('\', '/')
                    }
            )
            $childPublishDirectories = @(
                $publishLog |
                    ForEach-Object { [string] $_ } |
                    Where-Object { $_.Contains($childPrefix, [StringComparison]::Ordinal) } |
                    ForEach-Object {
                        $_.Substring($_.IndexOf(
                            $childPrefix,
                            [StringComparison]::Ordinal) + $childPrefix.Length).Trim().TrimEnd('\', '/')
                    }
            )
            if ($parentPublishDirectories.Count -ne 1 -or
                $childPublishDirectories.Count -ne 1 -or
                $parentPublishDirectories[0] -ne $childPublishDirectories[0] -or
                -not [IO.Path]::IsPathFullyQualified($parentPublishDirectories[0]))
            {
                throw "The production publish did not prove one identical absolute parent/child wrapper PublishDir."
            }

            & (Join-Path $RepositoryRoot 'packaging\validate\Assert-CopilotRuntimePayload.ps1') `
                -PayloadDirectory $publishDirectory `
                -RuntimeIdentifier $runtimeIdentifier
            & (Join-Path $RepositoryRoot 'packaging\validate\Assert-MxcRuntimePayload.ps1') `
                -PayloadDirectory $publishDirectory `
                -RuntimeIdentifier $runtimeIdentifier `
                -ManagedOutputPath $dotnetArtifactsDirectory
            & (Join-Path $RepositoryRoot 'packaging\zip\New-ReleaseZip.ps1') `
                -PublishDirectory $publishDirectory `
                -Version $appReleaseVersion `
                -RuntimeIdentifier $runtimeIdentifier `
                -OutputDirectory $releaseAssetsDirectory
            $releaseArchiveName =
                "Phantom.Workspaces-$appReleaseVersion-$runtimeIdentifier.zip"
            $zipPath = Join-Path `
                $releaseAssetsDirectory `
                $releaseArchiveName
            & (Join-Path $RepositoryRoot 'packaging\validate\Assert-CopilotRuntimeZip.ps1') `
                -ZipPath $zipPath `
                -RuntimeIdentifier $runtimeIdentifier
            & (Join-Path $RepositoryRoot 'packaging\validate\Assert-MxcRuntimeZip.ps1') `
                -ZipPath $zipPath `
                -RuntimeIdentifier $runtimeIdentifier `
                -ManagedOutputPath $dotnetArtifactsDirectory
        }
        finally
        {
            $env:CARGO_TARGET_DIR = $savedCargoTargetDirectory
        }

        $wrapperSource = Join-Path `
            $publishDirectory `
            'runtimes\win-x64\native\phantom-copilot-wrapper.exe'
        $wrapperBuildDirectory = Join-Path `
            $dotnetArtifactsDirectory `
            'bin\Phantom.Workspaces.Copilot.Cli.Wrapper\release_win-x64'
        $copilotSource = Get-ChildItem `
                -LiteralPath $wrapperBuildDirectory `
                -Filter 'copilot.exe' `
                -File `
                -Recurse |
            Where-Object {
                $_.FullName.Contains(
                    [IO.Path]::Combine('runtimes', 'win-x64', 'native'),
                    [StringComparison]::OrdinalIgnoreCase)
            } |
            Select-Object -First 1 -ExpandProperty FullName
        $copilotRuntimeSource = Get-ChildItem `
                -LiteralPath $wrapperBuildDirectory `
                -Filter 'copilot_runtime.dll' `
                -File `
                -Recurse |
            Where-Object {
                $_.FullName.Contains(
                    [IO.Path]::Combine('runtimes', 'win-x64', 'native'),
                    [StringComparison]::OrdinalIgnoreCase)
            } |
            Select-Object -First 1 -ExpandProperty FullName
        $copilotLicenseSource = Get-ChildItem `
                -LiteralPath $wrapperBuildDirectory `
                -Filter 'LICENSE.md' `
                -File `
                -Recurse |
            Where-Object {
                $_.FullName.Contains(
                    [IO.Path]::Combine('runtimes', 'win-x64', 'native'),
                    [StringComparison]::OrdinalIgnoreCase)
            } |
            Select-Object -First 1 -ExpandProperty FullName
        $mxcFfiSource = Join-Path `
            $cargoTargetDirectory `
            "$nativeTarget\$nativeProfile\mxc_ffi.dll"
        $plmSource = Join-Path `
            $cargoTargetDirectory `
            "$nativeTarget\$nativeProfile\plm.exe"
        $bindingsSource = Join-Path `
            $RepositoryRoot `
            'microsoft\mxc\sdk\dotnet\Microsoft.Mxc.Sdk\Native\NativeMethods.g.cs'
        $mxcLicenseSource = Join-Path $RepositoryRoot 'microsoft\mxc\LICENSE.md'
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

        $copilotInputRoots = @(
            (Join-Path $productionIntermediateDirectory 'copilot-cli')
            (Join-Path $dotnetArtifactsDirectory 'obj\Phantom.Workspaces.Llm.Core\release_win-x64\copilot-cli')
        )
        $copilotInputRoot = $copilotInputRoots |
            Where-Object { Test-Path -LiteralPath $_ -PathType Container } |
            Select-Object -First 1
        if ([string]::IsNullOrWhiteSpace($copilotInputRoot))
        {
            throw "The production publish did not retain its checksum-verified Copilot CLI inputs."
        }
        $copilotArchiveMatches = @(
            Get-ChildItem `
                -LiteralPath $copilotInputRoot `
                -Filter 'copilot.tgz' `
                -File `
                -Recurse
        )
        if ($copilotArchiveMatches.Count -ne 1)
        {
            throw "The wrapper prerequisite must resolve exactly one Copilot CLI source archive; found $($copilotArchiveMatches.Count)."
        }
        $copilotInputDirectory = $copilotArchiveMatches[0].Directory.FullName
        if ($copilotArchiveMatches[0].Directory.Name -ne $copilotCliPlatform -or
            $copilotArchiveMatches[0].Directory.Parent.Name -ne $copilotCliVersion)
        {
            throw "The Copilot CLI input path does not match SDK version $copilotCliVersion and platform $copilotCliPlatform."
        }
        $copilotArchivePath = $copilotArchiveMatches[0].FullName
        $copilotChecksumsPath = Join-Path $copilotInputDirectory 'SHA256SUMS.txt'
        $copilotInputExecutable = Join-Path `
            $copilotInputDirectory `
            "prebuilds\$copilotCliPlatform\copilot-runtime.exe"
        $copilotInputRuntimeLibrary = Join-Path `
            $copilotInputDirectory `
            "prebuilds\$copilotCliPlatform\runtime.node"
        $copilotInputLicense = Join-Path $copilotInputDirectory 'LICENSE.md'
        foreach ($copilotInput in @(
            $copilotChecksumsPath
            $copilotInputExecutable
            $copilotInputRuntimeLibrary
            $copilotInputLicense))
        {
            if (-not (Test-Path -LiteralPath $copilotInput -PathType Leaf))
            {
                throw "The Copilot CLI source input is incomplete: '$copilotInput'."
            }
        }

        $copilotAssetName = "github-copilot-$copilotCliVersion-$copilotCliPlatform.tgz"
        $copilotChecksumPattern =
            "^(?<hash>[0-9a-fA-F]{64})[\t ]+\*?$([Regex]::Escape($copilotAssetName))[\t ]*$"
        $copilotChecksumMatches = @(
            Get-Content -LiteralPath $copilotChecksumsPath |
                Where-Object { $_ -match $copilotChecksumPattern }
        )
        if ($copilotChecksumMatches.Count -ne 1 -or
            $copilotChecksumMatches[0] -notmatch $copilotChecksumPattern)
        {
            throw "The Copilot CLI checksum manifest must contain exactly one entry for '$copilotAssetName'."
        }
        $copilotCliArchiveSha256 = (Get-Sha256 $copilotArchivePath)
        if ($copilotCliArchiveSha256 -ne $Matches.hash.ToLowerInvariant())
        {
            throw "The Copilot CLI source archive hash does not match SHA256SUMS.txt."
        }
        $copilotCliChecksumsSha256 = Get-Sha256 $copilotChecksumsPath
        $copilotCliDownloadUrl =
            "$copilotCliReleaseBaseUrl/v$copilotCliVersion/$copilotAssetName"
        $copilotCliChecksumsUrl =
            "$copilotCliReleaseBaseUrl/v$copilotCliVersion/SHA256SUMS.txt"

        $requiredSources = @(
            $wrapperSource
            $copilotSource
            $copilotRuntimeSource
            $copilotLicenseSource
            $mxcFfiSource
            $plmSource
            $mxcLicenseSource
            $bindingsSource
            $runtimeConfigSource
            $appAssemblySource
            $mxcSdkAssemblySource
            $wrapperAssemblySource
            $llmCoreAssemblySource
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
        if ((Get-Sha256 $copilotSource) -ne
            (Get-Sha256 $copilotInputExecutable))
        {
            throw "The published copilot.exe does not match the checksum-verified CLI source."
        }
        if ((Get-Sha256 $copilotRuntimeSource) -ne
            (Get-Sha256 $copilotInputRuntimeLibrary))
        {
            throw "The published copilot_runtime.dll does not match the checksum-verified CLI source."
        }
        if ((Get-Sha256 $copilotLicenseSource) -ne
            (Get-Sha256 $copilotInputLicense))
        {
            throw "The published Copilot LICENSE.md does not match the checksum-verified CLI source."
        }

        $artifactSources = [ordered]@{
            'prepared/Microsoft.Mxc.Sdk.dll' = $mxcSdkAssemblySource
            'prepared/Phantom.Workspaces.Llm.Core.dll' = $llmCoreAssemblySource
            'prepared/Phantom.Workspaces.dll' = $appAssemblySource
            'prepared/phantom-copilot-wrapper.exe' = $wrapperSource
            'prepared/phantom-copilot-wrapper.dll' = $wrapperAssemblySource
            'prepared/copilot.exe' = $copilotSource
            'prepared/copilot_runtime.dll' = $copilotRuntimeSource
            'prepared/LICENSE.md' = $copilotLicenseSource
            'prepared/mxc_ffi.dll' = $mxcFfiSource
            'prepared/plm.exe' = $plmSource
            'prepared/MXC-LICENSE.md' = $mxcLicenseSource
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

        $cacheKey = Get-PreparedCacheKey `
            -SourceFingerprint $sourceFingerprint `
            -CopilotSdkPackageVersion $copilotSdkPackageVersion `
            -CopilotSdkPackageSha512 $copilotSdkPackageSha512 `
            -CopilotCliVersion $copilotCliVersion `
            -CopilotCliPlatform $copilotCliPlatform `
            -CopilotCliDownloadUrl $copilotCliDownloadUrl `
            -CopilotCliChecksumsUrl $copilotCliChecksumsUrl `
            -CopilotCliArchiveSha256 $copilotCliArchiveSha256 `
            -CopilotCliChecksumsSha256 $copilotCliChecksumsSha256 `
            -Artifacts @($artifacts)
        $manifest = [ordered]@{
            SchemaVersion = 5
            CacheKey = $cacheKey
            SourceFingerprint = $sourceFingerprint
            AppReleaseVersion = $appReleaseVersion
            AppAssemblyVersion = [Reflection.AssemblyName]::GetAssemblyName(
                $appAssemblySource).Version.ToString(3)
            AppFileVersion = ([Version] (
                [Diagnostics.FileVersionInfo]::GetVersionInfo(
                    $appAssemblySource).FileVersion)).ToString(3)
            AppInformationalVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo(
                $appAssemblySource).ProductVersion
            AppExecutableFileVersion = ([Version] (
                [Diagnostics.FileVersionInfo]::GetVersionInfo(
                    $appExecutableSource).FileVersion)).ToString(3)
            AppExecutableProductVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo(
                $appExecutableSource).ProductVersion
            MxcSdkProjectVersion = $mxcSdkProjectVersion
            MxcSdkAssemblyVersion = [Reflection.AssemblyName]::GetAssemblyName(
                $mxcSdkAssemblySource).Version.ToString(3)
            CopilotWrapperAssemblyVersion = [Reflection.AssemblyName]::GetAssemblyName(
                $wrapperAssemblySource).Version.ToString(3)
            LlmCoreAssemblyVersion = [Reflection.AssemblyName]::GetAssemblyName(
                $llmCoreAssemblySource).Version.ToString(3)
            ReleaseArchiveName = $releaseArchiveName
            Configuration = $configuration
            RuntimeIdentifier = $runtimeIdentifier
            NativeTarget = $nativeTarget
            NativeProfile = $nativeProfile
            NativePackages = $nativePackages
            NativeFeatures = $nativeFeatures
            CopilotSdkPackageVersion = $copilotSdkPackageVersion
            CopilotSdkPackageSha512 = $copilotSdkPackageSha512
            CopilotCliVersion = $copilotCliVersion
            CopilotCliPlatform = $copilotCliPlatform
            CopilotCliDownloadUrl = $copilotCliDownloadUrl
            CopilotCliChecksumsUrl = $copilotCliChecksumsUrl
            CopilotCliArchiveSha256 = $copilotCliArchiveSha256
            CopilotCliChecksumsSha256 = $copilotCliChecksumsSha256
            CopilotCliGraphPath = [IO.Path]::GetRelativePath(
                $dotnetArtifactsDirectory,
                $copilotSource).Replace('\', '/')
            ContainerRuntimeConfigGraphPath = [IO.Path]::GetRelativePath(
                $dotnetArtifactsDirectory,
                $runtimeConfigSource).Replace('\', '/')
            RustcIdentity = $rustcIdentity
            CargoIdentity = $cargoIdentity
            DotNetIdentity = $dotnetIdentity
            ProductionPublishValidated = $true
            ProductionPublishWrapperPath = [IO.Path]::GetRelativePath(
                $publishDirectory,
                $wrapperSource).Replace('\', '/')
            Artifacts = $artifacts
        }
        $manifest | ConvertTo-Json -Depth 5 |
            Set-Content -LiteralPath (Join-Path $stagingDirectory 'prerequisite.json') -Encoding utf8
        Set-Content `
            -LiteralPath (Join-Path $stagingDirectory 'prerequisite.fingerprint') `
            -Value $cacheKey `
            -Encoding utf8NoBOM

        Remove-Item -LiteralPath $cargoTargetDirectory -Recurse -Force
        Remove-Item -LiteralPath $dotnetArtifactsDirectory -Recurse -Force
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
        Remove-Item -LiteralPath $releaseAssetsDirectory -Recurse -Force
        Remove-Item -LiteralPath $productionIntermediateDirectory -Recurse -Force

        $cacheDirectory = Join-Path $CacheRoot $cacheKey.Substring(0, 16)
        if (Test-CompletedCache $cacheDirectory $CacheRoot $sourceFingerprint)
        {
            Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
        }
        else
        {
            if (Test-Path -LiteralPath $cacheDirectory)
            {
                Remove-Item -LiteralPath $cacheDirectory -Recurse -Force
            }
            [IO.Directory]::Move($stagingDirectory, $cacheDirectory)
        }
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
    if ($productionIntermediateDirectory -and
        (Test-Path -LiteralPath $productionIntermediateDirectory))
    {
        Remove-Item -LiteralPath $productionIntermediateDirectory -Recurse -Force
    }
    if ($ownsMutex)
    {
        $mutex.ReleaseMutex()
    }
    $mutex.Dispose()
}
