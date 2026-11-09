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

$oldDllPath = Join-Path $scriptDir "PermadBApo.dll.old"
if (Test-Path $oldDllPath) {
    Remove-Item $oldDllPath -Force -ErrorAction SilentlyContinue
}

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

# Ensure AudioProcessingObjects registration has correct interface GUIDs (EFX & SFX)
$efxClsid = "{968ff234-1895-49f1-8b97-9d9075eb25b6}"
$sfxClsid = "{b41e8c71-2947-4e3a-965a-06385d8204d1}"

foreach ($item in @(
    @{ Clsid = $efxClsid; Name = "PermadB Master Limiter EFX APO" },
    @{ Clsid = $sfxClsid; Name = "PermadB Stream Limiter SFX APO" }
)) {
    $apoRegPath = "SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\$($item.Clsid)"
    $apoKey = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey($apoRegPath, $true)
    try {
        $apoKey.SetValue("FriendlyName", $item.Name, [Microsoft.Win32.RegistryValueKind]::String)
        $apoKey.SetValue("Copyright", "PermadB", [Microsoft.Win32.RegistryValueKind]::String)
        $apoKey.SetValue("MajorVersion", 1, [Microsoft.Win32.RegistryValueKind]::DWord)
        $apoKey.SetValue("MinorVersion", 0, [Microsoft.Win32.RegistryValueKind]::DWord)
        $apoKey.SetValue("Flags", 15, [Microsoft.Win32.RegistryValueKind]::DWord)
        $apoKey.SetValue("MinInputConnections", 1, [Microsoft.Win32.RegistryValueKind]::DWord)
        $apoKey.SetValue("MaxInputConnections", 1, [Microsoft.Win32.RegistryValueKind]::DWord)
        $apoKey.SetValue("MinOutputConnections", 1, [Microsoft.Win32.RegistryValueKind]::DWord)
        $apoKey.SetValue("MaxOutputConnections", 1, [Microsoft.Win32.RegistryValueKind]::DWord)
        $apoKey.SetValue("MaxInstances", [int]0xFFFFFFFF, [Microsoft.Win32.RegistryValueKind]::DWord)
        $apoKey.SetValue("NumAPOInterfaces", 1, [Microsoft.Win32.RegistryValueKind]::DWord)
        $apoKey.SetValue("APOInterface0", "{FD7F2B29-24D0-4B5C-B177-592C39F9CA10}", [Microsoft.Win32.RegistryValueKind]::String) # IID_IAudioProcessingObject
        try { $apoKey.DeleteValue("APOInterface1") } catch { }
    } finally {
        $apoKey.Close()
    }
}
Write-Host "      COM & AudioEngine registration complete for EFX and SFX." -ForegroundColor Green

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

$efxClsid = "{968ff234-1895-49f1-8b97-9d9075eb25b6}"
$sfxClsid = "{b41e8c71-2947-4e3a-965a-06385d8204d1}"

$pkeySfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},5"
$pkeyEfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7"
$pkeyCompositeSfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},13"
$pkeyCompositeEfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},15"
$pkeySfxModes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},5"
$pkeyMfxModes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},6"
$pkeyEfxModes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},7"

# Set Endpoint Effect CLSID via direct Registry API with SetValue rights
$regSubPath = "SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\$TargetDeviceId\FxProperties"
$key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($regSubPath, [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree, [System.Security.AccessControl.RegistryRights]::SetValue -bor [System.Security.AccessControl.RegistryRights]::QueryValues)
try {
    # 1. Configure SFX (Pre-Mix Stream Effect)
    $existingSfx = $key.GetValue($pkeySfx)
    if ($existingSfx -and $existingSfx -ne $sfxClsid) {
        if (-not $key.GetValue("PermadB_Backup_SFX")) {
            $key.SetValue("PermadB_Backup_SFX", $existingSfx, [Microsoft.Win32.RegistryValueKind]::String)
        }
    }
    $key.SetValue($pkeySfx, $sfxClsid, [Microsoft.Win32.RegistryValueKind]::String)

    # 2. Configure CompositeSFX
    $existingCompSfx = $key.GetValue($pkeyCompositeSfx)
    if ($existingCompSfx -and $existingCompSfx.Length -gt 0) {
        if (-not ($existingCompSfx -contains $sfxClsid)) {
            if (-not $key.GetValue("PermadB_Backup_CompositeSFX")) {
                $key.SetValue("PermadB_Backup_CompositeSFX", [string[]]$existingCompSfx, [Microsoft.Win32.RegistryValueKind]::MultiString)
            }
            $newCompSfx = [string[]]($existingCompSfx + @($sfxClsid))
            $key.SetValue($pkeyCompositeSfx, $newCompSfx, [Microsoft.Win32.RegistryValueKind]::MultiString)
        }
    } else {
        $key.SetValue("PermadB_Created_CompositeSFX", 1, [Microsoft.Win32.RegistryValueKind]::DWord)
        $key.SetValue($pkeyCompositeSfx, [string[]]@($sfxClsid), [Microsoft.Win32.RegistryValueKind]::MultiString)
    }

    # 3. Configure EFX (Post-Mix Endpoint Effect)
    $existingEfx = $key.GetValue($pkeyEfx)
    if ($existingEfx -and $existingEfx -ne $efxClsid) {
        if (-not $key.GetValue("PermadB_Backup_EFX")) {
            $key.SetValue("PermadB_Backup_EFX", $existingEfx, [Microsoft.Win32.RegistryValueKind]::String)
        }
    }
    $key.SetValue($pkeyEfx, $efxClsid, [Microsoft.Win32.RegistryValueKind]::String)

    # 4. Configure CompositeEFX
    $existingComp = $key.GetValue($pkeyCompositeEfx)
    if ($existingComp -and $existingComp.Length -gt 0) {
        if (-not ($existingComp -contains $efxClsid)) {
            if (-not $key.GetValue("PermadB_Backup_CompositeEFX")) {
                $key.SetValue("PermadB_Backup_CompositeEFX", [string[]]$existingComp, [Microsoft.Win32.RegistryValueKind]::MultiString)
            }
            $newComp = [string[]]($existingComp + @($efxClsid))
            $key.SetValue($pkeyCompositeEfx, $newComp, [Microsoft.Win32.RegistryValueKind]::MultiString)
        }
    } else {
        $key.SetValue("PermadB_Created_CompositeEFX", 1, [Microsoft.Win32.RegistryValueKind]::DWord)
        $key.SetValue($pkeyCompositeEfx, [string[]]@($efxClsid), [Microsoft.Win32.RegistryValueKind]::MultiString)
    }

    # 5. Configure Streaming Modes for SFX and EFX
    $targetModes = [string[]]@(
        "{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}", # Default
        "{98951333-B9CD-48B1-A0A3-FF40682D73F7}", # Communications
        "{FC1CF0A4-3860-4592-8232-A567847BE2D1}", # Speech
        "{47800040-4533-461B-9B4C-9E9D77B11057}"  # Media
    )

    $existingSfxModes = $key.GetValue($pkeySfxModes)
    if ($existingSfxModes -and $existingSfxModes.Length -gt 0) {
        $mergedSfxModes = [string[]]($existingSfxModes + $targetModes | Select-Object -Unique)
        $key.SetValue($pkeySfxModes, $mergedSfxModes, [Microsoft.Win32.RegistryValueKind]::MultiString)
    } else {
        $key.SetValue($pkeySfxModes, $targetModes, [Microsoft.Win32.RegistryValueKind]::MultiString)
    }

    $existingEfxModes = $key.GetValue($pkeyEfxModes)
    if ($existingEfxModes -and $existingEfxModes.Length -gt 0) {
        $mergedEfxModes = [string[]]($existingEfxModes + $targetModes | Select-Object -Unique)
        $key.SetValue($pkeyEfxModes, $mergedEfxModes, [Microsoft.Win32.RegistryValueKind]::MultiString)
    } else {
        $key.SetValue($pkeyEfxModes, $targetModes, [Microsoft.Win32.RegistryValueKind]::MultiString)
    }

    $key.SetValue("PermadB_Installed_DeviceId", $TargetDeviceId, [Microsoft.Win32.RegistryValueKind]::String)
} finally {
    $key.Close()
}

Write-Host "      Dual-Stage (SFX + EFX) Effects configured successfully." -ForegroundColor Green

# 6. Configuration Complete
Write-Host "[5/5] Endpoint & APO Registry configuration complete." -ForegroundColor Green

Write-Host ""
Write-Host "===================================================" -ForegroundColor Green
Write-Host " [SUCCESS] PermadB Limiter APO Installed & Active!" -ForegroundColor Green
Write-Host "===================================================" -ForegroundColor Green
Write-Host "Endpoint: $deviceName ($deviceDesc)"
Write-Host "APO CLSID: $clsid"
Write-Host "Gain: -12.0 dB applied directly in audiodg.exe"
Write-Host ""
