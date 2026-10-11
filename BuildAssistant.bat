@echo off
setlocal
cd /d "%~dp0"
set "PS64=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "PS64=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
"%PS64%" -NoLogo -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0StartAssistant.ps1" -BuildOnly
set "EC=%ERRORLEVEL%"
if not "%EC%"=="0" pause
exit /b %EC%
