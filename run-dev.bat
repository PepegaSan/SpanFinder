@echo off
cd /d "%~dp0"
title SpanFinder Dev
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-dev.ps1" %*
if errorlevel 1 (
    echo.
    echo --- Fehler beim Start ---
    pause
) else (
    ping -n 3 127.0.0.1 >nul
)
