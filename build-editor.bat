@echo off
rem Release build of the mission editor: release\SkyNetEdit.exe (Native AOT) with cimgui.dll beside it,
rem next to the game's skynet-sdl.exe and SDL3.dll. Same needs as build-release.bat (.NET 10 SDK, the
rem Visual Studio C++ tools, optionally the Vulkan SDK). Only the editor is replaced: anything else in release\ stays.
rem Extra arguments go to dotnet publish.
setlocal
cd /d "%~dp0"
set RC=1

where dotnet >nul 2>nul
if errorlevel 1 (
    echo dotnet was not found - install the .NET 10 SDK.
    goto end
)

set "PATH=%PATH%;%ProgramFiles(x86)%\Microsoft Visual Studio\Installer"
if not defined VULKAN_SDK for /d %%D in ("C:\VulkanSDK\*") do set "VULKAN_SDK=%%~fD"
if not defined VULKAN_SDK echo note: no Vulkan SDK found, only the Direct3D 12 shaders are built

if exist release\SkyNetEdit.exe del release\SkyNetEdit.exe
dotnet publish src\SkyNet.Edit\SkyNet.Edit.csproj -c Release -r win-x64 -o release %*
set RC=%errorlevel%
if not "%RC%"=="0" goto end
echo.
echo Built: "%CD%\release\SkyNetEdit.exe"
echo Run:   release\SkyNetEdit.exe --data ^<the SkyNET folder^> --mission 1 [--mod ^<name^>]

:end
if not "%RC%"=="0" (
    echo.
    echo BUILD FAILED, exit code %RC%
)
set "CMDLINE=%cmdcmdline:"=%"
if not defined NOPAUSE if /i not "%CMDLINE: /c =%"=="%CMDLINE%" pause
exit /b %RC%
