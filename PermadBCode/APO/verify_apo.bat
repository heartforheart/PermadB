@echo off
setlocal
cd /d "%~dp0"
dotnet run --project PermadBApoTest\PermadBApoTest.csproj -c Release --no-build
pause
endlocal
