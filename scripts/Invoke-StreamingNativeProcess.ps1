[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $FileName,
    [string[]] $ProcessArguments = @()
)

$ErrorActionPreference = 'Stop'
$output = [Collections.Generic.List[object]]::new()
& $FileName @ProcessArguments 2>&1 | ForEach-Object {
    $output.Add($_)
    Write-Host $_
}
$nativeExitCode = $LASTEXITCODE
[pscustomobject]@{
    ExitCode = $nativeExitCode
    Output = $output.ToArray()
}
