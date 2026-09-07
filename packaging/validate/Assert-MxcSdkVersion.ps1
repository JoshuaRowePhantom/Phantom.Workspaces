[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $NativeLibraryPath,
    [string] $ManagedOutputPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).ProviderPath
$managedProject = Join-Path $repositoryRoot 'microsoft\mxc\sdk\dotnet\Microsoft.Mxc.Sdk\Microsoft.Mxc.Sdk.csproj'
$managedOutput = if ($ManagedOutputPath)
{
    $ManagedOutputPath
}
else
{
    Join-Path $repositoryRoot 'microsoft\mxc\sdk\dotnet\Microsoft.Mxc.Sdk\bin'
}

[xml] $project = Get-Content -LiteralPath $managedProject -Raw
$managedVersion = ([string] $project.Project.PropertyGroup.Version).Trim()
$managedAssembly = Get-ChildItem -LiteralPath $managedOutput -Filter 'Microsoft.Mxc.Sdk.dll' -Recurse -File |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if (-not $managedAssembly)
{
    throw "Built Microsoft.Mxc.Sdk.dll not found under '$managedOutput'. Build the solution before validating the payload."
}
$managedAssemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($managedAssembly.FullName).Version.ToString(3)
if ($managedVersion -ne $managedAssemblyVersion)
{
    throw "Built managed SDK version '$managedAssemblyVersion' does not match project version '$managedVersion'."
}
if (-not (Test-Path -LiteralPath $NativeLibraryPath -PathType Leaf))
{
    throw "MXC native library not found: '$NativeLibraryPath'."
}

if (-not ('MxcNativeVersionReader' -as [type]))
{
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class MxcNativeVersionReader
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr MxcVersion();

    public static string Read(string path)
    {
        IntPtr library = NativeLibrary.Load(path);
        try
        {
            IntPtr export = NativeLibrary.GetExport(library, "mxc_version");
            var version = Marshal.GetDelegateForFunctionPointer<MxcVersion>(export);
            return Marshal.PtrToStringUTF8(version()) ?? string.Empty;
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }
}
'@
}

$resolvedNativeLibrary = (Resolve-Path -LiteralPath $NativeLibraryPath).ProviderPath
$nativeVersion = [MxcNativeVersionReader]::Read($resolvedNativeLibrary)
if ([string]::IsNullOrWhiteSpace($nativeVersion))
{
    throw "MXC native library '$resolvedNativeLibrary' reported an empty version."
}
if ($managedVersion -ne $nativeVersion)
{
    throw "MXC native runtime version '$nativeVersion' does not match managed SDK version '$managedVersion'."
}

Write-Host "MXC managed assembly/native runtime version validated: $managedVersion"
