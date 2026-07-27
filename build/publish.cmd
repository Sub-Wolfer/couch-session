@echo off
REM ============================================================================
REM  Couch Session - build, restart, and optionally commit.
REM
REM    publish.cmd                    build and restart only
REM    publish.cmd "what changed"     build, restart, then commit and push
REM    publish.cmd "what changed" tag also tag the version and push the tag,
REM                                   which is what actually cuts a release
REM
REM  Run with no message and it asks for one. Press Enter at that prompt to
REM  skip the commit - that is the quick rebuild-to-test case, and it must not
REM  leave a trail of "wip" commits behind it.
REM
REM  The build runs BEFORE anything is committed, on purpose. Pushing code that
REM  does not compile is the one mistake this script exists to make impossible:
REM  a failed build exits here and git is never reached.
REM ============================================================================

setlocal EnableDelayedExpansion
cd /d "%~dp0\.."

set "MESSAGE=%~1"
set "TAGIT=%~2"

REM Ask first, force second.
REM
REM A plain taskkill /F was costing a monitor. If a session is running, the app has monitors
REM detached and puts them back during its shutdown - which a force kill never reaches. The
REM displays stayed off, and the next launch captured the reduced desktop as the arrangement to
REM restore, so one rebuild at the wrong moment broke every session after it.
REM
REM Without /F, taskkill posts WM_CLOSE and the app runs HandBack: displays, HDR and audio all go
REM back. The wait below is for that, not for the file lock. /F is still here for a build that has
REM hung or has no message loop to close, because a rebuild has to be able to proceed.
REM
REM The app now also recovers this by itself at startup - see DisplayManager.RecoverFromCrash - but
REM recovering from a mess is not a reason to keep making one.
echo Closing Couch Session if it's running...
taskkill /IM CouchSession.exe >nul 2>&1

REM Three seconds, then force it.
REM
REM Kept short because the polite close usually does nothing: taskkill without /F posts WM_CLOSE to
REM a process's top-level windows, and this app spends its life as a tray icon with none. So the
REM attempt is worth making for the case where the settings window is open, and not worth waiting
REM long for. The startup recovery is what actually makes the force kill safe.
REM
REM ping rather than timeout: timeout reads the console and fails outright with "Input redirection
REM is not supported" whenever this script is run with its output piped, which is every automated
REM build. ping to the loopback address waits without touching stdin and is the same one second.
for /l %%i in (1,1,3) do (
    tasklist /FI "IMAGENAME eq CouchSession.exe" 2>nul | find /i "CouchSession.exe" >nul || goto :closed
    ping -n 2 127.0.0.1 >nul
)

echo   it did not close on its own; forcing it.
taskkill /IM CouchSession.exe /F >nul 2>&1

:closed
REM Give Windows a moment to release the exe file lock before we overwrite it.
ping -n 2 127.0.0.1 >nul

echo Building Couch Session...
dotnet publish CouchSession.csproj -c Release -r win-x64 -o build\out
if errorlevel 1 (
    echo.
    echo BUILD FAILED - nothing has been committed.
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

REM ---------------------------------------------------------------- git ----

REM Nothing to say, nothing to do. Asked rather than assumed, because a default
REM message like "build 2026-07-26" is worse than no commit at all - it fills the
REM history with lines that have to be opened to find out what they were.
if "%MESSAGE%"=="" (
    echo.
    set /p "MESSAGE=Commit message (or press Enter to skip): "
)

if "%MESSAGE%"=="" (
    echo Skipping commit.
    goto :done
)

where git >nul 2>&1
if errorlevel 1 (
    echo.
    echo git is not on your PATH, so the build is done but nothing was committed.
    goto :done
)

REM Is there actually anything to commit? "git commit" with a clean tree fails
REM with an error, which would read as something having gone wrong.
git diff --quiet --exit-code
set "DIRTY=%errorlevel%"
git diff --cached --quiet --exit-code
if errorlevel 1 set "DIRTY=1"

for /f %%U in ('git ls-files --others --exclude-standard') do set "DIRTY=1"

if "%DIRTY%"=="0" (
    echo.
    echo Nothing has changed since the last commit.
    goto :tag
)

echo.
echo Committing...
git add -A
git commit -m "%MESSAGE%"
if errorlevel 1 (
    echo.
    echo The commit failed. Nothing has been pushed.
    exit /b 1
)

echo Pushing...
git push
if errorlevel 1 (
    echo.
    echo The push failed. Your commit is safe locally - it is only the upload
    echo that did not happen. The usual cause is GitHub being ahead of you,
    echo which happens whenever the README is edited on the site. Fix with:
    echo.
    echo     git pull --rebase
    echo     git push
    echo.
    exit /b 1
)

:tag

REM ---------------------------------------------------------------- tag ----

REM Only when asked. A tag is what the release workflow watches, so tagging on
REM every build would publish a release for every rebuild.
if /i not "%TAGIT%"=="tag" goto :done

REM The version out of the csproj, so the tag can never disagree with what the
REM app reports about itself - which is the thing the update checker compares.
set "APPVER="
for /f "tokens=2 delims=<> " %%V in ('findstr /i /c:"<Version>" CouchSession.csproj') do set "APPVER=%%V"

if "%APPVER%"=="" (
    echo.
    echo Could not read ^<Version^> from CouchSession.csproj, so no tag was made.
    goto :done
)

echo.
echo Tagging v%APPVER% ...
git tag "v%APPVER%"
if errorlevel 1 (
    echo That tag already exists. Bump ^<Version^> in CouchSession.csproj first.
    goto :done
)

git push origin "v%APPVER%"
if errorlevel 1 (
    echo.
    echo The tag was made locally but could not be pushed. Try: git push origin v%APPVER%
    goto :done
)

echo.
echo Pushed v%APPVER%. GitHub Actions is building the release now - it takes a
echo few minutes, and the app's update check will find it once it is published.

:done
echo.
endlocal
