@echo off
setlocal
cd /d "%~dp0"

where pwsh.exe >nul 2>nul
if errorlevel 1 (
  echo PowerShell 7 is required.
  echo Install it from https://aka.ms/powershell
  pause
  exit /b 1
)

start "" pwsh.exe -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0ytdl-manager-gui.ps1"
