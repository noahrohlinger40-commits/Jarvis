<#
.SYNOPSIS
  Sets up Jarvis's own language-model engine (llama.cpp's llama-server) so it no longer needs LM Studio.

.DESCRIPTION
  Downloads the newest llama.cpp Windows CUDA 12 build (plus the matching CUDA runtime DLLs) and puts a
  model file in place. It is safe to run again: finished steps are skipped. Use -Force to download the
  engine again (an update); the old engine folder is cleared first so no stale DLLs are left behind.

  Everything is installed under %LOCALAPPDATA%\Jarvis\llm, deliberately outside the project.
  The project usually lives in a OneDrive-synced folder, and OneDrive corrupts large model files.

    engine: %LOCALAPPDATA%\Jarvis\llm\bin\llama-server.exe
    model:  %LOCALAPPDATA%\Jarvis\llm\planner.gguf

.PARAMETER ModelPath
  A .gguf file to install as planner.gguf (for example a fine-tuned model). It replaces the current one.
  Without it, planner.gguf is only created when missing, from the LM Studio copy of Qwen3-4B-Instruct-2507
  (Q4_K_M) if present, otherwise downloaded from Hugging Face (about 2.5 GB).

.PARAMETER LlmHome
  Where the engine and model live. Jarvis looks in the default location, so only change this if
  you also set the JARVIS_LLM_HOME environment variable to the same folder.

.PARAMETER Force
  Download the engine again even if llama-server.exe is already installed.
#>
# Header: one-time setup for Jarvis's own language-model server, so it no longer needs LM Studio. It installs
# llama.cpp's llama-server.exe (the program that runs the model) and a planner.gguf model file. Jarvis starts
# that server itself afterwards; this script only prepares the files. Safe to run again.
[CmdletBinding()]
param(
    [string]$ModelPath,
    [string]$LlmHome = $(if ($env:JARVIS_LLM_HOME) { $env:JARVIS_LLM_HOME } else { Join-Path $env:LOCALAPPDATA 'Jarvis\llm' }),
    [switch]$Force
)

# Stop on any error so a half-finished install is noticed.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is far faster without the progress bar
$binDir = Join-Path $LlmHome 'bin'
$server = Join-Path $binDir 'llama-server.exe'
$versionFile = Join-Path $binDir 'version.txt'
# planner.gguf is the model file Jarvis loads. The LM Studio path is a copy that may already be on this PC (saves a 2.5 GB download); the URL is the fallback.
$planner = Join-Path $LlmHome 'planner.gguf'
$lmStudioModel = Join-Path $env:USERPROFILE '.lmstudio\models\lmstudio-community\Qwen3-4B-Instruct-2507-GGUF\Qwen3-4B-Instruct-2507-Q4_K_M.gguf'
$modelUrl = 'https://huggingface.co/lmstudio-community/Qwen3-4B-Instruct-2507-GGUF/resolve/main/Qwen3-4B-Instruct-2507-Q4_K_M.gguf'

Write-Host "Installing into $LlmHome"
New-Item -ItemType Directory -Force $LlmHome | Out-Null

# Step 1: the engine (llama-server.exe plus its CUDA graphics-card libraries).
Write-Host '1/2 llama.cpp engine...'
if ((Test-Path $server) -and -not $Force) {
    $tag = if (Test-Path $versionFile) { (Get-Content $versionFile -Raw).Trim() } else { 'unknown version' }
    Write-Host "  already installed ($tag), skipped. Use -Force to update."
}
else {
    # /releases/latest is not reliable for this repo (it can point at a release with no build files),
    # so take the newest releases and use the first one that has a Windows CUDA 12 build.
    $release = $null
    $recent = Invoke-RestMethod 'https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=10'
    foreach ($candidate in $recent) {
        # Matches the engine zip for Windows with an NVIDIA GPU, e.g. "llama-b4567-bin-win-cuda-12.4-x64.zip" (any build tag, any CUDA 12.x).
        $engineAsset = $candidate.assets | Where-Object { $_.name -match '^llama-.*-bin-win-cuda-12\.[0-9]+-x64\.zip$' } | Select-Object -First 1
        if ($engineAsset) { $release = $candidate; break }
    }
    if (-not $release) { throw 'No recent llama.cpp release has a Windows CUDA 12 build. Check https://github.com/ggml-org/llama.cpp/releases' }
    # Pull the CUDA version (e.g. "12.4") out of that file name, to find the matching runtime zip.
    $cudaVersion = [regex]::Match($engineAsset.name, 'cuda-(12\.[0-9]+)').Groups[1].Value
    # The separate zip of CUDA runtime DLLs for that same version; the engine needs both.
    $runtimeAsset = $release.assets | Where-Object { $_.name -eq "cudart-llama-bin-win-cuda-$cudaVersion-x64.zip" } | Select-Object -First 1
    if (-not $runtimeAsset) { throw "Release $($release.tag_name) has no cudart-llama-bin-win-cuda-$cudaVersion-x64.zip." }

    Write-Host "  release $($release.tag_name), CUDA $cudaVersion (about $([math]::Round(($engineAsset.size + $runtimeAsset.size) / 1MB)) MB)..."
    # Start from an empty folder so DLLs from an older release cannot mix with the new ones.
    if (Test-Path $binDir) { Remove-Item $binDir -Recurse -Force }
    New-Item -ItemType Directory -Force $binDir | Out-Null
    # Download and unzip each file, deleting the zip afterwards even if something failed.
    foreach ($asset in $engineAsset, $runtimeAsset) {
        $zip = Join-Path $LlmHome $asset.name
        try {
            Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip
            Expand-Archive -Path $zip -DestinationPath $binDir -Force
        }
        finally { Remove-Item $zip -Force -ErrorAction SilentlyContinue }
    }
    if (-not (Test-Path $server)) { throw 'llama-server.exe is missing after extracting. The release layout may have changed.' }
    Set-Content -Path $versionFile -Value $release.tag_name -NoNewline
    $tag = $release.tag_name
}

# Step 2: the model file. Order of preference: the file you passed in, one already installed, the LM Studio copy, a fresh download.
Write-Host '2/2 Model...'
if ($ModelPath) {
    if (-not (Test-Path $ModelPath -PathType Leaf)) { throw "ModelPath '$ModelPath' is not a file." }
    Copy-Item $ModelPath $planner -Force
    Write-Host "  installed $ModelPath as planner.gguf"
}
# A real model is gigabytes; anything under 100 MB is treated as missing or damaged.
elseif ((Test-Path $planner) -and (Get-Item $planner).Length -gt 100MB) {
    Write-Host '  planner.gguf already present, skipped.'
}
elseif (Test-Path $lmStudioModel) {
    Copy-Item $lmStudioModel $planner -Force
    Write-Host '  copied the Qwen3-4B-Instruct-2507 model from LM Studio.'
}
else {
    Write-Host '  downloading Qwen3-4B-Instruct-2507 Q4_K_M (about 2.5 GB)...'
    # Download to a temporary name and rename only when complete, so an interrupted download never leaves a half model named planner.gguf.
    $partial = "$planner.download"
    try {
        Invoke-WebRequest -Uri $modelUrl -OutFile $partial
        Move-Item $partial $planner -Force
    }
    catch {
        Remove-Item $partial -Force -ErrorAction SilentlyContinue
        throw "Model download failed: $($_.Exception.Message) Check your internet connection and run this again."
    }
}
# Final sanity check on the model file size.
if ((Get-Item $planner).Length -lt 100MB) { throw 'planner.gguf is too small to be a model. Delete it and run this again.' }

Write-Host ''
Write-Host "engine:  $server"
Write-Host "model:   $planner ($([math]::Round((Get-Item $planner).Length / 1GB, 2)) GB)"
Write-Host "version: $tag"
Write-Host 'Jarvis can now start its own model server; LM Studio is not needed.'
