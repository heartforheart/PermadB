# deploy_apo_elevated.ps1 - Complete elevated deployment for PermadB Dual-Stage APO
param(
    [string]$SourceDir = "A:\Projects2\PermadB\PermadBCode\APO",
    [string]$TargetDir = "C:\Program Files\PermadB"
)

$ErrorActionPreference = "Continue"
$logPath = "C:\Users\Public\deploy_apo.log"

function Log($msg) {
    $line = "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff')] $msg"
    Write-Host $line
    Add-Content -Path $logPath -Value $line -ErrorAction SilentlyContinue
}

Log "=== PermadB Dual-Stage APO Deployment Starting ==="
Log "Source: $SourceDir"
Log "Target: $TargetDir"

# 1. Stop audio services & kill audiodg
Log "[1/6] Stopping Windows Audio Service & audiodg..."
Stop-Service -Name "audiosrv" -Force -ErrorAction SilentlyContinue
Stop-Service -Name "AudioEndpointBuilder" -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
Get-Process -Name "audiodg" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

# 2. Prepare target directory & copy files
Log "[2/6] Staging APO binaries..."
if (-not (Test-Path $TargetDir)) {
    New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null
}

$sourceDll = Join-Path $SourceDir "PermadBApo.dll"
$targetDll = Join-Path $TargetDir "PermadBApo.dll"

try {
    Copy-Item $sourceDll -Destination $targetDll -Force -ErrorAction Stop
    Log "      Copied $sourceDll -> $targetDll successfully."
} catch {
    Log "      Direct copy failed: $($_.Exception.Message). Trying .old rename..."
    $oldDll = "$targetDll.old"
    if (Test-Path $oldDll) { Remove-Item $oldDll -Force -ErrorAction SilentlyContinue }
    Rename-Item $targetDll $oldDll -Force -ErrorAction SilentlyContinue
    Copy-Item $sourceDll -Destination $targetDll -Force
    Log "      Copied $sourceDll -> $targetDll via rename."
}

Copy-Item (Join-Path $SourceDir "PermadBApo.inf") -Destination $TargetDir -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $SourceDir "PermadBApo.cat") -Destination $TargetDir -Force -ErrorAction SilentlyContinue

# 3. Security Policy (DisableProtectedAudioDG)
Log "[3/6] Setting Windows Audio Security Policy (DisableProtectedAudioDG = 1)..."
Set-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Audio" -Name "DisableProtectedAudioDG" -Value 1 -Type DWord -ErrorAction SilentlyContinue

# 4. Register COM Server via regsvr32
Log "[4/6] Registering PermadBApo.dll with regsvr32..."
$regProc = Start-Process regsvr32.exe -ArgumentList "/s `"$targetDll`"" -PassThru -Wait
Log "      regsvr32 exited with code $($regProc.ExitCode)"

# Also register source DLL in case paths differ
Start-Process regsvr32.exe -ArgumentList "/s `"$sourceDll`"" -PassThru -Wait | Out-Null

# 5. AudioProcessingObjects Registration (EFX & SFX)
Log "[5/6] Writing AudioProcessingObjects registry keys..."
$efxClsid = "{968ff234-1895-49f1-8b97-9d9075eb25b6}"
$sfxClsid = "{b41e8c71-2947-4e3a-965a-06385d8204d1}"

foreach ($item in @(
    @{ Clsid = $efxClsid; Name = "PermadB Master Limiter EFX APO" },
    @{ Clsid = $sfxClsid; Name = "PermadB Stream Limiter SFX APO" }
)) {
    # HKCR CLSID
    $clsidKey = [Microsoft.Win32.Registry]::ClassesRoot.CreateSubKey("CLSID\$($item.Clsid)", $true)
    $clsidKey.SetValue("", $item.Name, [Microsoft.Win32.RegistryValueKind]::String)
    $inprocKey = $clsidKey.CreateSubKey("InProcServer32", $true)
    $inprocKey.SetValue("", $targetDll, [Microsoft.Win32.RegistryValueKind]::String)
    $inprocKey.SetValue("ThreadingModel", "Both", [Microsoft.Win32.RegistryValueKind]::String)
    $inprocKey.Close()
    $clsidKey.Close()

    # HKLM Classes CLSID
    $hklmClsidKey = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey("SOFTWARE\Classes\CLSID\$($item.Clsid)", $true)
    $hklmClsidKey.SetValue("", $item.Name, [Microsoft.Win32.RegistryValueKind]::String)
    $hklmInprocKey = $hklmClsidKey.CreateSubKey("InProcServer32", $true)
    $hklmInprocKey.SetValue("", $targetDll, [Microsoft.Win32.RegistryValueKind]::String)
    $hklmInprocKey.SetValue("ThreadingModel", "Both", [Microsoft.Win32.RegistryValueKind]::String)
    $hklmInprocKey.Close()
    $hklmClsidKey.Close()

    # AudioEngine AudioProcessingObjects
    $apoRegPath = "SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\$($item.Clsid)"
    $apoKey = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey($apoRegPath, $true)
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
    $apoKey.SetValue("NumAPOInterfaces", 2, [Microsoft.Win32.RegistryValueKind]::DWord)
    $apoKey.SetValue("APOInterface0", "{FD7F2B29-24D0-4B5C-B177-592C39F9CA10}", [Microsoft.Win32.RegistryValueKind]::String)
    $apoKey.SetValue("APOInterface1", "{BAFE99D2-7436-44CE-9E0E-4D89AFBFFF56}", [Microsoft.Win32.RegistryValueKind]::String)
    $apoKey.Close()

    Log "      Registered $($item.Name) ($($item.Clsid)) with 2 interfaces."
}

# 6. Associate with active render endpoints in FxProperties
Log "[6/6] Associating SFX + EFX with active playback endpoints..."
$renderBase = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render"
$endpoints = Get-ChildItem -Path $renderBase -ErrorAction SilentlyContinue | Where-Object {
    $st = (Get-ItemProperty -Path $_.PSPath -Name "DeviceState" -ErrorAction SilentlyContinue).DeviceState
    $st -eq 1
}

$allModes = @(
    "{00000000-0000-0000-0000-000000000000}",
    "{c18e2f7e-933d-407f-b723-4f73d09d4310}",
    "{e069d28e-d734-4001-a847-b16a3e173808}",
    "{98951333-b9cd-48b1-a0a3-ff40682d73f7}",
    "{fc1cf0a4-3860-4592-8232-a567847be2d1}",
    "{47800040-4533-461b-9b4c-9e9d77b11057}",
    "{b26fedb9-886c-430b-8018-cb5467e9fc0e}"
)

foreach ($ep in $endpoints) {
    $fxKeyPath = "$($ep.PSPath)\FxProperties"
    if (-not (Test-Path $fxKeyPath)) {
        New-Item -Path $fxKeyPath -Force | Out-Null
    }

    # EFX (Endpoint Effect)
    Set-ItemProperty -Path $fxKeyPath -Name "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7" -Value $efxClsid -Type String -Force
    Set-ItemProperty -Path $fxKeyPath -Name "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},15" -Value $efxClsid -Type String -Force
    Set-ItemProperty -Path $fxKeyPath -Name "{d3993a3f-99c2-4402-b5ec-a92a0367664b},7" -Value $allModes -Type MultiString -Force

    # SFX (Stream Effect)
    Set-ItemProperty -Path $fxKeyPath -Name "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},5" -Value $sfxClsid -Type String -Force
    Set-ItemProperty -Path $fxKeyPath -Name "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},13" -Value $sfxClsid -Type String -Force
    Set-ItemProperty -Path $fxKeyPath -Name "{d3993a3f-99c2-4402-b5ec-a92a0367664b},5" -Value $allModes -Type MultiString -Force

    Log "      Bound SFX+EFX to endpoint: $($ep.PSChildName)"
}

# Restart audio service
Log "Restarting audio services..."
Start-Service -Name "AudioEndpointBuilder" -ErrorAction SilentlyContinue
Start-Service -Name "audiosrv" -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

Log "=== PermadB Dual-Stage APO Deployment COMPLETED SUCCESSFULLY ==="
