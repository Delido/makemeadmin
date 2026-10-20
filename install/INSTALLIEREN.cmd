@echo off
setlocal
REM Doppelklick-Installer fuer RunAsAdmin.
REM Hebt sich selbst auf Administratorrechte und ruft Install.ps1 mit ABSOLUTEM
REM Pfad auf (wichtig: nach "Als Admin" ist das Arbeitsverzeichnis sonst System32).

REM In den Ordner dieser cmd wechseln (funktioniert auch nach RunAs).
cd /d "%~dp0"

REM Adminrechte pruefen / anfordern.
net session >nul 2>&1
if %errorlevel% NEQ 0 (
    echo Fordere Administratorrechte an...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo ============================================
echo   RunAsAdmin wird installiert
echo   Ordner: %~dp0
echo ============================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1"
set RC=%errorlevel%

echo.
if "%RC%"=="0" (
    echo FERTIG. Du kannst dieses Fenster schliessen.
) else (
    echo Es ist ein Fehler aufgetreten ^(Code %RC%^). Bitte die Meldung oben lesen.
)
echo.
pause
