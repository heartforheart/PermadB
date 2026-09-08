@echo off
setlocal
echo ===================================================
echo           Building PermadB Limiter APO (x64)
echo ===================================================

call "A:\Windows Software\Visual Studio Code\VS3\VC\Auxiliary\Build\vcvars64.bat"
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Failed to initialize Visual Studio build environment.
    exit /b %ERRORLEVEL%
)

cd /d "%~dp0"

echo Compiling PermadBApo.dll...
cl /O2 /LD /EHsc /std:c++17 /D_WIN32_WINNT=0x0A00 /DWINVER=0x0A00 /D_WINDLL /D_USRDLL PermadBApo.cpp /link /NODEFAULTLIB:atls.lib /DEF:PermadBApo.def /OUT:PermadBApo.dll AudioBaseProcessingObjectV140.lib audiomediatypecrt.lib audioeng.lib kernel32.lib ole32.lib oleaut32.lib advapi32.lib user32.lib uuid.lib

if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Compilation failed!
    exit /b %ERRORLEVEL%
)

echo [SUCCESS] PermadBApo.dll built successfully!
endlocal
