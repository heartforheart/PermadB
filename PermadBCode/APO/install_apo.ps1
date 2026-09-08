# install_apo.ps1 - Installs and associates PermadB Limiter APO with an audio endpoint
param(
    [string]$TargetDeviceId = ""
)

$ErrorActionPreference = "Stop"

# 1. Require Administrator
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$logPath = Join-Path $scriptDir "install_apo.log"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "[APO-Installer] Administrator elevation is required." -ForegroundColor Yellow
    try {
        $argsList = "-NoProfile -ExecutionPolicy Bypass -Command `"& '$PSCommandPath' *>&1 | Tee-Object -FilePath '$logPath'`""
        $proc = Start-Process powershell.exe -ArgumentList $argsList -Verb RunAs -PassThru -ErrorAction Stop
        $proc.WaitForExit()
        if (Test-Path $logPath) {
            Get-Content $logPath
        }
        exit $proc.ExitCode
    }
    catch {
        Write-Host "[ERROR] Could not automatically elevate from this shell." -ForegroundColor Red
        Write-Host "Please right-click 'install_apo.bat' and select 'Run as administrator'," -ForegroundColor Cyan
        Write-Host "or open PowerShell as Administrator and run: `"$PSCommandPath`"" -ForegroundColor Cyan
        exit 1
    }
}

Write-Host "===================================================" -ForegroundColor Cyan
Write-Host "       PermadB Limiter APO - Installation" -ForegroundColor Cyan
Write-Host "===================================================" -ForegroundColor Cyan

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$dllPath = Join-Path $scriptDir "PermadBApo.dll"

if (-not (Test-Path $dllPath)) {
    Write-Error "[ERROR] PermadBApo.dll not found at '$dllPath'. Run build_apo.bat first!"
}

# 2. Allow unsigned/custom APOs in audiodg.exe (Windows 11 Requirement)
Write-Host "[1/5] Configuring Windows Audio Engine security policy..." -ForegroundColor Yellow
$audioKeyPath = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Audio"
Set-ItemProperty -Path $audioKeyPath -Name "DisableProtectedAudioDG" -Value 1 -Type DWord
Write-Host "      DisableProtectedAudioDG set to 1 (allows audiodg.exe to load custom APO)." -ForegroundColor Green

# 3. Register COM Server
Write-Host "[2/5] Registering PermadBApo.dll in COM..." -ForegroundColor Yellow
$regSvr = Start-Process regsvr32.exe -ArgumentList "/s `"$dllPath`"" -PassThru -Wait
if ($regSvr.ExitCode -ne 0) {
    Write-Error "[ERROR] regsvr32 failed with exit code $($regSvr.ExitCode)"
}
Write-Host "      COM registration complete." -ForegroundColor Green

# 4. Determine Target Audio Endpoint
Write-Host "[3/5] Resolving playback endpoint..." -ForegroundColor Yellow
$renderBase = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render"

if (-not $TargetDeviceId) {
    # Auto-detect active playback endpoint (DeviceState = 1)
    $devices = Get-ChildItem -Path $renderBase | Where-Object {
        $state = (Get-ItemProperty -Path $_.PSPath -Name "DeviceState" -ErrorAction SilentlyContinue).DeviceState
        $state -eq 1
    }

    if (-not $devices) {
        Write-Error "[ERROR] No active playback device found."
    }

    # Prefer headphones or first active device
    $selected = $devices[0]
    foreach ($dev in $devices) {
        $props = Get-ItemProperty -Path "$($dev.PSPath)\Properties" -ErrorAction SilentlyContinue
        $name = $props.'{a45c254e-df1c-4efd-8020-67d146a850e0},2'
        $desc = $props.'{b3f8fa53-0004-438e-9003-51a46e139bfc},6'
        if ($name -match "Headphone" -or $desc -match "HyperX") {
            $selected = $dev
            break
        }
    }
    $TargetDeviceId = $selected.PSChildName
}

$devicePath = "$renderBase\$TargetDeviceId"
$deviceProps = Get-ItemProperty -Path "$devicePath\Properties" -ErrorAction SilentlyContinue
$deviceName = $deviceProps.'{a45c254e-df1c-4efd-8020-67d146a850e0},2'
$deviceDesc = $deviceProps.'{b3f8fa53-0004-438e-9003-51a46e139bfc},6'

Write-Host "      Target Endpoint: $deviceName ($deviceDesc)" -ForegroundColor Green
Write-Host "      Endpoint ID:     $TargetDeviceId" -ForegroundColor Gray

# 5. Associate APO with Endpoint in FxProperties
Write-Host "[4/5] Associating APO in endpoint FxProperties..." -ForegroundColor Yellow
$fxPath = "$devicePath\FxProperties"
if (-not (Test-Path $fxPath)) {
    New-Item -Path $fxPath -Force | Out-Null
}

$clsid = "{968ff234-1895-49f1-8b97-9d9075eb25b6}"
$pkeyEfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7"
$pkeyCompositeEfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},15"

# Backup original values if present and not already backed up
$existingFx = Get-ItemProperty -Path $fxPath -ErrorAction SilentlyContinue
if ($existingFx.PSObject.Properties[$pkeyEfx] -and -not $existingFx.PSObject.Properties["PermadB_Backup_EFX"]) {
    Set-ItemProperty -Path $fxPath -Name "PermadB_Backup_EFX" -Value $existingFx.$pkeyEfx -Type String
}
if ($existingFx.PSObject.Properties[$pkeyCompositeEfx] -and -not $existingFx.PSObject.Properties["PermadB_Backup_CompositeEFX"]) {
    Set-ItemProperty -Path $fxPath -Name "PermadB_Backup_CompositeEFX" -Value $existingFx.$pkeyCompositeEfx -Type String
}

# Set Endpoint Effect CLSID
Set-ItemProperty -Path $fxPath -Name $pkeyEfx -Value $clsid -Type String
Set-ItemProperty -Path $fxPath -Name $pkeyCompositeEfx -Value $clsid -Type String
Set-ItemProperty -Path $fxPath -Name "PermadB_Installed_DeviceId" -Value $TargetDeviceId -Type String

Write-Host "      Endpoint Effects configured successfully." -ForegroundColor Green

# 6. Restart Windows Audio Service
Write-Host "[5/5] Restarting Windows Audio Service (audiosrv)..." -ForegroundColor Yellow
Restart-Service -Name "audiosrv" -Force
Start-Sleep -Seconds 2
Write-Host "      Windows Audio Service restarted." -ForegroundColor Green

Write-Host ""
Write-Host "===================================================" -ForegroundColor Green
Write-Host " [SUCCESS] PermadB Limiter APO Installed & Active!" -ForegroundColor Green
Write-Host "===================================================" -ForegroundColor Green
Write-Host "Endpoint: $deviceName ($deviceDesc)"
Write-Host "APO CLSID: $clsid"
Write-Host "Gain: -12.0 dB applied directly in audiodg.exe"
Write-Host ""
