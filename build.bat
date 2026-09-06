@echo off
setlocal
echo ===================================================
echo               Building PermadB Suite
echo ===================================================

echo [1/4] Building Web UI with Vite...
cd /d "%~dp0PermadBCode\UI"
call npm run build
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] UI build failed!
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo [2/4] Compiling PermadB Application (.NET 9)...
cd /d "%~dp0PermadBCode\App"
dotnet publish PermadB.csproj -c Release -r win-x64 --self-contained false -o "%~dp0dist"
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Application compilation failed!
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo [3/4] Compiling Uninstaller...
cd /d "%~dp0installer\uninstaller"
dotnet publish PermadB-Uninstaller.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%~dp0dist"
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Uninstaller compilation failed!
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo [4/4] Packing PermadB-Setup.exe Installer...
cd /d "%~dp0"
if exist "%~dp0installer\payload.zip" del /f /q "%~dp0installer\payload.zip"
powershell -NoProfile -Command "Compress-Archive -Path '%~dp0dist\*' -DestinationPath '%~dp0installer\payload.zip' -Force"

cd /d "%~dp0installer"
dotnet publish PermadB-Installer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "%~dp0dist-setup"
copy /y "%~dp0dist-setup\PermadB-Setup.exe" "%~dp0PermadB-Setup.exe" >nul

echo.
echo Cleaning temporary build artifacts...
if exist "%~dp0dist" rmdir /s /q "%~dp0dist"
if exist "%~dp0dist-setup" rmdir /s /q "%~dp0dist-setup"
if exist "%~dp0installer\payload.zip" del /f /q "%~dp0installer\payload.zip"

echo.
echo ===================================================
echo [SUCCESS] PermadB Build Complete!
echo Installer: %~dp0PermadB-Setup.exe
echo ===================================================
endlocal
