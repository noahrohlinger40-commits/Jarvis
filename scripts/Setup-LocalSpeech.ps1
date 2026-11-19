<#
.SYNOPSIS
  Sets up local, key-free speech recognition for Jarvis (a private Whisper server).

.DESCRIPTION
  Creates an isolated Python environment, installs faster-whisper and its web server, and downloads
  the speech, voice and "hey Jarvis" wake-word models once. It is safe to run again: finished steps are skipped and damaged model files
  are downloaded again.

  Everything is installed under %LOCALAPPDATA%\Jarvis\whisper, deliberately outside the project.
  The project usually lives in a OneDrive-synced folder, and OneDrive corrupts large model files and
  Python environments (it renamed and broke the model files during testing).

  Jarvis then starts and stops the server by itself whenever speechRecognitionBaseUrl in
  jarvis.settings.json points at localhost. Nothing else needs to be launched.

.PARAMETER Model
  faster-whisper model to download. small.en is a good balance of accuracy and speed on a laptop CPU.
  Use base.en for faster but less accurate results, or medium.en for slower but more accurate ones.

.PARAMETER SpeechHome
  Where the environment and model live. Jarvis looks in the default location, so only change this if
  you also set the JARVIS_SPEECH_HOME environment variable to the same folder.
#>
# Header: this one-time setup script prepares everything the local speech server (scripts\whisper-server.py)
# needs: a Python virtual environment, the pip packages in whisper-requirements.txt, and three model downloads
# (speech-to-text Whisper, text-to-speech Kokoro, and the "hey Jarvis" wake word). Run it once by hand.
[CmdletBinding()]
param(
    [string]$Model = 'small.en',
    [string]$SpeechHome = $(if ($env:JARVIS_SPEECH_HOME) { $env:JARVIS_SPEECH_HOME } else { Join-Path $env:LOCALAPPDATA 'Jarvis\whisper' })
)

# Stop on any error so a half-finished install is noticed.
$ErrorActionPreference = 'Stop'
$venvDir = Join-Path $SpeechHome 'venv'
$venvPython = Join-Path $venvDir 'Scripts\python.exe'
$modelDir = Join-Path $SpeechHome ('models\' + $Model.Replace('/', '--'))
$requirements = Join-Path $PSScriptRoot 'whisper-requirements.txt'

Write-Host "Installing into $SpeechHome"

# Step 1: create the private Python environment, only if it does not exist yet. Prefer "python", fall back to the "py" launcher.
Write-Host '1/3 Python environment...'
if (-not (Test-Path $venvPython)) {
    $python = Get-Command python -ErrorAction SilentlyContinue
    if (-not $python) { $python = Get-Command py -ErrorAction SilentlyContinue }
    if (-not $python) { throw 'Python 3.10 or newer is required. Install it from https://www.python.org/downloads/ and run this again.' }

    New-Item -ItemType Directory -Force $SpeechHome | Out-Null
    & $python.Source -m venv $venvDir
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the Python environment.' }
}

# Step 2: install the packages. pip skips ones that are already installed.
Write-Host '2/3 Installing packages (skipped if already installed)...'
& $venvPython -m pip install --quiet --disable-pip-version-check --no-input -r $requirements
if ($LASTEXITCODE -ne 0) { throw 'Package installation failed.' }

# Step 3: Whisper speech model. The inline Python below skips the download when model.bin already exists
# and is over 1 MB, and fails if the file is still too small afterwards (a sign of a broken download).
Write-Host "3/3 Speech model '$Model' (downloaded once, about 0.5 GB for small.en)..."
$env:JARVIS_SETUP_MODEL = $Model
$env:JARVIS_SETUP_MODEL_DIR = $modelDir
& $venvPython -c @'
import os, pathlib
from faster_whisper.utils import download_model

name, folder = os.environ["JARVIS_SETUP_MODEL"], pathlib.Path(os.environ["JARVIS_SETUP_MODEL_DIR"])
weights = folder / "model.bin"
if not (weights.is_file() and weights.stat().st_size > 1_000_000):
    download_model(name, output_dir=str(folder))
if not (weights.is_file() and weights.stat().st_size > 1_000_000):
    raise SystemExit("The model is still incomplete after downloading.")
print(f"model ready: {weights.stat().st_size / 1_048_576:.0f} MB")
'@
if ($LASTEXITCODE -ne 0) { throw 'Model download failed. Check your internet connection and run this again.' }

# Kokoro text-to-speech model: two files (the model and the voice pack), each re-downloaded if missing or under 1 MB.
Write-Host "Voice model 'Kokoro' (downloaded once, about 350 MB)..."
$kokoroDir = Join-Path $SpeechHome 'models\kokoro'
$kokoroBase = 'https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0'
New-Item -ItemType Directory -Force $kokoroDir | Out-Null
foreach ($file in 'kokoro-v1.0.onnx', 'voices-v1.0.bin') {
    $target = Join-Path $kokoroDir $file
    if (-not ((Test-Path $target) -and (Get-Item $target).Length -gt 1000000)) {
        Invoke-WebRequest -Uri "$kokoroBase/$file" -OutFile $target
    }
}

Write-Host "Wake word 'hey Jarvis' (downloaded once, a few MB)..."
# openWakeWord installs without its model files; this fetches only the missing ones.
& $venvPython -c "from openwakeword.utils import download_models; download_models(['hey_jarvis'])"
if ($LASTEXITCODE -ne 0) { throw 'Wake-word model download failed. Check your internet connection and run this again.' }

Write-Host ''
Write-Host 'Local speech recognition and voice are ready. In jarvis.settings.json use:'
Write-Host '  "speechRecognitionBaseUrl": "http://127.0.0.1:8178/v1",'
Write-Host "  `"speechRecognitionModel`": `"$Model`","
Write-Host 'Then start Jarvis as usual. It launches the speech server automatically.'
