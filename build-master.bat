@echo off
rem Release build of the master server (docs/multiplayer.md): release\master\<rid>\SkyNet.Master(.exe).
rem No SDL, no GPU, no game data. Default win-x64 as Native AOT (needs the Visual Studio C++ tools).
rem Another runtime, e.g.  build-master.bat linux-x64  , is built self-contained single-file instead
rem (Native AOT cannot cross-compile from Windows); copy that folder to the server and run it there.
rem Extra arguments after the runtime go to dotnet publish.
setlocal
cd /d "%~dp0"
set RC=1
set "RID=%~1"
if "%RID%"=="" set RID=win-x64
if not "%~1"=="" shift

where dotnet >nul 2>nul
if errorlevel 1 (
    echo dotnet was not found - install the .NET 10 SDK.
    goto end
)

set "OUT=release\master\%RID%"
if /i "%RID:~0,4%"=="win-" (
    set "PATH=%PATH%;%ProgramFiles(x86)%\Microsoft Visual Studio\Installer"
    dotnet publish tools\SkyNet.Master\SkyNet.Master.csproj -c Release -r %RID% -o "%OUT%" -p:PublishAot=true %1 %2 %3 %4 %5 %6
) else (
    dotnet publish tools\SkyNet.Master\SkyNet.Master.csproj -c Release -r %RID% -o "%OUT%" --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true %1 %2 %3 %4 %5 %6
)
set RC=%errorlevel%
if not "%RC%"=="0" goto end
rem The tracked sample (every key with its default and a comment), under its .sample name only:
rem a real master.json there is never touched.
copy /y config\master.sample.json "%OUT%\master.sample.json" >nul
echo Copied config\master.sample.json to %OUT%\master.sample.json
echo.
echo Built: "%CD%\%OUT%"

:end
if not "%RC%"=="0" (
    echo.
    echo BUILD FAILED, exit code %RC%
)
set "CMDLINE=%cmdcmdline:"=%"
if not defined NOPAUSE if /i not "%CMDLINE: /c =%"=="%CMDLINE%" pause
exit /b %RC%
