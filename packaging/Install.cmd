@echo off
rem Installs MD Reader for the current user. No administrator rights are needed.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
if errorlevel 1 (
    echo.
    echo Installation did not finish. See the message above.
)
echo.
pause
