<#
.SYNOPSIS
    Publishes the Missa media recorder for deployment to the VM.

.DESCRIPTION
    Produces a framework-dependent win-x64 build (the VM needs the .NET 8 ASP.NET Core
    runtime installed). Pass -SelfContained to bundle the runtime instead, so the VM needs
    no .NET installed (larger output).

    After publishing, copy the output folder to the VM (e.g. C:\Media\Recorder) and run
    install-service.ps1 there as Administrator.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -SelfContained -OutDir .\publish
#>
param(
    [string]$OutDir = ".\publish",
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$projectDir = $PSScriptRoot
$csproj = Join-Path $projectDir "Recorder.Api.csproj"

if (-not (Test-Path $csproj)) {
    throw "Recorder.Api.csproj not found next to this script ($projectDir)."
}

# Resolve the output dir to an absolute path so 'dotnet publish -o' is unambiguous.
$absOut = [System.IO.Path]::GetFullPath((Join-Path $projectDir $OutDir))

Write-Host "Publishing Recorder.Api -> $absOut" -ForegroundColor Cyan
Write-Host ("Mode: {0}" -f ($(if ($SelfContained) { "self-contained (no .NET runtime needed on VM)" } else { "framework-dependent (VM needs .NET 8 ASP.NET Core runtime)" }))) -ForegroundColor Cyan

if (Test-Path $absOut) {
    Write-Host "Cleaning existing output folder..." -ForegroundColor DarkGray
    Remove-Item $absOut -Recurse -Force
}

$dotnetArgs = @(
    "publish", $csproj,
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", $(if ($SelfContained) { "true" } else { "false" }),
    "-o", $absOut,
    "--nologo"
)

& dotnet @dotnetArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

# --- Copy the Skype real-time media native runtime (NativeMedia.dll, skypert.dll, RtmCodecs.dll,
#     etc.). These native binaries do NOT flow through `dotnet publish`, so without this step the
#     media bot loads but fails the moment it tries to capture audio. Prefer the TFM that matches
#     the build (net8.0); fall back to whatever NativeMedia.dll the package ships. -->
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE ".nuget\packages" }
$skypePkg  = Join-Path $nugetRoot "microsoft.skype.bots.media"
if (-not (Test-Path $skypePkg)) {
    throw "microsoft.skype.bots.media package not found under '$nugetRoot'. Restore the project first."
}
# WARNING: the native DLLs from THIS package version (1.31.0.225-preview) fail to load on the
# VM's Windows Server 2022 with DllNotFoundException (0x8007007E), even with the VC++ redist
# present. The proven-working native set currently lives ONLY on the VM. So these copied DLLs
# are a best-effort bootstrap for a from-scratch install; update-recorder.ps1 PRESERVES the
# VM's working native DLLs and does not overwrite them. (net6.0 vs net8.0 is irrelevant here -
# NativeMedia.dll is byte-identical between them.)
$nativeDll =
    Get-ChildItem $skypePkg -Recurse -Filter "NativeMedia.dll" |
    Sort-Object { if ($_.DirectoryName -match "net6") { 0 } elseif ($_.DirectoryName -match "net8") { 1 } else { 2 } } |
    Select-Object -First 1
if (-not $nativeDll) { throw "NativeMedia.dll not found inside '$skypePkg'." }

Write-Host "Copying native media runtime from: $($nativeDll.DirectoryName)" -ForegroundColor DarkGray
Copy-Item (Join-Path $nativeDll.DirectoryName "*") -Destination $absOut -Force
if (-not (Test-Path (Join-Path $absOut "NativeMedia.dll"))) {
    throw "NativeMedia.dll did not land in the publish output - media capture would fail."
}

Write-Host ""
Write-Host "Publish complete: $absOut" -ForegroundColor Green
Write-Host "Native media runtime included (NativeMedia.dll present)." -ForegroundColor Green
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Copy this folder to the VM (e.g. C:\Media\Recorder)."
Write-Host "  2. Place your runtime config as '.localConfigs' inside that folder"
Write-Host "     (ASPNETCORE_URLS=https://<fqdn>:9442, Azure Speech key, RECORDER_SHARED_SECRET, media-bot settings)."
Write-Host "  3. On the VM, run install-service.ps1 as Administrator."
