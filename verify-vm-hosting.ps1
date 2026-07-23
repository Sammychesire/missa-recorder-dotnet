<#
.SYNOPSIS
  Verifies the VM-side hosting prerequisites for the Missa Recorder (Recorder.Api)
  running as an NSSM Windows Service. Prints PASS / FAIL / WARN / MANUAL for each
  item on the hosting checklist (1-31).

.DESCRIPTION
  Run this ON THE VM, in an ELEVATED PowerShell (Run as Administrator), after you
  have published the app and set up the service. It is read-only: it changes nothing.

  Items that can only be confirmed in the Azure control plane (static IP, NSG rules)
  are reported as MANUAL with the exact command to run.

.PARAMETER PublishDir     Folder the app is published to (contains Recorder.Api.exe).
.PARAMETER ServiceName    NSSM service name.
.PARAMETER ServiceAccount Local service account the service runs as.
.PARAMETER Fqdn           Public FQDN the TLS cert is issued for. Auto-read from config if omitted.
.PARAMETER CertThumbprint Cert thumbprint. Auto-read from config if omitted.
.PARAMETER MediaPort      Public TCP media port (default 8445).
.PARAMETER SignalingPort  Public HTTPS signaling/API port (default 9442).
.PARAMETER LoopbackPort   Local Kestrel HTTP port (default 5000).
.PARAMETER LogDir         Directory NSSM writes stdout/stderr logs to.

.EXAMPLE
  .\verify-vm-hosting.ps1 -PublishDir C:\publish\recorder -ServiceAccount RecorderSvc
#>

[CmdletBinding()]
param(
    [string]$PublishDir     = "C:\publish\recorder",
    [string]$ServiceName    = "RecorderApi",
    [string]$ServiceAccount = "RecorderSvc",
    [string]$Fqdn           = "",
    [string]$CertThumbprint = "",
    [int]$MediaPort         = 8445,
    [int]$SignalingPort     = 9442,
    [int]$LoopbackPort      = 5000,
    [string]$LogDir         = "C:\logs\recorder"
)

$ErrorActionPreference = "Continue"
$script:results = @()

# ---- helpers -----------------------------------------------------------------

function Add-Result {
    param([int]$Num, [string]$Name, [string]$Status, [string]$Detail = "")
    $color = switch ($Status) {
        "PASS"   { "Green" }
        "FAIL"   { "Red" }
        "WARN"   { "Yellow" }
        "MANUAL" { "Cyan" }
        default  { "Gray" }
    }
    $tag = "[{0,-6}]" -f $Status
    Write-Host ("{0} {1,2}. {2}" -f $tag, $Num, $Name) -ForegroundColor $color
    if ($Detail) { Write-Host ("           -> {0}" -f $Detail) -ForegroundColor DarkGray }
    $script:results += [pscustomobject]@{ Num = $Num; Name = $Name; Status = $Status; Detail = $Detail }
}

function Get-ConfigValue {
    param([string]$Dir, [string]$Key)
    foreach ($f in @((Join-Path $Dir '.localConfigs'), (Join-Path $Dir 'env\.env.local'))) {
        if (Test-Path $f) {
            $m = Select-String -Path $f -Pattern ("^\s*{0}\s*=" -f [regex]::Escape($Key)) -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($m) { return ($m.Line -replace ("^\s*{0}\s*=\s*" -f [regex]::Escape($Key)), '').Trim().Trim('"') }
        }
    }
    return ''
}

function Get-NssmValue {
    param([string]$Svc, [string]$Param)
    $nssm = (Get-Command nssm -ErrorAction SilentlyContinue)
    if (-not $nssm) { return $null }
    try {
        $out = & nssm get $Svc $Param 2>$null
        if ($out) { return ($out -join "`n").Trim() }
    } catch {}
    return $null
}

Write-Host "`n=== Missa Recorder VM hosting verification ===" -ForegroundColor White
Write-Host ("PublishDir={0}  Service={1}  Account={2}" -f $PublishDir, $ServiceName, $ServiceAccount) -ForegroundColor DarkGray

# Auto-resolve FQDN / thumbprint from config if not supplied.
if (-not $Fqdn)           { $Fqdn           = Get-ConfigValue $PublishDir 'MEDIA_BOT_SERVICE_FQDN' }
if (-not $CertThumbprint) { $CertThumbprint = (Get-ConfigValue $PublishDir 'MEDIA_BOT_CERT_THUMBPRINT') }
$CertThumbprint = ($CertThumbprint -replace '\s', '').ToUpper()
Write-Host ("FQDN={0}  CertThumbprint={1}`n" -f ($(if ($Fqdn) { $Fqdn } else { '(unknown)' })), ($(if ($CertThumbprint) { $CertThumbprint } else { '(unknown)' }))) -ForegroundColor DarkGray

# ---- VM / OS -----------------------------------------------------------------

try {
    $os = Get-CimInstance Win32_OperatingSystem
    $arch = $os.OSArchitecture
    $isServer = $os.Caption -match 'Server'
    $st = if ($arch -match '64') { "PASS" } else { "FAIL" }
    Add-Result 1 "Windows Server (x64) + RDP" $st ("{0} ({1}){2}" -f $os.Caption, $arch, $(if (-not $isServer) { ' - NOT a Server SKU' } else { '' }))
    try {
        $deny = (Get-ItemProperty 'HKLM:\System\CurrentControlSet\Control\Terminal Server' -Name fDenyTSConnections -ErrorAction Stop).fDenyTSConnections
        if ($deny -eq 0) { Add-Result 1 "  RDP enabled" "PASS" } else { Add-Result 1 "  RDP enabled" "WARN" "fDenyTSConnections=$deny (RDP appears disabled)" }
    } catch { Add-Result 1 "  RDP enabled" "WARN" "could not read RDP setting" }
} catch { Add-Result 1 "Windows Server (x64) + RDP" "WARN" $_.Exception.Message }

try {
    $rt = & dotnet --list-runtimes 2>$null
    $asp8 = $rt | Where-Object { $_ -match 'Microsoft\.AspNetCore\.App 8\.' }
    if ($asp8) { Add-Result 2 ".NET 8 ASP.NET Core Runtime" "PASS" ($asp8 | Select-Object -First 1) }
    else { Add-Result 2 ".NET 8 ASP.NET Core Runtime" "FAIL" "No 'Microsoft.AspNetCore.App 8.x' in dotnet --list-runtimes" }
} catch { Add-Result 2 ".NET 8 ASP.NET Core Runtime" "FAIL" "dotnet CLI not found on PATH" }

try {
    if (Get-Command Get-WindowsFeature -ErrorAction SilentlyContinue) {
        $mf = Get-WindowsFeature -Name Server-Media-Foundation -ErrorAction Stop
        if ($mf.Installed) { Add-Result 3 "Media Foundation (Server-Media-Foundation)" "PASS" }
        else { Add-Result 3 "Media Foundation (Server-Media-Foundation)" "FAIL" "Feature not installed. Install-WindowsFeature Server-Media-Foundation" }
    } else {
        $mfdll = Test-Path "$env:WinDir\System32\mf.dll"
        Add-Result 3 "Media Foundation" $(if ($mfdll) { "WARN" } else { "FAIL" }) "Get-WindowsFeature unavailable; mf.dll present=$mfdll (verify feature manually)"
    }
} catch { Add-Result 3 "Media Foundation (Server-Media-Foundation)" "WARN" $_.Exception.Message }

try {
    $vc = $false
    foreach ($p in @('HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64',
                     'HKLM:\SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64')) {
        if (Test-Path $p) { if ((Get-ItemProperty $p -Name Installed -ErrorAction SilentlyContinue).Installed -eq 1) { $vc = $true } }
    }
    Add-Result 4 "Visual C++ Redistributable (x64)" $(if ($vc) { "PASS" } else { "FAIL" }) $(if (-not $vc) { "VC++ 2015-2022 x64 runtime key not found" } else { "" })
} catch { Add-Result 4 "Visual C++ Redistributable (x64)" "WARN" $_.Exception.Message }

# ---- App files ---------------------------------------------------------------

$exePath = Join-Path $PublishDir "Recorder.Api.exe"
$nativePath = Join-Path $PublishDir "NativeMedia.dll"
$appsettingsPath = Join-Path $PublishDir "appsettings.json"

Add-Result 5 "App published to folder" $(if (Test-Path $PublishDir) { "PASS" } else { "FAIL" }) $PublishDir
Add-Result 6 "Recorder.Api.exe present" $(if (Test-Path $exePath) { "PASS" } else { "FAIL" }) $exePath
Add-Result 7 "NativeMedia.dll next to exe" $(if (Test-Path $nativePath) { "PASS" } else { "FAIL" }) $(if (Test-Path $nativePath) { $nativePath } else { "MISSING - publish does not copy it; copy manually from the Skype.Bots.Media package" })
Add-Result 8 "appsettings.json present" $(if (Test-Path $appsettingsPath) { "PASS" } else { "FAIL" }) $appsettingsPath

$cfg1 = Join-Path $PublishDir '.localConfigs'
$cfg2 = Join-Path $PublishDir 'env\.env.local'
$cfgOk = (Test-Path $cfg1) -or (Test-Path $cfg2)
Add-Result 9 "Config files resolve from working dir" $(if ($cfgOk) { "PASS" } else { "WARN" }) $(if ($cfgOk) { "found under $PublishDir" } else { "no .localConfigs/env.local under AppDirectory - inject settings as env vars instead" })

# ---- TLS certificate ---------------------------------------------------------

try {
    $certs = Get-ChildItem Cert:\LocalMachine\My -ErrorAction Stop
    $byThumb = $null
    if ($CertThumbprint) { $byThumb = $certs | Where-Object { $_.Thumbprint -eq $CertThumbprint } }
    $byFqdn = $null
    if ($Fqdn) { $byFqdn = $certs | Where-Object { $_.Subject -match [regex]::Escape($Fqdn) -or ($_.DnsNameList.Unicode -contains $Fqdn) } }

    Add-Result 10 "CA cert exists for FQDN" $(if ($byFqdn) { "PASS" } elseif (-not $Fqdn) { "MANUAL" } else { "FAIL" }) $(if ($byFqdn) { "subject/SAN matches $Fqdn" } elseif (-not $Fqdn) { "FQDN unknown - pass -Fqdn to check" } else { "no cert in LocalMachine\My matches $Fqdn" })
    Add-Result 11 "Cert imported into LocalMachine\My" $(if ($byThumb -or $byFqdn) { "PASS" } else { "FAIL" }) $(if ($certs) { "$($certs.Count) cert(s) in store" } else { "store empty" })
    Add-Result 12 "Thumbprint known & present" $(if ($byThumb) { "PASS" } elseif (-not $CertThumbprint) { "MANUAL" } else { "FAIL" }) $(if ($byThumb) { $CertThumbprint } elseif (-not $CertThumbprint) { "thumbprint not provided/config" } else { "thumbprint $CertThumbprint not found in store" })

    # 22: service account read access to the cert private key
    $target = if ($byThumb) { $byThumb } elseif ($byFqdn) { $byFqdn | Select-Object -First 1 } else { $null }
    if ($target) {
        $keyFile = $null
        try {
            $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($target)
            if ($rsa -and $rsa.Key -and $rsa.Key.UniqueName) {
                $keyFile = Join-Path "$env:ProgramData\Microsoft\Crypto\Keys" $rsa.Key.UniqueName          # CNG
                if (-not (Test-Path $keyFile)) { $keyFile = Join-Path "$env:ProgramData\Microsoft\Crypto\RSA\MachineKeys" $rsa.Key.UniqueName }
            } elseif ($target.PrivateKey -and $target.PrivateKey.CspKeyContainerInfo) {
                $keyFile = Join-Path "$env:ProgramData\Microsoft\Crypto\RSA\MachineKeys" $target.PrivateKey.CspKeyContainerInfo.UniqueKeyContainerName
            }
        } catch {}
        if ($keyFile -and (Test-Path $keyFile)) {
            $acl = Get-Acl $keyFile
            $hasRead = $acl.Access | Where-Object { $_.IdentityReference -match [regex]::Escape($ServiceAccount) -and $_.FileSystemRights -match 'Read' -and $_.AccessControlType -eq 'Allow' }
            Add-Result 22 "Svc account can read cert private key" $(if ($hasRead) { "PASS" } else { "FAIL" }) $(if ($hasRead) { "$ServiceAccount has Read on key file" } else { "grant with: icacls `"$keyFile`" /grant $ServiceAccount:R" })
        } else {
            Add-Result 22 "Svc account can read cert private key" "WARN" "could not locate private key file to check ACL"
        }
    } else {
        Add-Result 22 "Svc account can read cert private key" "WARN" "no target cert resolved"
    }
} catch { Add-Result 10 "TLS certificate checks" "WARN" $_.Exception.Message }

# ---- Networking --------------------------------------------------------------

$publicIp = $null
try { $publicIp = (Invoke-RestMethod -Uri 'https://api.ipify.org' -TimeoutSec 5) } catch {}
Add-Result 13 "Static public IP assigned to VM" "MANUAL" $(if ($publicIp) { "detected public IP $publicIp (confirm it is STATIC in Azure: az network public-ip list -o table)" } else { "could not detect public IP; check in Azure portal / az network public-ip" })

try {
    if ($Fqdn) {
        $resolved = (Resolve-DnsName $Fqdn -Type A -ErrorAction Stop | Where-Object { $_.IPAddress } | Select-Object -First 1).IPAddress
        if ($resolved -and $publicIp -and $resolved -eq $publicIp) { Add-Result 14 "FQDN resolves to VM public IP" "PASS" "$Fqdn -> $resolved" }
        elseif ($resolved) { Add-Result 14 "FQDN resolves to VM public IP" "WARN" "$Fqdn -> $resolved (public IP detected: $publicIp - compare manually)" }
        else { Add-Result 14 "FQDN resolves to VM public IP" "FAIL" "DNS did not resolve $Fqdn" }
    } else { Add-Result 14 "FQDN resolves to VM public IP" "MANUAL" "FQDN unknown" }
} catch { Add-Result 14 "FQDN resolves to VM public IP" "FAIL" "Resolve-DnsName failed for $Fqdn" }

Add-Result 15 "NSG inbound rule for TCP $MediaPort" "MANUAL" "az network nsg rule list --nsg-name <nsg> -g <rg> --query `"[?destinationPortRange=='$MediaPort']`" -o table"
Add-Result 16 "NSG inbound rule for TCP $SignalingPort" "MANUAL" "az network nsg rule list --nsg-name <nsg> -g <rg> --query `"[?destinationPortRange=='$SignalingPort']`" -o table"

function Test-FirewallPort {
    param([int]$Port)
    try {
        $filters = Get-NetFirewallPortFilter -Protocol TCP -ErrorAction Stop | Where-Object { "$($_.LocalPort)" -split ',\s*' -contains "$Port" }
        foreach ($pf in $filters) {
            $rule = $pf | Get-NetFirewallRule -ErrorAction SilentlyContinue
            if ($rule -and $rule.Enabled -eq 'True' -and $rule.Direction -eq 'Inbound' -and $rule.Action -eq 'Allow') { return $true }
        }
        return $false
    } catch { return $null }
}
$fw8445 = Test-FirewallPort $MediaPort
$fw9442 = Test-FirewallPort $SignalingPort
Add-Result 17 "Windows Firewall allows TCP $MediaPort" $(if ($fw8445 -eq $true) { "PASS" } elseif ($null -eq $fw8445) { "WARN" } else { "FAIL" }) $(if ($fw8445 -ne $true) { "New-NetFirewallRule -DisplayName 'Recorder media $MediaPort' -Direction Inbound -Protocol TCP -LocalPort $MediaPort -Action Allow" } else { "" })
Add-Result 18 "Windows Firewall allows TCP $SignalingPort" $(if ($fw9442 -eq $true) { "PASS" } elseif ($null -eq $fw9442) { "WARN" } else { "FAIL" }) $(if ($fw9442 -ne $true) { "New-NetFirewallRule -DisplayName 'Recorder signaling $SignalingPort' -Direction Inbound -Protocol TCP -LocalPort $SignalingPort -Action Allow" } else { "" })

try {
    $urlacl = (& netsh http show urlacl) -join "`n"
    $has9442 = $urlacl -match [regex]::Escape("https://+:$SignalingPort/") -or $urlacl -match [regex]::Escape("https://+:$SignalingPort")
    $has5000 = $urlacl -match [regex]::Escape("http://127.0.0.1:$LoopbackPort/") -or $urlacl -match [regex]::Escape("http://+:$LoopbackPort")
    Add-Result 19 "URL ACL for https://+:$SignalingPort/" $(if ($has9442) { "PASS" } else { "WARN" }) $(if (-not $has9442) { "not found - only needed if Kestrel (not a reverse proxy) terminates TLS on $SignalingPort" })
    Add-Result 20 "URL ACL for http://127.0.0.1:$LoopbackPort/" $(if ($has5000) { "PASS" } else { "WARN" }) $(if (-not $has5000) { "not found - the app binds 127.0.0.1:$LoopbackPort; a URL ACL may be needed depending on the account" })
} catch { Add-Result 19 "URL ACL checks (netsh)" "WARN" $_.Exception.Message }

# ---- Service account ---------------------------------------------------------

try {
    $acct = Get-LocalUser -Name $ServiceAccount -ErrorAction SilentlyContinue
    Add-Result 21 "Service account exists ($ServiceAccount)" $(if ($acct) { "PASS" } else { "FAIL" }) $(if (-not $acct) { "create with: New-LocalUser $ServiceAccount (or use a gMSA / built-in account)" })
} catch { Add-Result 21 "Service account exists ($ServiceAccount)" "WARN" $_.Exception.Message }

function Test-FolderRight {
    param([string]$Path, [string]$Account, [string]$RightPattern)
    if (-not (Test-Path $Path)) { return $null }
    try {
        $acl = Get-Acl $Path
        $hit = $acl.Access | Where-Object { $_.IdentityReference -match [regex]::Escape($Account) -and $_.FileSystemRights -match $RightPattern -and $_.AccessControlType -eq 'Allow' }
        return [bool]$hit
    } catch { return $null }
}
$r23 = Test-FolderRight $PublishDir $ServiceAccount 'ReadAndExecute|Read, |FullControl|Modify'
Add-Result 23 "Svc account Read+Execute on publish dir" $(if ($r23 -eq $true) { "PASS" } elseif ($null -eq $r23) { "WARN" } else { "FAIL" }) $(if ($r23 -ne $true) { "icacls `"$PublishDir`" /grant $ServiceAccount`:(OI)(CI)RX" })

if (-not (Test-Path $LogDir)) { Add-Result 24 "Svc account Write on log dir" "FAIL" "$LogDir does not exist - New-Item -ItemType Directory $LogDir" }
else {
    $r24 = Test-FolderRight $LogDir $ServiceAccount 'Write|Modify|FullControl'
    Add-Result 24 "Svc account Write on log dir" $(if ($r24 -eq $true) { "PASS" } elseif ($null -eq $r24) { "WARN" } else { "FAIL" }) $(if ($r24 -ne $true) { "icacls `"$LogDir`" /grant $ServiceAccount`:(OI)(CI)M" })
}

# ---- NSSM --------------------------------------------------------------------

$nssmCmd = Get-Command nssm -ErrorAction SilentlyContinue
Add-Result 25 "NSSM installed / on PATH" $(if ($nssmCmd) { "PASS" } else { "FAIL" }) $(if ($nssmCmd) { $nssmCmd.Source } else { "nssm not found on PATH" })

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
Add-Result 26 "Service '$ServiceName' registered" $(if ($svc) { "PASS" } else { "FAIL" }) $(if ($svc) { "status=$($svc.Status)" } else { "nssm install $ServiceName `"$exePath`"" })

$appDir = Get-NssmValue $ServiceName 'AppDirectory'
Add-Result 27 "AppDirectory set" $(if ($appDir) { "PASS" } else { "WARN" }) $(if ($appDir) { $appDir } else { "nssm set $ServiceName AppDirectory `"$PublishDir`"" })

$stdout = Get-NssmValue $ServiceName 'AppStdout'
$stderr = Get-NssmValue $ServiceName 'AppStderr'
Add-Result 28 "AppStdout/AppStderr configured" $(if ($stdout -and $stderr) { "PASS" } else { "WARN" }) ("stdout={0}; stderr={1}" -f $stdout, $stderr)

$requiredEnv = @('MEDIA_BOT_ENABLED','MICROSOFT_APP_ID','MICROSOFT_APP_PASSWORD','MICROSOFT_APP_TENANT_ID',
                 'MEDIA_BOT_SERVICE_FQDN','MEDIA_BOT_CERT_THUMBPRINT','MEDIA_BOT_MEDIA_PORT','MEDIA_BOT_NOTIFICATION_URL',
                 'RECORDER_SHARED_SECRET','AZURE_SPEECH_KEY','AZURE_SPEECH_REGION','BOT_ENDPOINT')
$envExtra = Get-NssmValue $ServiceName 'AppEnvironmentExtra'
if ($null -eq $envExtra) {
    Add-Result 29 "Required env vars set on service" "WARN" "could not read AppEnvironmentExtra (NSSM missing or service not registered) - note: vars may instead come from .localConfigs in AppDirectory"
} else {
    $missing = @()
    foreach ($k in $requiredEnv) { if ($envExtra -notmatch ("(?m)^\s*{0}=" -f [regex]::Escape($k))) { $missing += $k } }
    if ($missing.Count -eq 0) { Add-Result 29 "Required env vars set on service" "PASS" "all $($requiredEnv.Count) present in AppEnvironmentExtra" }
    else { Add-Result 29 "Required env vars set on service" "WARN" ("not in AppEnvironmentExtra (ok if provided via .localConfigs): {0}" -f ($missing -join ', ')) }
}

$startType = Get-NssmValue $ServiceName 'Start'
$autoStart = ($startType -match 'SERVICE_AUTO_START') -or ($svc -and (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue).StartMode -eq 'Auto')
Add-Result 30 "Service set to auto-start" $(if ($autoStart) { "PASS" } else { "WARN" }) $(if ($startType) { $startType } else { "nssm set $ServiceName Start SERVICE_AUTO_START" })

Add-Result 31 "Service currently running" $(if ($svc -and $svc.Status -eq 'Running') { "PASS" } else { "FAIL" }) $(if ($svc) { "status=$($svc.Status)" } else { "service not found" })

# ---- summary -----------------------------------------------------------------

Write-Host "`n=== Summary ===" -ForegroundColor White
$grp = $script:results | Group-Object Status
foreach ($s in @('PASS','FAIL','WARN','MANUAL')) {
    $c = ($grp | Where-Object { $_.Name -eq $s } | Select-Object -First 1).Count
    if (-not $c) { $c = 0 }
    $col = switch ($s) { 'PASS' { 'Green' } 'FAIL' { 'Red' } 'WARN' { 'Yellow' } 'MANUAL' { 'Cyan' } }
    Write-Host ("  {0,-6}: {1}" -f $s, $c) -ForegroundColor $col
}
$fails = $script:results | Where-Object { $_.Status -eq 'FAIL' }
if ($fails) {
    Write-Host "`nMust-fix (FAIL):" -ForegroundColor Red
    foreach ($f in $fails) { Write-Host ("  {0,2}. {1} - {2}" -f $f.Num, $f.Name, $f.Detail) -ForegroundColor Red }
}
Write-Host ""
