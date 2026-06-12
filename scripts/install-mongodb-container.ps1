param(
    [string]$ContainerName = "phantom-mongodb",
    [string]$DataDirectory = ".\mongo-data",
    [int]$HostPort = 27017,
    [switch]$SkipDockerInstall,
    [string]$LogPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Test-IsAdministrator {
    $windowsIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $windowsPrincipal = [Security.Principal.WindowsPrincipal]::new($windowsIdentity)
    return $windowsPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Ensure-Elevation {
    if (Test-IsAdministrator) {
        return
    }

    Write-Host "Elevation required. Relaunching installer as administrator..."
    $argumentList = @(
        "-NoProfile"
        "-ExecutionPolicy", "Bypass"
        "-File", "`"$PSCommandPath`""
        "-ContainerName", "`"$ContainerName`""
        "-DataDirectory", "`"$DataDirectory`""
        "-HostPort", $HostPort
    )

    if ($SkipDockerInstall.IsPresent) {
        $argumentList += "-SkipDockerInstall"
    }

    if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
        $argumentList += @("-LogPath", "`"$LogPath`"")
    }

    $process = Start-Process -FilePath "powershell.exe" -ArgumentList $argumentList -Verb RunAs -PassThru -Wait
    exit $process.ExitCode
}

function Ensure-DockerDesktopInstalled {
    $dockerCommand = Get-Command docker -ErrorAction SilentlyContinue
    if ($dockerCommand) {
        Write-Host "Docker CLI found at '$($dockerCommand.Source)'."
        return
    }

    if ($SkipDockerInstall.IsPresent) {
        throw "Docker is not installed and -SkipDockerInstall was specified."
    }

    Write-Host "Docker Desktop not found. Installing with winget..."
    winget install --id Docker.DockerDesktop --accept-package-agreements --accept-source-agreements
}

function Ensure-DockerDaemonReady {
    Write-Host "Checking Docker daemon availability..."
    docker version | Out-Null
}

function Ensure-MongoContainer {
    param(
        [Parameter(Mandatory = $true)][string]$ResolvedDataDirectory
    )

    $containerId = docker ps -a --filter "name=^/${ContainerName}$" --format "{{.ID}}"
    if ([string]::IsNullOrWhiteSpace($containerId)) {
        Write-Host "Creating MongoDB container '$ContainerName' on port $HostPort."
        docker run --detach `
            --name $ContainerName `
            --publish "${HostPort}:27017" `
            --volume "${ResolvedDataDirectory}:/data/db" `
            mongo:latest | Out-Null
        return
    }

    $runningId = docker ps --filter "name=^/${ContainerName}$" --format "{{.ID}}"
    if ([string]::IsNullOrWhiteSpace($runningId)) {
        Write-Host "Starting existing MongoDB container '$ContainerName'."
        docker start $ContainerName | Out-Null
    }
    else {
        Write-Host "MongoDB container '$ContainerName' is already running."
    }
}

Ensure-Elevation

$resolvedLogPath = if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    Join-Path -Path (Split-Path -Parent $PSCommandPath) -ChildPath "install-mongodb-container-$timestamp.log"
}
else {
    [IO.Path]::GetFullPath($LogPath)
}

Start-Transcript -Path $resolvedLogPath -Force | Out-Null
try {
    $resolvedDataDirectory = [IO.Path]::GetFullPath($DataDirectory)
    if (-not (Test-Path $resolvedDataDirectory)) {
        Write-Host "Creating MongoDB data directory '$resolvedDataDirectory'."
        New-Item -ItemType Directory -Path $resolvedDataDirectory -Force | Out-Null
    }

    Ensure-DockerDesktopInstalled
    Ensure-DockerDaemonReady
    Ensure-MongoContainer -ResolvedDataDirectory $resolvedDataDirectory

    Write-Host "MongoDB container installation complete."
    Write-Host "Container name : $ContainerName"
    Write-Host "Host port      : $HostPort"
    Write-Host "Data directory : $resolvedDataDirectory"
    Write-Host "Log file       : $resolvedLogPath"
}
finally {
    Stop-Transcript | Out-Null
}
