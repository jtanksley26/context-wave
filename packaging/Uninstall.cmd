@echo off
rem Removes Context Wave for the current user.
rem The script is copied out first: it cannot delete the folder it is running from.
copy /y "%~dp0uninstall.ps1" "%TEMP%\mdreader-uninstall.ps1" >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%TEMP%\mdreader-uninstall.ps1"
echo.
pause
