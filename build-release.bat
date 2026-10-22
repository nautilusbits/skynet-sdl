@echo off
rem Release build of the port: release\skynet-sdl.exe (Native AOT, no .NET runtime needed, shaders embedded)
rem with SDL3.dll beside it. Needs the .NET 10 SDK and the Visual Studio C++ tools (the AOT linker);
rem the Vulkan SDK adds the SPIR-V and Metal shaders (D3D12 only without it).
rem Only the program is replaced: anything else in release\ stays.
rem Extra arguments go to dotnet publish, e.g.  build-release.bat -r linux-x64
setlocal
cd /d "%~dp0"
set RC=1

where dotnet >nul 2>nul
if errorlevel 1 (
    echo dotnet was not found - install the .NET 10 SDK.
    goto end
)

rem The AOT linker finds the C++ tools through vswhere.
set "PATH=%PATH%;%ProgramFiles(x86)%\Microsoft Visual Studio\Installer"

rem The Vulkan SDK's dxc and spirv-cross build the Vulkan and Metal shaders; take the newest if it isn't set.
if not defined VULKAN_SDK for /d %%D in ("C:\VulkanSDK\*") do set "VULKAN_SDK=%%~fD"
if not defined VULKAN_SDK echo note: no Vulkan SDK found, only the Direct3D 12 shaders are built

rem SDL3.dll is the engine's (RetroEng.Native: SDL with a D3D12 fix) in place of the ppy.SDL3-CS package's.

if exist release\skynet-sdl.exe del release\skynet-sdl.exe
dotnet publish src\SkyNet.App\SkyNet.App.csproj -c Release -r win-x64 -o release %*
set RC=%errorlevel%
if not "%RC%"=="0" goto end
rem The master servers a release ships (not in git: release-config\ is ignored), one host[:port] a line.
if exist release-config\masters.txt (
    copy /y release-config\masters.txt release\masters.txt >nul
    echo Copied release-config\masters.txt to release\masters.txt
)
rem The tracked samples (config\, every option with its default and a comment) go beside the exe
rem under their .sample names only: a real masters.txt or server.json there is never touched.
copy /y config\masters.sample.txt release\masters.sample.txt >nul
copy /y config\server.sample.json release\server.sample.json >nul
echo Copied config\masters.sample.txt and config\server.sample.json to release\
rem The sample mods (mods-samples\, off until turned on under OPTIONS - MODS) go to release\mods\
rem once: a sample a player has edited there is never overwritten.
for /d %%M in (mods-samples\*) do (
    if not exist "release\mods\%%~nxM" (
        xcopy /e /i /q "%%M" "release\mods\%%~nxM" >nul
        echo Copied sample mod %%~nxM to release\mods\
    )
)
echo.
echo Built: "%CD%\release\skynet-sdl.exe"
echo Run:   release\skynet-sdl.exe --data ^<the SkyNET folder^>

:end
if not "%RC%"=="0" (
    echo.
    echo BUILD FAILED, exit code %RC%
)
rem keep the window open when the file was double-clicked (cmd /c), not when it runs in a console; NOPAUSE=1 skips it
set "CMDLINE=%cmdcmdline:"=%"
if not defined NOPAUSE if /i not "%CMDLINE: /c =%"=="%CMDLINE%" pause
exit /b %RC%
