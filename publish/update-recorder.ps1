<#
.SYNOPSIS
    Redeploys a new recorder build into the live MissaRecorder service folder in one step.

.DESCRIPTION
    Run this ON THE VM, elevated, with NO live meeting in progress (it stops the service).

    Safely swaps in fresh binaries from a staging folder while PRESERVING the three things a
    fresh publish would otherwise clobber:
      * .localConfigs            - your secrets/settings (never in a publish)
      * the native media DLLs    - the VM's proven-working set; the cache's preview DLLs fail
                                   to load here (see recorder-native-media notes)
      * logs\ and recorder_debug\ - runtime output / diagnostics history

    Everything else (Recorder.Api.dll, deps, managed dependencies) is overwritten with the new
    build. A timestamped backup is taken first; on any failure the previous version is restored.

.PARAMETER NewBuild
    Folder containing the freshly published build (copied to the VM), e.g. C:\Media\Recorder\staging.

.PARAMETER AppDir
    The live service folder. Default: C:\Media\Recorder\missa-recorder-deploy.

.EXAMPLE
    .\update-recorder.ps1 -NewBuild C:\Media\Recorder\staging
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$NewBuild,

    [string]$AppDir      = "C:\Media\Recorder\missa-recorder-deploy",
    [string]$ServiceName = "MissaRecorder"
)

$ErrorActionPreference = "Stop"

# --- Must be elevated (Stop/Start-Service + writing under C:\Media). ---
$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw "Run this in an elevated PowerShell (Run as Administrator)." }

# --- Validate inputs. ---
if (-not (Test-Path (Join-Path $NewBuild "Recorder.Api.exe"))) {
    throw "No Recorder.Api.exe in -NewBuild '$NewBuild'. Point it at a published folder."
}
if (-not (Test-Path (Join-Path $AppDir "Recorder.Api.exe"))) {
    throw "No existing install at -AppDir '$AppDir'. Use install-service.ps1 for a first-time install."
}

# The proven native DLLs (preserved from the live folder, NOT taken from the new build).
$nativeDlls = @(
    'NativeMedia.dll','skypert.dll','RtmPal.dll','RtmCodecs.dll','RtmMvrCs.dll',
    'SlimCV.dll','Ijwhost.dll','MediaPerf.dll','IfxMetricExtensions.dll'
)

$backupDir = "$AppDir-bak-{0:yyyyMMdd-HHmmss}" -f (Get-Date)

Write-Host "Stopping '$ServiceName'..." -ForegroundColor Cyan
$svc = Get-Service -Name $ServiceName -ErrorAction Stop
if ($svc.Status -ne 'Stopped') {
    Stop-Service -Name $ServiceName -Force
    $svc.WaitForStatus('Stopped', '00:00:30')
}

Write-Host "Backing up current build -> $backupDir" -ForegroundColor Cyan
robocopy $AppDir $backupDir /E /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Backup failed (robocopy exit $LASTEXITCODE). Aborted before touching the live folder." }

try {
    # Copy new build over the live folder, but do NOT overwrite the preserved items:
    #   /XF excludes files by name; /XD excludes whole directories.
    Write-Host "Applying new build from $NewBuild (preserving config, native DLLs, runtime output)..." -ForegroundColor Cyan
    robocopy $NewBuild $AppDir /E /XF ".localConfigs" @nativeDlls /XD "logs" "recorder_debug" /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy of new build failed (exit $LASTEXITCODE)." }

    # Sanity: preserved items must still be present.
    if (-not (Test-Path (Join-Path $AppDir ".localConfigs"))) { throw "Post-copy check failed: .localConfigs is missing." }
    foreach ($d in $nativeDlls) {
        if (-not (Test-Path (Join-Path $AppDir $d))) { throw "Post-copy check failed: native DLL '$d' is missing." }
    }

    Write-Host "Starting '$ServiceName'..." -ForegroundColor Cyan
    Start-Service -Name $ServiceName
    (Get-Service $ServiceName).WaitForStatus('Running', '00:00:30')

    Start-Sleep -Seconds 3
    $listening = Get-NetTCPConnection -State Listen -LocalPort 9442,5000 -ErrorAction SilentlyContinue
    if (-not $listening) { throw "Service is Running but 9442/5000 are not listening - it may be crash-looping." }

    Write-Host ""
    Write-Host "Update complete. '$ServiceName' is Running and listening on 9442/5000." -ForegroundColor Green
    Write-Host "Backup kept at: $backupDir  (delete once you've verified a test call)." -ForegroundColor DarkGray
    Write-Host "Tail logs: Get-Content '$AppDir\logs\recorder-$(Get-Date -Format yyyyMMdd).log' -Tail 100 -Wait" -ForegroundColor DarkGray
}
catch {
    Write-Warning "Update failed: $($_.Exception.Message)"
    Write-Warning "Rolling back to the previous build..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    robocopy $backupDir $AppDir /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    Start-Service -Name $ServiceName -ErrorAction SilentlyContinue
    Write-Warning "Rolled back from $backupDir and restarted '$ServiceName'. Investigate before retrying."
    throw
}
