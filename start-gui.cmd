@echo off
setlocal
cd /d "%~dp0"

if exist "%~dp0VideoDownloader.exe" (
  start "" "%~dp0VideoDownloader.exe"
  exit /b 0
)

if exist "%~dp0dist\VideoDownloader.exe" (
  start "" "%~dp0dist\VideoDownloader.exe"
  exit /b 0
)

where pwsh.exe >nul 2>nul
if errorlevel 1 (
  echo PowerShell 7 is required.
  echo Install it from https://aka.ms/powershell
  pause
  exit /b 1
)

start "" pwsh.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -STA -File "%~dp0ytdl-manager-gui.ps1"
