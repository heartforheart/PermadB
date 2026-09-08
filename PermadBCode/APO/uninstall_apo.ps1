# uninstall_apo.ps1 - Uninstalls and removes PermadB Limiter APO from audio endpoints
param(
    [string]$TargetDeviceId = ""
)

$ErrorActionPreference = "Stop"

# 1. Require Administrator
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "[APO-Uninstaller] Administrator elevation is required." -ForegroundColor Yellow
    try {
        $argsList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
        if ($TargetDeviceId) { $argsList += " -TargetDeviceId `"$TargetDeviceId`"" }
        Start-Process powershell.exe -ArgumentList $argsList -Verb RunAs -ErrorAction Stop
        exit
    }
    catch {
        Write-Host "[ERROR] Could not automatically elevate from this shell." -ForegroundColor Red
        Write-Host "Please right-click 'uninstall_apo.bat' and select 'Run as administrator'," -ForegroundColor Cyan
        Write-Host "or open PowerShell as Administrator and run: `"$PSCommandPath`"" -ForegroundColor Cyan
        exit 1
    }
}

Write-Host "===================================================" -ForegroundColor Cyan
Write-Host "      PermadB Limiter APO - Uninstallation" -ForegroundColor Cyan
Write-Host "===================================================" -ForegroundColor Cyan

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$dllPath = Join-Path $scriptDir "PermadBApo.dll"
$clsid = "{968ff234-1895-49f1-8b97-9d9075eb25b6}"
$pkeyEfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7"
$pkeyCompositeEfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},15"

$renderBase = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render"

# Scan all render endpoints for PermadB APO
Write-Host "[1/3] Removing APO associations from endpoints..." -ForegroundColor Yellow
$devices = Get-ChildItem -Path $renderBase
foreach ($dev in $devices) {
    $fxPath = "$($dev.PSPath)\FxProperties"
    if (Test-Path $fxPath) {
        $fx = Get-ItemProperty -Path $fxPath -ErrorAction SilentlyContinue
        $modified = $false

        if ($fx.PSObject.Properties[$pkeyEfx] -and $fx.$pkeyEfx -eq $clsid) {
            if ($fx.PSObject.Properties["PermadB_Backup_EFX"]) {
                Set-ItemProperty -Path $fxPath -Name $pkeyEfx -Value $fx.PermadB_Backup_EFX -Type String
                Remove-ItemProperty -Path $fxPath -Name "PermadB_Backup_EFX" -ErrorAction SilentlyContinue
            } else {
                Remove-ItemProperty -Path $fxPath -Name $pkeyEfx -ErrorAction SilentlyContinue
            }
            $modified = $true
        }

        if ($fx.PSObject.Properties[$pkeyCompositeEfx] -and ($fx.$pkeyCompositeEfx -contains $clsid -or $fx.$pkeyCompositeEfx -eq $clsid)) {
            if ($fx.PSObject.Properties["PermadB_Backup_CompositeEFX"]) {
                Set-ItemProperty -Path $fxPath -Name $pkeyCompositeEfx -Value $fx.PermadB_Backup_CompositeEFX -Type MultiString
                Remove-ItemProperty -Path $fxPath -Name "PermadB_Backup_CompositeEFX" -ErrorAction SilentlyContinue
            } else {
                $rem = [string[]]($fx.$pkeyCompositeEfx | Where-Object { $_ -ne $clsid })
                if ($rem -and $rem.Length -gt 0) {
                    Set-ItemProperty -Path $fxPath -Name $pkeyCompositeEfx -Value $rem -Type MultiString
                } else {
                    Remove-ItemProperty -Path $fxPath -Name $pkeyCompositeEfx -ErrorAction SilentlyContinue
                }
            }
            Remove-ItemProperty -Path $fxPath -Name "PermadB_Created_CompositeEFX" -ErrorAction SilentlyContinue
            $modified = $true
        }

        $pkeyEfxModes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},7"
        if ($fx.PSObject.Properties["PermadB_Backup_EFXModes"]) {
            Set-ItemProperty -Path $fxPath -Name $pkeyEfxModes -Value $fx.PermadB_Backup_EFXModes -Type MultiString
            Remove-ItemProperty -Path $fxPath -Name "PermadB_Backup_EFXModes" -ErrorAction SilentlyContinue
            $modified = $true
        } elseif ($fx.PSObject.Properties["PermadB_Created_EFXModes"]) {
            Remove-ItemProperty -Path $fxPath -Name $pkeyEfxModes -ErrorAction SilentlyContinue
            Remove-ItemProperty -Path $fxPath -Name "PermadB_Created_EFXModes" -ErrorAction SilentlyContinue
            $modified = $true
        }

        Remove-ItemProperty -Path $fxPath -Name "PermadB_Installed_DeviceId" -ErrorAction SilentlyContinue

        if ($modified) {
            Write-Host "      Cleaned endpoint: $($dev.PSChildName)" -ForegroundColor Green
        }
    }
}

# Unregister COM server
Write-Host "[2/3] Unregistering PermadBApo.dll..." -ForegroundColor Yellow
if (Test-Path $dllPath) {
    Start-Process regsvr32.exe -ArgumentList "/u /s `"$dllPath`"" -Wait
}
Write-Host "      COM unregistration complete." -ForegroundColor Green

# Restart audio service
Write-Host "[3/3] Restarting Windows Audio Service..." -ForegroundColor Yellow
Restart-Service -Name "audiosrv" -Force
Start-Sleep -Seconds 2

Write-Host ""
Write-Host "[SUCCESS] PermadB Limiter APO uninstalled successfully." -ForegroundColor Green
