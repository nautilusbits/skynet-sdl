@echo off
rem Builds the Android APK (src\SkyNet.Android: arm64-v8a + x86_64, Mono, SDL_GPU on Vulkan) into
rem release-android\SkyNET-<version>.apk, signed with the debug key. Install: adb install -r release-android\SkyNET-<version>.apk
rem Needs: a .NET 10 SDK with the android workload (C:\android\dotnet is used when it exists - a
rem private SDK, since the machine-wide one's MSI workload install can fail), the Android SDK
rem (ANDROID_HOME, else C:\android\sdk), JDK 21 (JAVA_HOME_21, else C:\android\jdk21; dotnet build
rem -t:InstallAndroidDependencies -p:JavaSdkDirectory=... installs one) and the Vulkan SDK (SPIR-V shaders).
rem The game's data: the app asks for the DOS game's folder on its first start (copied into its
rem storage), or start it once (it makes its folder) and: adb push <gamedata>\. /sdcard/Android/data/net.skynetport.skynet/files/gamedata/
setlocal
cd /d "%~dp0"
set RESULT=1

set "DOTNET=dotnet"
if exist "C:\android\dotnet\dotnet.exe" (
    set "DOTNET=C:\android\dotnet\dotnet.exe"
    set "DOTNET_ROOT=C:\android\dotnet"
    set "DOTNET_MULTILEVEL_LOOKUP=0"
)
if "%ANDROID_HOME%"=="" set "ANDROID_HOME=C:\android\sdk"
if "%JAVA_HOME_21%"=="" set "JAVA_HOME_21=C:\android\jdk21"
if "%VULKAN_SDK%"=="" for /f "tokens=2*" %%A in ('reg query "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment" /v VULKAN_SDK 2^>nul') do set "VULKAN_SDK=%%B"
if "%VULKAN_SDK%"=="" (
    echo The Vulkan SDK was not found ^(VULKAN_SDK^): Android needs the SPIR-V shaders.
    goto end
)

"%DOTNET%" publish src\SkyNet.Android\SkyNet.Android.csproj -c Release -nologo -p:RequiredShaders=spirv ^
    -p:AndroidSdkDirectory="%ANDROID_HOME%" -p:JavaSdkDirectory="%JAVA_HOME_21%" -o release-android\publish
if errorlevel 1 goto end

rem The version from Directory.Build.props, so the file says which build it is.
for /f "usebackq delims=" %%V in (`powershell -NoProfile -Command "(Select-Xml -Path Directory.Build.props -XPath '//Version').Node.InnerText"`) do set "VERSION=%%V"
if "%VERSION%"=="" goto end
set "APK=release-android\SkyNET-%VERSION%.apk"
copy /y release-android\publish\net.skynetport.skynet-Signed.apk "%APK%" >nul
if errorlevel 1 goto end
rmdir /s /q release-android\publish
set RESULT=0
echo.
echo Built: "%CD%\%APK%"

:end
if not "%RESULT%"=="0" (
    echo.
    echo ANDROID BUILD FAILED
)
exit /b %RESULT%
