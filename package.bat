@echo off
setlocal
cd /d "%~dp0"

call scripts\publish-portable.bat
if errorlevel 1 exit /b %errorlevel%
