@echo off
REM Build Couch Session, then restart it. The finished CouchSession.exe lands in the project root.

setlocal
cd /d "%~dp0\.."

echo Closing Couch Session if it's running...
taskkill /IM CouchSession.exe /F >nul 2>&1

REM Give the tray a second to release the exe file lock before we overwrite it.
timeout /t 1 /nobreak >nul

echo Building Couch Session...
dotnet publish CouchSession.csproj -c Release -r win-x64 -o build\out
if errorlevel 1 (
    echo.
    echo BUILD FAILED
    exit /b 1
)

REM Copy just the exe up to the main folder - that's the whole deliverable.
copy /y "build\out\CouchSession.exe" "CouchSession.exe" >nul
if errorlevel 1 (
    echo.
    echo Built OK, but couldn't copy the exe to the main folder.
    echo It may still be running - quit it from the tray and run this again.
    exit /b 1
)

echo.
for %%F in ("CouchSession.exe") do echo Done: %%~fF  ^(%%~zF bytes^)

echo Relaunching Couch Session...
start "" "CouchSession.exe"

endlocal
