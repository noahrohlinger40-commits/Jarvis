# Ensure-DotNetSdk.ps1: makes sure a private copy of the .NET SDK exists in tools\dotnet.
# Called by Start Jarvis.cmd and Build Jarvis.cmd before every build. If the right version is already
# there it exits quickly; otherwise it downloads Microsoft's official dotnet-install script and runs it.
# Needs internet only on the first run.
param(
    [string]$SdkVersion = "8.0.400",
    [string]$InstallDir = (Join-Path $PSScriptRoot "..\tools\dotnet")
)

# Treat undeclared variables as errors, and stop on the first failure.
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

# Turn the install folder into an absolute path (relative ones are taken from the project root).
$repoRoot = Split-Path -Parent $PSScriptRoot
$resolvedInstallDir = if ([System.IO.Path]::IsPathRooted($InstallDir))
{
    [System.IO.Path]::GetFullPath($InstallDir)
}
else
{
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $InstallDir))
}

# dotnet.exe inside the private install folder.
$dotnetExe = Join-Path $resolvedInstallDir "dotnet.exe"

# Already installed? "dotnet --list-sdks" prints lines like "8.0.400 [path]"; if one starts with the wanted version, we are done.
if (Test-Path $dotnetExe)
{
    $installed = & $dotnetExe --list-sdks 2>$null | Where-Object { $_ -like "$SdkVersion *" }
    if ($LASTEXITCODE -eq 0 -and $installed)
    {
        Write-Host ".NET SDK $SdkVersion already available at $resolvedInstallDir."
        exit 0
    }
}

# Not installed (or wrong version): download the installer into a scratch folder, .tmpbuild.
$tmpDir = Join-Path $repoRoot ".tmpbuild"
$installScript = Join-Path $tmpDir "dotnet-install.ps1"

New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null
New-Item -ItemType Directory -Force -Path $resolvedInstallDir | Out-Null

# Microsoft's official bootstrap script for installing .NET SDKs.
Write-Host "Downloading .NET SDK $SdkVersion bootstrapper..."
Invoke-WebRequest "https://dot.net/v1/dotnet-install.ps1" -OutFile $installScript

# -NoPath: do not add this SDK to the system PATH; Jarvis's .cmd files point at it directly.
Write-Host "Installing .NET SDK $SdkVersion into $resolvedInstallDir..."
& $installScript -Version $SdkVersion -InstallDir $resolvedInstallDir -Architecture x64 -NoPath

# Final check that the install really worked.
if (-not (Test-Path $dotnetExe))
{
    throw "dotnet.exe was not created in $resolvedInstallDir."
}

$installed = & $dotnetExe --list-sdks 2>$null | Where-Object { $_ -like "$SdkVersion *" }
if ($LASTEXITCODE -ne 0 -or -not $installed)
{
    throw "The local .NET SDK install did not expose SDK $SdkVersion."
}

Write-Host ".NET SDK $SdkVersion is ready at $resolvedInstallDir."
