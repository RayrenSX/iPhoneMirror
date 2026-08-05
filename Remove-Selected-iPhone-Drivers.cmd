@echo off
setlocal
title iPhone/iPad Driver Cleanup

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\remove_selected_iphone_drivers.ps1" %*
set "EXIT_CODE=%ERRORLEVEL%"

echo.
if not "%EXIT_CODE%"=="0" (
    echo Operation did not complete. Exit code: %EXIT_CODE%
)
echo Press any key to close this window...
pause >nul
exit /b %EXIT_CODE%
