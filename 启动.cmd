@echo off
cd /d "%~dp0"
if exist "release-next\READY" (
    start "" "release-next\PerfCompass.exe" --data-dir "%~dp0release\data"
) else if exist "release\PerfCompass.exe" (
    start "" "release\PerfCompass.exe" --data-dir "%~dp0release\data"
) else (
    echo Please run build.ps1 -Publish first.
    pause
)
