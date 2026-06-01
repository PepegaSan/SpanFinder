@echo off
cd /d "%~dp0"
title SpanFinder Build
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-dev.ps1" -BuildOnly
if errorlevel 1 (
    echo.
    echo --- Build fehlgeschlagen ---
    pause
    exit /b 1
)
ping -n 3 127.0.0.1 >nul
