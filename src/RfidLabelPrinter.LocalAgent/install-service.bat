@echo off
echo ========================================================
echo Installing RfidLabelPrinterAgent Windows Service...
echo ========================================================

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo [ERROR] Please right-click and "Run as administrator".
    pause
    exit /b 1
)

set SERVICE_NAME=RfidLabelPrinterAgent
set BIN_PATH=%~dp0RfidLabelPrinter.LocalAgent.exe

echo Creating service: %SERVICE_NAME%
sc.exe create %SERVICE_NAME% binPath= "\"%BIN_PATH%\"" start= auto DisplayName= "RFID Label Printer Local Agent"

if %errorLevel% equ 0 (
    echo Service created successfully.
    echo Starting service...
    sc.exe start %SERVICE_NAME%
    echo Done! The agent will now run automatically on Windows startup.
) else (
    echo [ERROR] Failed to create service.
)

pause
