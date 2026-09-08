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
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install_apo.ps1"
echo.
pause
endlocal
