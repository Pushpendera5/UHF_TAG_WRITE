@echo off
echo ========================================================
echo Uninstalling RfidLabelPrinterAgent Windows Service...
echo ========================================================

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo [ERROR] Please right-click and "Run as administrator".
    pause
    exit /b 1
)

set SERVICE_NAME=RfidLabelPrinterAgent

echo Stopping service: %SERVICE_NAME%
sc.exe stop %SERVICE_NAME%

echo Deleting service: %SERVICE_NAME%
sc.exe delete %SERVICE_NAME%

echo Done! The service has been removed.
pause
