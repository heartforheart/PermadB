@echo off
setlocal
echo ===================================================
echo     Installing PermadB Limiter APO (Admin Required)
echo ===================================================

:: Check for administrative privileges
net session >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo [ELEVATION REQUIRED] Please right-click this file and select "Run as administrator".
    echo.
    pause
    exit /b 1
)

cd /d "%~dp0"
net stop audiosrv /y >nul 2>&1
taskkill /F /IM audiodg.exe >nul 2>&1
for /f "tokens=2 delims=," %%p in ('tasklist /svc /fi "services eq audiosrv" /fo csv /nh') do (
    if not "%%~p"=="" if not "%%~p"=="N/A" taskkill /F /PID %%~p >nul 2>&1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install_apo.ps1"
taskkill /F /IM audiodg.exe >nul 2>&1
for /f "tokens=2 delims=," %%p in ('tasklist /svc /fi "services eq audiosrv" /fo csv /nh') do (
    if not "%%~p"=="" if not "%%~p"=="N/A" taskkill /F /PID %%~p >nul 2>&1
)
net start AudioEndpointBuilder >nul 2>&1
net start audiosrv
echo.
pause
endlocal
