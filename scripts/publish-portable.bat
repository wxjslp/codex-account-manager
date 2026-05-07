@echo off
setlocal
cd /d "%~dp0\.."

set "OUT=dist\CodexAccountManager-win-x64"
set "ZIP=dist\CodexAccountManager-win-x64-portable.zip"
set "EXE=CodexAccountManager.exe"

dotnet restore CodexAccountManager.sln
if errorlevel 1 exit /b %errorlevel%

dotnet test tests\CodexAccountManager.Core.Tests\CodexAccountManager.Core.Tests.csproj --no-restore
if errorlevel 1 exit /b %errorlevel%

if exist "%OUT%\CodexAccountManager.App.*" del /q "%OUT%\CodexAccountManager.App.*"

dotnet publish src\CodexAccountManager.App\CodexAccountManager.App.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o "%OUT%"
if errorlevel 1 exit /b %errorlevel%

if exist "%ZIP%" del "%ZIP%"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%OUT%' -DestinationPath '%ZIP%' -CompressionLevel Optimal"
if errorlevel 1 exit /b %errorlevel%

echo.
echo Portable app published to:
echo %CD%\%OUT%\%EXE%
echo.
echo Portable zip:
echo %CD%\%ZIP%
