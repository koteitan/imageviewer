@echo off
rem Build on Windows with the MSBuild of Visual Studio.
rem Output: bin\Release\imageviewer.exe, copied to C:\app\imageviewer\imageviewer.exe
setlocal
set CONFIG=%1
if "%CONFIG%"=="" set CONFIG=Release
for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do set MSBUILD=%%i
"%MSBUILD%" "%~dp0imageviewer.csproj" -nologo -v:minimal -p:Configuration=%CONFIG%
