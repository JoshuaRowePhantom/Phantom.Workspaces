param(
    [Parameter(Mandatory)][string] $OwnedProcessIds,
    [Parameter(Mandatory)][string] $Stage,
    [Parameter(Mandatory)][string] $OutputDirectory,
    [string] $NativeCompilerName = 'crossgen2.exe'
)

$ErrorActionPreference = 'Stop'
$ids = @($OwnedProcessIds.Split(',') | ForEach-Object { [int] $_ })
if ($ids.Count -eq 0 -or @($ids | Where-Object { $_ -le 0 }).Count -gt 0) {
    throw 'Only explicit owned job process IDs may be captured.'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$filter = ($ids | ForEach-Object { "ProcessId = $_" }) -join ' OR '
$snapshot = @(Get-CimInstance Win32_Process -Filter $filter | Select-Object ProcessId, ParentProcessId, Name, CreationDate, CommandLine, KernelModeTime, UserModeTime, WorkingSetSize)
@{ Stage = $Stage; CapturedAt = (Get-Date -Format o); OwnedProcessIds = $ids; Processes = $snapshot } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'processes.json')

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class OwnedTimeoutDump {
    [DllImport("dbghelp.dll", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MiniDumpWriteDump(
        IntPtr process, uint processId, SafeFileHandle file, uint dumpType,
        IntPtr exception, IntPtr streams, IntPtr callback);
}
'@

# Preserve the native compiler when present; otherwise capture the direct owned command.
$targets = @($snapshot | Where-Object { $_.Name -like $NativeCompilerName })
if ($targets.Count -eq 0) { $targets = @($snapshot | Where-Object { $_.ProcessId -eq $ids[0] }) }
if ($targets.Count -eq 0) { throw 'Owned command exited before native evidence capture; process snapshot was preserved.' }
$captureErrors = @()
foreach ($target in $targets) {
    $process = $null
    try {
        $process = Get-Process -Id $target.ProcessId
        if ([Math]::Abs(($process.StartTime - $target.CreationDate).TotalMilliseconds) -gt 1) {
            throw 'Process identity changed during evidence capture; refusing to dump a reused PID.'
        }
        $path = Join-Path $OutputDirectory "$($target.Name)-$($target.ProcessId).dmp"
        $file = [IO.File]::Create($path)
        try {
            # Native threads/stacks/modules, including unloaded modules and thread metadata.
            if (-not [OwnedTimeoutDump]::MiniDumpWriteDump(
                $process.Handle, $target.ProcessId, $file.SafeFileHandle, 0x1020,
                [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]::Zero)) {
                throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
            }
        } finally { $file.Dispose() }
        Get-FileHash -LiteralPath $path -Algorithm SHA256 |
            ConvertTo-Json | Set-Content -LiteralPath "$path.sha256.json"
    } catch {
        $captureErrors += "PID $($target.ProcessId) ($($target.Name)): $($_.Exception.Message)"
    } finally {
        if ($null -ne $process) { $process.Dispose() }
    }
}
ConvertTo-Json -InputObject $captureErrors |
    Set-Content -LiteralPath (Join-Path $OutputDirectory 'capture-errors.json')
if ($captureErrors.Count -gt 0) {
    throw "Native evidence is partial in ${OutputDirectory}: $($captureErrors -join '; ')"
}
Write-Output "Pre-termination native evidence preserved for stage '$Stage': $OutputDirectory"
