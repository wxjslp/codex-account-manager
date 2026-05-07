@echo off
setlocal
cd /d "%~dp0\.."
dotnet restore CodexAccountManager.sln
if errorlevel 1 exit /b %errorlevel%
dotnet test tests\CodexAccountManager.Core.Tests\CodexAccountManager.Core.Tests.csproj
if errorlevel 1 exit /b %errorlevel%
dotnet build CodexAccountManager.sln -p:Platform=x64 --no-restore
