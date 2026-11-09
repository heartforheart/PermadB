@echo off
setlocal
echo ===================================================
echo     PermadB Dual-Stage (SFX + EFX) APO Deployer
echo ===================================================

:: Check for administrative privileges
net session >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo [ELEVATION REQUIRED] Requesting Administrator privileges...
    powershell -NoProfile -Command "Start-Process cmd.exe -ArgumentList '/c \"\"%~f0\"\"' -Verb RunAs"
    exit /b 0
)

cd /d "%~dp0"
echo [1/3] Stopping Windows Audio Service & audiodg.exe...
net stop audiosrv /y >nul 2>&1
net stop AudioEndpointBuilder /y >nul 2>&1
taskkill /F /IM audiodg.exe >nul 2>&1
for /f "tokens=2 delims=," %%p in ('tasklist /svc /fi "services eq audiosrv" /fo csv /nh') do (
    if not "%%~p"=="" if not "%%~p"=="N/A" taskkill /F /PID %%~p >nul 2>&1
)

echo [2/3] Installing Dual-Stage SFX + EFX Limiter APO...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0PermadBCode\APO\deploy_apo_elevated.ps1"

echo [3/3] Restarting Windows Audio Services...
net start AudioEndpointBuilder >nul 2>&1
net start audiosrv >nul 2>&1

echo.
echo ===================================================
echo [SUCCESS] Dual-Stage APO Deployment Complete!
echo Launching PermadB into your Windows Tray...
echo ===================================================
start "" "%~dp0PermadBCode\App\bin\Release\net9.0-windows\win-x64\PermadB.exe"
pause
endlocal
