@echo off
setlocal
title iPhone/iPad Remove All Drivers

set "DRIVER_HOST=%~dp0iPhoneMirror.Driver.exe"
if not exist "%DRIVER_HOST%" (
    echo iPhoneMirror.Driver.exe was not found next to this launcher.
    exit /b 2
)

"%DRIVER_HOST%" --run-driver-cleanup
set "EXIT_CODE=%ERRORLEVEL%"

echo.
if not "%EXIT_CODE%"=="0" (
    echo Operation did not complete. Exit code: %EXIT_CODE%
)
exit /b %EXIT_CODE%
