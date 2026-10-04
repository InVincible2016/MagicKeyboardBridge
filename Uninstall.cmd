@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Bootstrap.ps1" -Action Uninstall
if not errorlevel 1 exit /b 0
echo.
echo Press any key to close this window.
pause >nul
exit /b 1
