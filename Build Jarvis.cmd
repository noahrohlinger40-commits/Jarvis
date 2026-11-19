@echo off
REM Build Jarvis.cmd: command-line build of the whole solution (Jarvis.sln, so the App, Core and test
REM projects), using the project's private .NET SDK from tools\dotnet. It builds only; it does not start
REM Jarvis (use Start Jarvis.cmd for that). Any extra arguments you give it are passed on to dotnet build.
REM setlocal keeps the variables below private to this script run.
setlocal

REM Folder this .cmd file lives in.
set "ROOT=%~dp0"
set "CONFIG=Release"
REM Private .NET SDK folder inside the project.
set "DOTNET_ROOT=%ROOT%tools\dotnet"
set "DOTNET_EXE=%DOTNET_ROOT%\dotnet.exe"
REM Keep the build isolated from any other .NET install, cache or package folder on the PC.
set "DOTNET_MULTILEVEL_LOOKUP=0"
set "DOTNET_CLI_HOME=%ROOT%.dotnet"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "NUGET_PACKAGES=%ROOT%.nuget\packages"

echo Ensuring local .NET SDK...
REM Install the local SDK if it is missing (does nothing if already present).
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\Ensure-DotNetSdk.ps1"
if errorlevel 1 (
  echo Failed to provision the local .NET SDK.
  exit /b 1
)

if not exist "%DOTNET_EXE%" (
  echo Local dotnet executable not found at "%DOTNET_EXE%".
  REM -m:1 and UseSharedCompilation=false: one project at a time, no leftover compiler process holding files. %* forwards extra arguments.
  exit /b 1
)

echo Building Jarvis solution...
"%DOTNET_EXE%" build "%ROOT%Jarvis.sln" -c %CONFIG% -m:1 /p:UseSharedCompilation=false %*
exit /b %errorlevel%
