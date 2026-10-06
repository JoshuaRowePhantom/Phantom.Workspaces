[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $FileName,
    [Parameter()]
    [string[]] $ProcessArguments = @(),
    [Parameter(Mandatory)]
    [ValidateRange(1, 600)]
    [int] $TimeoutSeconds
)

$ErrorActionPreference = 'Stop'
$startInfo = [System.Diagnostics.ProcessStartInfo]::new($FileName)
$startInfo.UseShellExecute = $false
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
foreach ($argument in $ProcessArguments)
{
    [void] $startInfo.ArgumentList.Add($argument)
}

$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $startInfo
$deadline = [System.Threading.CancellationTokenSource]::new(
    [TimeSpan]::FromSeconds($TimeoutSeconds))
$started = $false
try
{
    if (-not $process.Start())
    {
        throw "Could not start '$FileName'."
    }
    $started = $true
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    try
    {
        [void] $process.WaitForExitAsync($deadline.Token).GetAwaiter().GetResult()
        $standardOutput = $stdout.WaitAsync($deadline.Token).GetAwaiter().GetResult()
        $standardError = $stderr.WaitAsync($deadline.Token).GetAwaiter().GetResult()
    }
    catch [System.OperationCanceledException]
    {
        throw "Process '$FileName' exceeded its $TimeoutSeconds-second end-to-end deadline."
    }
    [pscustomobject]@{
        ExitCode = $process.ExitCode
        StandardOutput = $standardOutput
        StandardError = $standardError
    }
}
finally
{
    if ($started -and -not $process.HasExited)
    {
        try
        {
            $process.Kill($true)
            if (-not $process.WaitForExit(5000))
            {
                throw "Process '$FileName' did not terminate after its end-to-end deadline."
            }
        }
        catch [System.InvalidOperationException]
        {
            # The process exited between HasExited and Kill.
        }
    }
    $deadline.Dispose()
    $process.Dispose()
}
