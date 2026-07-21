@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
cd /d "%SCRIPT_DIR%"

set "EXE=%SCRIPT_DIR%gui\publish\LlamaManager.exe"
if not exist "%EXE%" (
    echo Error: %EXE% not found.
    echo Run this from the llama-manager directory.
    pause
    exit /b 1
)

echo Starting LlamaManager GUI...
start "" "%EXE%"
endlocal
