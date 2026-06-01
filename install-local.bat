@echo off
cd /d "%~dp0"
title SpanFinder Install
echo.
echo SpanFinder - lokale Installation
echo ================================
echo.
taskkill /F /IM Span.exe /T >nul 2>&1
taskkill /F /IM Span.Thumbs.exe /T >nul 2>&1
echo [1/4] Baue Release - kann einige Minuten dauern...
dotnet publish "src\Span\Span\Span.csproj" -c Release -p:Platform=x64 -r win-x64 -v:minimal
if errorlevel 1 goto fail
echo.
echo [2/4] Suche Publish-Ordner...
set "PUB="
for /f "usebackq delims=" %%I in (`powershell -NoProfile -Command "$r=Join-Path '%CD%' 'src\Span\Span\bin\x64\Release'; if (Test-Path $r) { Get-ChildItem $r -Recurse -Directory -Filter publish -EA 0 | Where-Object { Test-Path (Join-Path $_.FullName 'Span.exe') } | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName }"`) do set "PUB=%%I"
if not defined PUB (echo FEHLER: Publish-Ordner nicht gefunden. & goto fail)
echo Gefunden: %PUB%
echo.
echo [3/4] Kopiere Dateien...
set "DEST=%LOCALAPPDATA%\Programs\SpanFinder-Personal"
if exist "%DEST%" rmdir /s /q "%DEST%"
mkdir "%DEST%" 2>nul
xcopy /E /I /Y "%PUB%\*" "%DEST%\"
if not exist "%DEST%\Span.exe" (echo FEHLER: Span.exe fehlt. & goto fail)
echo.
echo [4/4] Startmenue-Verknuepfung...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$d='%DEST%';$e=Join-Path $d 'Span.exe';$l=Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\SpanFinder Personal.lnk';$p=Split-Path $l; if(-not(Test-Path $p)){New-Item -ItemType Directory -Path $p -Force|Out-Null};$s=(New-Object -ComObject WScript.Shell).CreateShortcut($l);$s.TargetPath=$e;$s.WorkingDirectory=$d;$s.IconLocation=($e+',0');$s.Save();Write-Host ('OK: '+$l)"
if errorlevel 1 goto fail
echo.
echo --- Installation abgeschlossen ---
echo Ordner: %DEST%
start "" "%DEST%\Span.exe"
echo.
pause
exit /b 0
:fail
echo.
echo --- Installation fehlgeschlagen ---
echo.
pause
exit /b 1