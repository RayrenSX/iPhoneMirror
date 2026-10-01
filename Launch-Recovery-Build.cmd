@echo off
setlocal
set "IPHONE_MIRROR_TEST_DISCONNECT_AFTER_SECONDS="
set "IPHONE_MIRROR_TEST_DISCONNECT_MODES="
set "IPHONE_MIRROR_APP_LOG_DIRECTORY="
start "" /D "%~dp0outputs\recovery-latency-v9" "%~dp0outputs\recovery-latency-v9\iPhoneMirror.exe"
