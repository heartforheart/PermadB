@echo off
setlocal
echo ===================================================
echo               Building PermadB Suite
echo ===================================================

echo [1/5] Compiling PermadB Limiter APO (MSVC x64)...
call "%~dp0PermadBCode\APO\build_apo.bat"
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] APO build failed!
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo [2/5] Building Web UI with Vite...
cd /d "%~dp0PermadBCode\UI"
call npm run build
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] UI build failed!
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo [3/5] Compiling PermadB Application (.NET 9)...
cd /d "%~dp0PermadBCode\App"
dotnet publish PermadB.csproj -c Release -r win-x64 --self-contained false -o "%~dp0dist"
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Application compilation failed!
    pause
    exit /b %ERRORLEVEL%
)
copy /y "%~dp0PermadBCode\APO\PermadBApo.dll" "%~dp0dist\PermadBApo.dll" >nul
copy /y "%~dp0PermadBCode\APO\PermadBApo.inf" "%~dp0dist\PermadBApo.inf" >nul
copy /y "%~dp0PermadBCode\APO\PermadBApo.cat" "%~dp0dist\PermadBApo.cat" >nul
if not exist "%~dp0dist\driver" mkdir "%~dp0dist\driver"
copy /y "%~dp0PermadBCode\APO\PermadBApo.dll" "%~dp0dist\driver\PermadBApo.dll" >nul
copy /y "%~dp0PermadBCode\APO\PermadBApo.inf" "%~dp0dist\driver\PermadBApo.inf" >nul
copy /y "%~dp0PermadBCode\APO\PermadBApo.cat" "%~dp0dist\driver\PermadBApo.cat" >nul

if not exist "%~dp0driver_package" mkdir "%~dp0driver_package"
copy /y "%~dp0PermadBCode\APO\PermadBApo.dll" "%~dp0driver_package\PermadBApo.dll" >nul
copy /y "%~dp0PermadBCode\APO\PermadBApo.inf" "%~dp0driver_package\PermadBApo.inf" >nul
copy /y "%~dp0PermadBCode\APO\PermadBApo.cat" "%~dp0driver_package\PermadBApo.cat" >nul

echo.
echo [4/5] Compiling Uninstaller (.NET 9)...
cd /d "%~dp0installer\uninstaller"
dotnet publish PermadB-Uninstaller.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%~dp0dist"
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Uninstaller compilation failed!
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo [5/5] Packaging PermadB-Setup.exe Installer...
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
