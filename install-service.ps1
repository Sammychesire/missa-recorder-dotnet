<#
.SYNOPSIS
    Installs (or re-installs) the Missa media recorder as a Native Windows Service.

.DESCRIPTION
    Registers Recorder.Api.exe with the Service Control Manager so it:
      * starts automatically on boot (survives reboot and RDP logoff),
      * restarts automatically after a crash or non-zero exit,
      * shuts down gracefully on stop (so a live call can drain).

    Run this ON THE VM, in an ELEVATED PowerShell (Run as Administrator), after copying
    the published folder over. Safe to re-run to redeploy: it stops + recreates the service.

.PARAMETER AppDir
    Folder containing the published Recorder.Api.exe (e.g. C:\Media\Recorder).

.PARAMETER ServiceName
    Windows service name. Default: MissaRecorder.

.EXAMPLE
    .\install-service.ps1 -AppDir C:\Media\Recorder
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$AppDir,

    [string]$ServiceName = "MissaRecorder",
    [string]$DisplayName = "Missa Media Recorder",
    [string]$Description = "Application-hosted Teams media bot + Azure Speech recorder for Mela."
)

$ErrorActionPreference = "Stop"

# --- Must be elevated: SCM registration requires Administrator. ---
$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw "This script must be run in an elevated PowerShell (Run as Administrator)."
}

$exePath = Join-Path $AppDir "Recorder.Api.exe"
if (-not (Test-Path $exePath)) {
    throw "Recorder.Api.exe not found in '$AppDir'. Publish first, then copy the folder here."
}
$exePath = (Resolve-Path $exePath).Path

# --- Warn (don't fail) if runtime config is missing: the app reads .localConfigs from its
#     own folder now, so config must live alongside the exe (or be set as machine env vars). ---
$localConfigs = Join-Path $AppDir ".localConfigs"
if (-not (Test-Path $localConfigs)) {
    Write-Warning "No .localConfigs found in '$AppDir'. The service will start with defaults only."
    Write-Warning "Add .localConfigs (ASPNETCORE_URLS=https://<fqdn>:9442, Azure Speech key, RECORDER_SHARED_SECRET, media-bot settings) before relying on it."
}

# --- Remove any existing service so this is an idempotent redeploy. ---
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping and removing existing service '$ServiceName'..." -ForegroundColor DarkGray
    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        # Give the media bot a moment to drain / release ports before deletion.
        $existing.WaitForStatus('Stopped', '00:00:30') 2>$null
    }
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

# --- Create the service (LocalSystem, auto-start). BinaryPathName is quoted for spaces. ---
Write-Host "Creating service '$ServiceName' -> $exePath" -ForegroundColor Cyan
New-Service `
    -Name $ServiceName `
    -BinaryPathName ('"{0}"' -f $exePath) `
    -DisplayName $DisplayName `
    -Description $Description `
    -StartupType Automatic | Out-Null

# --- Auto-restart on failure (New-Service can't set this; use sc.exe). ---
#   reset= 86400  -> failure counter resets to 0 after a day of health
#   actions=      -> restart 5s after 1st & 2nd failure, 10s after the 3rd+
& sc.exe failure     $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/10000 | Out-Null
#   failureflag 1 -> apply the recovery actions on ANY non-zero exit, not just a hard crash
& sc.exe failureflag $ServiceName 1 | Out-Null

# --- Start it. ---
Write-Host "Starting '$ServiceName'..." -ForegroundColor Cyan
Start-Service -Name $ServiceName

Start-Sleep -Seconds 3
$svc = Get-Service -Name $ServiceName
Write-Host ""
Write-Host ("Service '{0}' is {1} (StartType: {2})." -f $svc.Name, $svc.Status, $svc.StartType) -ForegroundColor Green
Write-Host "Useful commands:" -ForegroundColor Yellow
Write-Host "  Get-Service $ServiceName"
Write-Host "  Restart-Service $ServiceName"
Write-Host "  sc.exe qfailure $ServiceName        # view recovery config"
Write-Host "  Get-EventLog -LogName Application -Source $ServiceName -Newest 20   # service/host logs"
