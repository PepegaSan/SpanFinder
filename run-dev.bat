@echo off
cd /d "%~dp0"
title SpanFinder Dev
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-dev.ps1" %*
if errorlevel 1 (
    echo.
    echo --- Fehler beim Start ---
) else (
    echo.
    echo --- Fertig ---
)
echo.
pause