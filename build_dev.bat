@echo off
cd /d "%~dp0"

dotnet build -c Debug --no-restore

pause
