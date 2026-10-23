function Get-TestExecutionPlan
{
    param(
        [string] $Target,
        [string] $Filter,
        [string[]] $TestNames,
        [string] $Mode,
        [string] $PerTestHangTimeout,
        [string] $RepositoryRoot
    )

    $contract = Get-Content (Join-Path $PSScriptRoot 'cold-publish-test-contract.json') -Raw | ConvertFrom-Json
    if ($contract.LifecycleSeconds -le (2 * $contract.CleanSeconds + $contract.PrepareSeconds + $contract.PublishSeconds) -or
        $contract.WatchdogSeconds -le $contract.LifecycleSeconds)
    {
        throw 'Cold publish watchdog must outlast the bounded lifecycle and its stage/cleanup budgets.'
    }

    $coldProject = Join-Path $RepositoryRoot 'Phantom.Workspaces.Install.Tests\Phantom.Workspaces.Install.Tests.csproj'
    $TestNames = @($TestNames | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $matchesNames = -not $TestNames -or @($TestNames | Where-Object {
        $contract.TestName.IndexOf($_, [StringComparison]::OrdinalIgnoreCase) -ge 0
    }).Count -gt 0
    $partitionCold = ($Target -eq $coldProject -or $Target.EndsWith('.slnx')) -and
        $matchesNames -and ($Mode -ne 'fast' -or $TestNames)

    $ordinaryFilter = $Filter
    if ($partitionCold)
    {
        $ordinaryFilter = if ($Filter) { "($Filter)&FullyQualifiedName!=$($contract.TestName)" } else { "FullyQualifiedName!=$($contract.TestName)" }
    }
    [pscustomobject]@{ Name = 'ordinary'; Target = $Target; Filter = $ordinaryFilter; HangTimeout = $PerTestHangTimeout }
    if ($partitionCold)
    {
        $coldFilter = "FullyQualifiedName=$($contract.TestName)"
        if ($Filter) { $coldFilter = "($Filter)&$coldFilter" }
        [pscustomobject]@{ Name = 'cold-publish'; Target = $coldProject; Filter = $coldFilter; HangTimeout = "$($contract.WatchdogSeconds)s" }
    }
}
