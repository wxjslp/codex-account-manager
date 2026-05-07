@echo off
setlocal
cd /d "%~dp0"
set "APP=dist\CodexAccountManager-win-x64\CodexAccountManager.exe"
if not exist "%APP%" (
  call scripts\publish-portable.bat
  if errorlevel 1 exit /b %errorlevel%
)
start "" "%APP%"
