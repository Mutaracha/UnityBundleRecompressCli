@echo off
chcp 65001 >nul
setlocal

set "ROOT=%~dp0"
set "PS7_SCRIPT=%ROOT%bin\batch_lzma_cli_pwsh.ps1"

echo.
echo Unity bundle recompress launcher
echo.

where pwsh >nul 2>nul
if not %errorlevel%==0 (
    echo [ERROR] PowerShell 7 не найден
    echo Установите PowerShell 7: winget install Microsoft.PowerShell
    echo.
    pause
    exit /b 1
)

if not exist "%PS7_SCRIPT%" (
    echo [ERROR] Не найден скрипт:
    echo %PS7_SCRIPT%
    echo.
    pause
    exit /b 1
)

echo [OK] Найден PowerShell 7
echo [RUN] %PS7_SCRIPT%
echo.

if not exist "%ROOT%log" mkdir "%ROOT%log"

cls
pwsh -NoProfile -ExecutionPolicy Bypass -File "%PS7_SCRIPT%"
set "RESULT=%errorlevel%"

rem Штатное завершение скрипт ждёт клавишу сам (Pause-OnExit).
rem Здесь пауза только при ошибке запуска, чтобы окно не закрылось молча.
if not "%RESULT%"=="0" (
    echo.
    echo [ERROR] Код завершения: %RESULT%
    pause
)
endlocal & exit /b %RESULT%
