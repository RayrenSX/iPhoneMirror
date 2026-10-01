@echo off
setlocal
chcp 65001 >nul
title iPhoneMirror

set "DRIVER_HOST=%~dp0iPhoneMirror.Driver.exe"
if not exist "%DRIVER_HOST%" (
    echo 找不到 iPhoneMirror.Driver.exe。请将启动器放在驱动管理器所在文件夹。
    echo 找不到 iPhoneMirror.Driver.exe。請將啟動器放在驅動程式管理員所在資料夾。
    echo iPhoneMirror.Driver.exe was not found. Place this launcher in the driver manager folder.
    exit /b 2
)

"%DRIVER_HOST%" --run-driver-cleanup
set "EXIT_CODE=%ERRORLEVEL%"

echo.
if not "%EXIT_CODE%"=="0" (
    echo 操作未完成。退出代码：%EXIT_CODE%
    echo 操作未完成。結束代碼：%EXIT_CODE%
    echo The operation did not complete. Exit code: %EXIT_CODE%
)
exit /b %EXIT_CODE%
