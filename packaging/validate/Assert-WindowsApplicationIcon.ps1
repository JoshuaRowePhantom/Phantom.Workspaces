param(
    [Parameter(Mandatory = $true)]
    [string] $ApplicationExecutablePath
)

$ErrorActionPreference = 'Stop'

if (-not [OperatingSystem]::IsWindows()) {
    throw 'Application icon resource validation requires Windows.'
}

$resolvedPath = (Resolve-Path -LiteralPath $ApplicationExecutablePath).Path

if (-not ('Phantom.Workspaces.Packaging.NativeApplicationIcon' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace Phantom.Workspaces.Packaging
{
    public static class NativeApplicationIcon
    {
        private const uint LoadLibraryAsDataFile = 0x00000002;
        private const uint LoadLibraryAsImageResource = 0x00000020;

        public delegate bool EnumResourceNameProc(IntPtr module, IntPtr type, IntPtr name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumResourceNames(
            IntPtr module,
            IntPtr type,
            EnumResourceNameProc callback,
            IntPtr parameter);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr module);

        public static int CountGroupIcons(string executablePath)
        {
            IntPtr module = LoadLibraryEx(
                executablePath,
                IntPtr.Zero,
                LoadLibraryAsDataFile | LoadLibraryAsImageResource);
            if (module == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "LoadLibraryEx failed with Win32 error " + Marshal.GetLastWin32Error());
            }

            try
            {
                int count = 0;
                EnumResourceNameProc callback = (_, _, _) =>
                {
                    count++;
                    return true;
                };
                bool enumerated = EnumResourceNames(module, new IntPtr(14), callback, IntPtr.Zero);
                GC.KeepAlive(callback);
                if (!enumerated)
                {
                    return 0;
                }

                return count;
            }
            finally
            {
                FreeLibrary(module);
            }
        }
    }
}
'@
}

$groupIconCount = [Phantom.Workspaces.Packaging.NativeApplicationIcon]::CountGroupIcons($resolvedPath)
if ($groupIconCount -lt 1) {
    throw "No RT_GROUP_ICON resource was found in '$resolvedPath'."
}

Write-Host "Application icon validation passed: $groupIconCount RT_GROUP_ICON resource(s) in '$resolvedPath'."
