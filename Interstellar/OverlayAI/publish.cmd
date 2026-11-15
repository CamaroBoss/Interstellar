@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" -Mode Portable
if errorlevel 1 (
    echo.
    echo Interstellar publishing failed. Review the error above.
    pause
    exit /b 1
)
echo.
echo Interstellar.exe was created in the dist\portable folder.
pause
