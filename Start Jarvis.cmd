@echo off
REM Start Jarvis.cmd: the double-click launcher for the Jarvis desktop app.
REM Steps: (1) make sure a private .NET 8 SDK exists in tools\dotnet (scripts\Ensure-DotNetSdk.ps1),
REM (2) build src\Jarvis.App in Release mode with that SDK, (3) start the built Jarvis.App.exe.
REM It rebuilds on every launch, so edits to the code are picked up without a separate build step.
REM The SDK, NuGet packages and dotnet caches all stay inside the project folder, not system-wide.
REM setlocal keeps every variable set below private to this script run.
setlocal

REM ROOT is the folder this .cmd file lives in (%~dp0 ends with a backslash).
set "ROOT=%~dp0"
REM Release build; the exe path below depends on this name.
set "CONFIG=Release"
REM The project's own copy of .NET, so Jarvis never depends on whatever is installed on the PC.
set "DOTNET_ROOT=%ROOT%tools\dotnet"
set "DOTNET_EXE=%DOTNET_ROOT%\dotnet.exe"
REM Where the build puts the finished app.
set "APP_EXE=%ROOT%src\Jarvis.App\bin\%CONFIG%\net8.0-windows\Jarvis.App.exe"

REM Step 1: download/install the local .NET SDK if it is not already there (does nothing if it is).
echo Ensuring local .NET SDK...
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\Ensure-DotNetSdk.ps1"
if errorlevel 1 (
  echo Failed to provision the local .NET SDK.
  pause
  exit /b 1
)

REM Safety check: the step above should have produced dotnet.exe.
if not exist "%DOTNET_EXE%" (
  echo Local dotnet executable not found at "%DOTNET_EXE%".
  pause
  exit /b 1
)

REM Step 2: build. These variables keep .NET from using any other install or global cache.
echo Building Jarvis desktop app...
REM Point the dotnet tool at the local SDK.
set "DOTNET_ROOT=%ROOT%tools\dotnet"
REM Do not look for other machine-wide .NET installs.
set "DOTNET_MULTILEVEL_LOOKUP=0"
REM Keep the dotnet CLI's per-user data inside the project.
set "DOTNET_CLI_HOME=%ROOT%.dotnet"
REM Skip the first-run welcome banner.
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
REM Keep downloaded NuGet packages inside the project.
set "NUGET_PACKAGES=%ROOT%.nuget\packages"
REM -m:1 builds one project at a time and UseSharedCompilation=false avoids a long-lived compiler process
REM that could keep files locked. Output is hidden (sent to nul); only a failure message is shown.
"%DOTNET_EXE%" build "%ROOT%src\Jarvis.App\Jarvis.App.csproj" -c %CONFIG% -m:1 /p:UseSharedCompilation=false >nul
REM Most common failure: a Jarvis window from an earlier launch is still open and locks the exe.
if errorlevel 1 (
  echo Build failed. Close any running Jarvis window and try again.
  pause
  exit /b 1
)

REM Step 3: confirm the build really produced the exe.
if not exist "%APP_EXE%" (
  echo Built app executable not found at "%APP_EXE%".
  pause
  exit /b 1
)

REM Step 4: start the app from the project folder. PowerShell is used so DOTNET_ROOT can be set just for
REM the new process and the launcher does not wait for Jarvis to close.
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$env:DOTNET_ROOT='%DOTNET_ROOT%'; $env:DOTNET_MULTILEVEL_LOOKUP='0'; Start-Process -WorkingDirectory '%ROOT%' -FilePath '%APP_EXE%'"
if errorlevel 1 (
  echo Failed to launch Jarvis through the local .NET runtime.
  pause
  exit /b 1
)

exit /b 0
