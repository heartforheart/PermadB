@echo off
setlocal EnableDelayedExpansion
title PermadB Dual-Stage (SFX + EFX) APO Deployer

echo ===================================================
echo     PermadB Dual-Stage (SFX + EFX) APO Deployer
echo ===================================================

:: Check for administrative privileges
net session >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo [ELEVATION REQUIRED] Requesting Administrator privileges...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process cmd.exe -ArgumentList '/k \"\"%~f0\"\"' -Verb RunAs"
    exit /b 0
)

cd /d "%~dp0"

echo [1/4] Stopping Windows Audio Service and audiodg.exe...
net stop audiosrv /y >nul 2>&1
net stop AudioEndpointBuilder /y >nul 2>&1
taskkill /F /IM audiodg.exe >nul 2>&1
taskkill /F /IM PermadB.exe >nul 2>&1

echo [2/4] Deploying Dual-Stage SFX + EFX Limiter APO into Windows...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0PermadBCode\APO\deploy_apo_elevated.ps1"

echo [3/4] Restarting Windows Audio Services...
net start AudioEndpointBuilder >nul 2>&1
net start audiosrv >nul 2>&1

echo [4/4] Launching PermadB Application...
if exist "%~dp0PermadB-Setup.exe" (
    echo Launching PermadB Setup...
    start "" "%~dp0PermadB-Setup.exe"
) else (
    echo Launching PermadB...
    start "" "%~dp0PermadBCode\App\bin\Release\net9.0-windows\win-x64\PermadB.exe"
)

echo.
echo ===================================================
echo [SUCCESS] Dual-Stage APO Deployment Complete!
echo ===================================================
timeout /t 4
endlocal
