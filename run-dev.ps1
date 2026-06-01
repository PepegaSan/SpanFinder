# SpanFinder — lokaler Dev-Start (ohne Visual Studio)
param(
    [switch]$Build,
    [switch]$BuildOnly
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
$Project = Join-Path $Root "src\Span\Span\Span.csproj"
$OutDir = Join-Path $Root "src\Span\Span\bin\x64\Debug\net8.0-windows10.0.19041.0"
$Exe = Join-Path $OutDir "Span.exe"

function Stop-SpanProcesses {
    $names = @('Span', 'Span.Thumbs')
    foreach ($name in $names) {
        $procs = Get-Process -Name $name -ErrorAction SilentlyContinue
        if (-not $procs) { continue }
        Write-Host "Beende $name (PID $($procs.Id -join ', '))..." -ForegroundColor Yellow
        $procs | Stop-Process -Force
    }
    Start-Sleep -Milliseconds 500
}

function Show-SpanWindow {
    param([System.Diagnostics.Process]$Process)

    if ($Process.MainWindowHandle -eq [IntPtr]::Zero) { return $false }

    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SpanWin32 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
"@ -ErrorAction SilentlyContinue | Out-Null

    [SpanWin32]::ShowWindow($Process.MainWindowHandle, 9) | Out-Null
    return [SpanWin32]::SetForegroundWindow($Process.MainWindowHandle)
}

function Invoke-SpanBuild {
    Write-Host "Baue SpanFinder (nur Fehler werden angezeigt)..." -ForegroundColor Cyan
    dotnet build $Project -p:Platform=x64 -v:q -clp:ErrorsOnly --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host "Build OK." -ForegroundColor Green
}

$needsBuild = $Build -or $BuildOnly -or -not (Test-Path $Exe)

if ($needsBuild) {
    Stop-SpanProcesses
    Invoke-SpanBuild
    if ($BuildOnly) { exit 0 }
}

$running = Get-Process -Name Span -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "SpanFinder laeuft bereits (PID $($running.Id))." -ForegroundColor Yellow
    if (Show-SpanWindow -Process $running) {
        Write-Host "Fenster in den Vordergrund geholt." -ForegroundColor Green
    } else {
        Write-Host "App laeuft (evtl. nur in der Taskleiste sichtbar)." -ForegroundColor Cyan
    }
    exit 0
}

if (-not (Test-Path $Exe)) {
    Write-Host "Span.exe nicht gefunden. Nutze build-dev.bat zum Bauen." -ForegroundColor Red
    exit 1
}

Write-Host "Starte SpanFinder..." -ForegroundColor Green
Start-Process -FilePath $Exe -WorkingDirectory $OutDir
Write-Host "SpanFinder gestartet." -ForegroundColor Green
