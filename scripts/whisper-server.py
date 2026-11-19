# whisper-server.py: a small local web server (FastAPI + uvicorn) that gives Jarvis its speech features
# without any cloud service. It listens on http://127.0.0.1:8178 by default (JARVIS_WHISPER_PORT) and serves:
#   POST /v1/audio/transcriptions  speech-to-text (faster-whisper); returns the spoken words as text
#   POST /v1/audio/speech          text-to-speech (Kokoro); streams spoken audio back
#   POST /v1/wake                  wake-word check: was "hey Jarvis" said in this clip? (openWakeWord)
#   GET  /health, GET /v1/models   liveness checks (/health also says whether the voice has finished loading)
# Who calls it: the Jarvis desktop app. AppBootstrapper.cs starts this script as a hidden background process
# (passing JARVIS_PARENT_PID so it quits when Jarvis does); OpenAiCompatibleVoiceRuntime.cs and
# OpenAiCompatibleStreamingSpeaker.cs send the audio and text; the wake-word engine file
# data/wake-engines/openwakeword-hey-jarvis.json points the wake check at /v1/wake.
# The endpoints copy OpenAI's API shapes so the app can use the same code for this and for OpenAI itself.
# Models are installed beforehand by scripts\Setup-LocalSpeech.ps1.
"""Local, OpenAI-compatible speech server for Jarvis.

Serves POST /v1/audio/transcriptions (faster-whisper) and POST /v1/audio/speech (Kokoro), the
request shapes Jarvis already sends, all on the CPU, so voice needs no API key and no cloud service.

Settings come from environment variables so the launcher can tune them without edits:
  JARVIS_WHISPER_MODEL    model name or path        (default: small.en)
  JARVIS_WHISPER_HOST     bind address              (default: 127.0.0.1)
  JARVIS_WHISPER_PORT     port                      (default: 8178)
  JARVIS_WHISPER_THREADS  CPU threads for inference (default: 6)
  JARVIS_WHISPER_BEAM     beam size                 (default: 3)
  JARVIS_WHISPER_MODELS   model directory           (default: %LOCALAPPDATA%/Jarvis/whisper/models)
  JARVIS_WHISPER_HINT     short vocabulary hint     (default: none, see the note near VOCABULARY_HINT)
  JARVIS_WHISPER_CLIENT_PROMPT  set to 1 to pass the app's prompt to Whisper (default: off, see below)
  JARVIS_WHISPER_SAVE_DIR save every upload here for debugging (default: off)
  JARVIS_WHISPER_LOG      log file to write to      (default: console)
  JARVIS_TTS_VOICE        Kokoro voice used when the app asks for one Kokoro lacks (default: af_heart)
  JARVIS_PARENT_PID       exit when this process exits (set by Jarvis so the server never outlives it)
"""

# Make type hints like "WhisperModel | None" work as plain text on any supported Python version.
from __future__ import annotations

import io
import os
import re
import sys
import threading
import time
import wave
from pathlib import Path

import uvicorn
from fastapi import Body, FastAPI, File, Form, HTTPException, Request, UploadFile
from fastapi.responses import JSONResponse, PlainTextResponse, Response, StreamingResponse
from faster_whisper import WhisperModel
from faster_whisper.utils import download_model

# Settings: read from environment variables (see the list at the top) so the launcher can change them without editing this file.
MODEL_NAME = os.environ.get("JARVIS_WHISPER_MODEL", "small.en")
HOST = os.environ.get("JARVIS_WHISPER_HOST", "127.0.0.1")
PORT = int(os.environ.get("JARVIS_WHISPER_PORT", "8178"))
CPU_THREADS = int(os.environ.get("JARVIS_WHISPER_THREADS", "6"))
BEAM_SIZE = int(os.environ.get("JARVIS_WHISPER_BEAM", "3"))
# Kept outside the project on purpose: the project usually lives in a OneDrive-synced folder, and sync
# conflicts corrupt large model files (and the symbolic links a Hugging Face cache is built from).
MODEL_DIR = os.environ.get(
    "JARVIS_WHISPER_MODELS",
    str(Path(os.environ.get("LOCALAPPDATA", str(Path.home()))) / "Jarvis" / "whisper" / "models"),
)

# Whisper is English-only when the model name ends in ".en"; a language hint is then unnecessary.
ENGLISH_ONLY = MODEL_NAME.endswith(".en")

# Jarvis sends a long block of *instructions* as the transcription prompt. That suits OpenAI's GPT-4o
# transcription models, but Whisper treats the prompt as earlier speech to continue from. On unclear
# audio it then repeats the instructions back as the transcript ("Preserve command phrases..."). So the
# app's prompt is ignored by default.
#
# No vocabulary hint is used by default either. A hint such as "Jarvis." makes Whisper treat the wake
# word as already said, so it sometimes drops it from the start of a command. Without a hint the word
# is at worst misspelled ("Garbus"), and the app matches known misspellings as wake words.
USE_CLIENT_PROMPT = os.environ.get("JARVIS_WHISPER_CLIENT_PROMPT") == "1"
VOCABULARY_HINT = os.environ.get("JARVIS_WHISPER_HINT", "")
SAVE_DIR = os.environ.get("JARVIS_WHISPER_SAVE_DIR")
TTS_VOICE = os.environ.get("JARVIS_TTS_VOICE", "af_heart")
# fp32 on purpose: on a laptop CPU the int8 Kokoro build ran about 4x slower than real time.
TTS_MODEL_DIR = Path(MODEL_DIR) / "kokoro"
# Kokoro trims the silence off the end of each sentence it renders, and sentences are rendered one at
# a time, so without this they run into each other. 0.25 s is Kokoro's own gap between sentences.
SENTENCE_PAUSE = bytes(2 * int(0.25 * 24000))

# Whisper learned these from video captions and emits them for noise or breathing. Nobody says them
# to an assistant, so a segment that is only one of these is dropped. A lone "you" is the same thing
# on near-silence; it showed up at the start of a lecture recording's second piece.
CAPTION_HALLUCINATIONS = {
    "thanks for watching", "thank you for watching", "thanks for watching and see you next time",
    "please subscribe", "like and subscribe", "subtitles by the amaraorg community", "you",
}

# Whisper can get stuck repeating itself on noise or on Jarvis's own echo ("Thank you. Thank you. ..."
# twenty times), which the app would then treat as a long request. Text that compresses this well is
# Whisper's own sign of that loop; 2.4 is the threshold Whisper itself uses.
REPETITION_COMPRESSION_RATIO = 2.4

# The web application object; the @app.get / @app.post functions below register the endpoints.
app = FastAPI(title="Jarvis local speech server")
# The loaded Whisper model, created once in main() before the server starts accepting requests.
_model: WhisperModel | None = None
_tts = None  # Kokoro, loaded after Whisper so speech recognition is ready first
_tts_error = "The speech voice is still loading."
# One inference at a time keeps CPU use predictable while the local LLM is also running.
_inference_lock = threading.Lock()
_tts_lock = threading.Lock()


def model_is_usable(directory: Path) -> bool:
    """True when the folder holds a real, non-empty model file (not a broken link or placeholder)."""
    weights = directory / "model.bin"
    return weights.is_file() and weights.stat().st_size > 1_000_000


def resolve_model_directory() -> Path:
    """Return a folder of plain model files, downloading them once if they are missing or damaged.

    Plain files are used instead of a Hugging Face cache because that cache is built from symbolic
    links, which cloud-sync tools such as OneDrive can silently break.
    """
    explicit = Path(MODEL_NAME)

    if explicit.is_dir():
        return explicit

    directory = Path(MODEL_DIR) / MODEL_NAME.replace("/", "--")

    if not model_is_usable(directory):
        print(f"[whisper] '{MODEL_NAME}' is missing or damaged; downloading it to {directory} "
              "(one time only)...", flush=True)
        download_model(MODEL_NAME, output_dir=str(directory))

        if not model_is_usable(directory):
            raise RuntimeError(f"The speech model in {directory} is still incomplete after downloading.")

    return directory


# Load the Whisper speech model onto the CPU. compute_type="int8" uses compressed (8-bit) weights, which is faster and lighter on a laptop with little loss in accuracy.
def load_model() -> WhisperModel:
    started = time.perf_counter()
    model = WhisperModel(
        str(resolve_model_directory()),
        device="cpu",
        compute_type="int8",
        cpu_threads=CPU_THREADS,
    )
    print(f"[whisper] loaded '{MODEL_NAME}' in {time.perf_counter() - started:.1f}s "
          f"(cpu_threads={CPU_THREADS}, beam={BEAM_SIZE})", flush=True)
    return model


def transcribe_bytes(audio: bytes, language: str | None, prompt: str | None) -> tuple[str, list[dict], float]:
    """The transcript, its timed segments ({start, end, text, avg_logprob}, in seconds), and how long it took."""
    assert _model is not None
    started = time.perf_counter()

    # Hold the lock for the whole transcription so two requests never run the model at the same time.
    with _inference_lock:
        # Whisper reads the audio from memory, so no temp file is needed.
        segments, _info = _model.transcribe(
            io.BytesIO(audio),
            # English-only models take no language; otherwise use the caller's language, or let Whisper auto-detect.
            language=None if ENGLISH_ONLY and not language else (language or None),
            # Prefer the app's prompt only when explicitly enabled; otherwise use the (usually empty) vocabulary hint.
            initial_prompt=(prompt if USE_CLIENT_PROMPT and prompt else VOCABULARY_HINT) or None,
            beam_size=BEAM_SIZE,
            # temperature 0 = always take the most likely words (no random variation).
            temperature=0.0,
            # Skip silence before decoding. This is what stops Whisper from inventing text
            # such as "Thank you." when the microphone captured only room noise.
            vad_filter=True,
            # Deliberately lenient: the app already gates on voice activity, and a strict filter here
            # trims a quiet first word such as the wake word.
            vad_parameters={
                "threshold": 0.35,
                "min_speech_duration_ms": 100,
                "min_silence_duration_ms": 400,
                "speech_pad_ms": 400,
            },
            # Do not feed earlier text into the next chunk; this avoids one mistake snowballing into repeated text.
            condition_on_previous_text=False,
            # If Whisper thinks a chunk is probably not speech at all (60% or more), drop it.
            no_speech_threshold=0.6,
        )
        # Checked per segment: a long recording can have "Thanks for watching" in a quiet stretch.
        # Keep each segment with its timing, discarding caption hallucinations and repetition loops.
        timed = [
            {"start": round(segment.start, 2), "end": round(segment.end, 2), "text": segment.text.strip(),
             "avg_logprob": round(segment.avg_logprob, 3)}
            for segment in segments
            # Strip everything but letters and spaces so "Thanks for watching!" matches the lowercase set above.
            if re.sub(r"[^a-z ]", "", segment.text.lower()).strip() not in CAPTION_HALLUCINATIONS
            and segment.compression_ratio <= REPETITION_COMPRESSION_RATIO
        ]

    text = " ".join(segment["text"] for segment in timed).strip()
    return text, timed, time.perf_counter() - started


def load_tts() -> None:
    """Load Kokoro in the background. Until it is ready (or if it is missing), /audio/speech answers
    503 and the app speaks with the built-in Windows voice instead."""
    global _tts, _tts_error
    try:
        from kokoro_onnx import Kokoro

        started = time.perf_counter()
        tts = Kokoro(str(TTS_MODEL_DIR / "kokoro-v1.0.onnx"), str(TTS_MODEL_DIR / "voices-v1.0.bin"))
        tts.create("Ready.", voice=TTS_VOICE, lang="en-gb")  # the first synthesis is the slow one
        _tts = tts
        print(f"[tts] loaded Kokoro in {time.perf_counter() - started:.1f}s (voice={TTS_VOICE})", flush=True)
    except Exception as exc:
        _tts_error = f"Kokoro is not available ({exc}). Run scripts\\Setup-LocalSpeech.ps1."
        print(f"[tts] {_tts_error}", flush=True)


# Split a reply into sentences for Kokoro to say one at a time.
def speakable_sentences(text: str) -> list[str]:
    # Emoji and markdown symbols come out as noise or silence, so keep only speakable characters.
    text = re.sub(r"[^\w\s.,!?;:'\"()%$&-]", " ", text)
    # The regex splits after . ! ? ; or : when followed by whitespace, e.g. "Hi. How are you?" becomes "Hi." and "How are you?". Pieces with no letters or digits are dropped.
    return [part.strip() for part in re.split(r"(?<=[.!?;:])\s+", text) if re.search(r"\w", part)]


# Turn one sentence into raw audio bytes. The lock keeps Kokoro to one sentence at a time.
def synthesize_pcm(sentence: str, voice: str, speed: float) -> bytes:
    with _tts_lock:
        # Kokoro voice names starting with "b" are British voices ("bf_..."); everything else uses American English.
        samples, _rate = _tts.create(sentence, voice=voice, speed=speed, lang="en-gb" if voice[:1] == "b" else "en-us")
    # Kokoro returns floats between -1 and 1; convert to 16-bit little-endian integers (32767 is the largest 16-bit value).
    return (samples.clip(-1, 1) * 32767).astype("<i2").tobytes()


# Text-to-speech endpoint. Expects JSON like {"input": "text", "voice": "af_heart", "speed": 1.0, "response_format": "wav"}.
@app.post("/v1/audio/speech")
def create_speech(payload: dict = Body(...)):
    """OpenAI-shaped text-to-speech. Returns 24 kHz 16-bit mono PCM, streamed one sentence at a time
    so playback starts after the first sentence instead of after the whole reply."""
    # Kokoro is still loading or missing: 503 tells the app to fall back to the Windows voice.
    if _tts is None:
        raise HTTPException(status_code=503, detail=_tts_error)

    sentences = speakable_sentences(str(payload.get("input", "")))

    if not sentences:
        raise HTTPException(status_code=400, detail="Nothing to speak.")

    voice = str(payload.get("voice") or "")
    # Unknown voice names fall back to the default voice.
    voice = voice if voice in _tts.get_voices() else TTS_VOICE
    # Clamp the speed to Kokoro's sensible range of half to double.
    speed = min(max(float(payload.get("speed") or 1.0), 0.5), 2.0)

    # "wav" returns one finished WAV file; anything else streams raw audio.
    if payload.get("response_format") == "wav":
        buffer = io.BytesIO()
        with wave.open(buffer, "wb") as output:
            output.setnchannels(1)
            output.setsampwidth(2)
            output.setframerate(24000)
            output.writeframes(SENTENCE_PAUSE.join(synthesize_pcm(s, voice, speed) for s in sentences))
        return Response(buffer.getvalue(), media_type="audio/wav")

    # Streaming mode: generate and send sentence by sentence so the app can start playing right away.
    def stream():
        started = time.perf_counter()
        for index, sentence in enumerate(sentences):
            if index:
                yield SENTENCE_PAUSE
            yield synthesize_pcm(sentence, voice, speed)
        print(f"[tts] {len(sentences)} sentence(s) -> {time.perf_counter() - started:.2f}s", flush=True)

    return StreamingResponse(stream(), media_type="audio/pcm")


_wake = None  # openWakeWord's pretrained hey_jarvis model, loaded on first use
_wake_lock = threading.Lock()
WAKE_THRESHOLD = float(os.environ.get("JARVIS_WAKE_THRESHOLD", "0.5"))


def wake_score(audio: bytes) -> float:
    """Highest hey_jarvis score over a 16-bit WAV at any rate, scored in the model's 80 ms frames."""
    global _wake
    import numpy as np
    from scipy.signal import resample_poly

    # Read the WAV header: sample rate, channel count and bytes per sample, then the raw samples.
    with wave.open(io.BytesIO(audio)) as source:
        rate, channels, width = source.getframerate(), source.getnchannels(), source.getsampwidth()
        frames = source.readframes(source.getnframes())
    if width != 2:
        raise HTTPException(status_code=400, detail="Expected 16-bit PCM audio.")
    # Convert bytes to 16-bit numbers; stereo is averaged down to mono; the model needs 16 kHz, so resample if needed.
    samples = np.frombuffer(frames, dtype=np.int16)
    if channels > 1:
        samples = samples.reshape(-1, channels).mean(axis=1).astype(np.int16)
    if rate != 16000:
        samples = resample_poly(samples, 16000, rate).astype(np.int16)
    # Create the wake-word model lazily on the first request, and reset its memory so a previous clip cannot leak into this one.
    with _wake_lock:
        if _wake is None:
            from openwakeword.model import Model
            _wake = Model(wakeword_models=["hey_jarvis"], inference_framework="onnx")
        _wake.reset()
        # Feed the model 1280 samples (80 ms at 16 kHz) at a time and keep the highest score seen in the clip.
        best = 0.0
        for start in range(0, len(samples) - 1279, 1280):
            best = max(best, max(float(v) for v in _wake.predict(samples[start:start + 1280]).values()))
        return best


@app.post("/v1/wake")
def detect_wake(file: UploadFile = File(...)):
    """Was "hey Jarvis" said? The app's openWakeWord wake-engine manifest posts each utterance here."""
    audio = file.file.read()

    if not audio:
        raise HTTPException(status_code=400, detail="Empty audio upload.")

    try:
        score = wake_score(audio)
    except (wave.Error, EOFError, ValueError) as exc:
        raise HTTPException(status_code=422, detail=f"Could not read the audio: {exc}") from exc

    # shouldArmOnly: a detection opens the follow-up window; it never turns the transcript into a command by itself.
    return {"detected": score >= WAKE_THRESHOLD, "confidence": round(score, 3), "shouldArmOnly": True, "model": "hey_jarvis"}


@app.exception_handler(HTTPException)
def openai_error_shape(_: Request, exc: HTTPException) -> JSONResponse:
    """Errors in OpenAI's {"error": {...}} shape, so OpenAI-SDK clients surface the message instead of a KeyError."""
    return JSONResponse(
        status_code=exc.status_code,
        content={"error": {"message": str(exc.detail), "type": "invalid_request_error", "param": None, "code": None}},
    )


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "ok", "model": MODEL_NAME, "tts": "ready" if _tts is not None else _tts_error}


@app.get("/v1/models")
def list_models() -> dict[str, object]:
    return {"object": "list", "data": [{"id": MODEL_NAME, "object": "model", "owned_by": "local"}]}


# Plain def: FastAPI runs it in a worker thread, so a long transcription can't block /health.
@app.post("/v1/audio/transcriptions")
def create_transcription(
    file: UploadFile = File(...),
    model: str = Form(default=""),  # accepted for API compatibility; the server's own model is used
    language: str | None = Form(default=None),
    prompt: str | None = Form(default=None),
    response_format: str = Form(default="json"),
):
    audio = file.file.read()

    if not audio:
        raise HTTPException(status_code=400, detail="Empty audio upload.")

    if SAVE_DIR:
        Path(SAVE_DIR).mkdir(parents=True, exist_ok=True)
        (Path(SAVE_DIR) / f"{time.strftime('%Y%m%d-%H%M%S')}-{int(time.time() * 1000) % 1000:03d}.wav").write_bytes(audio)

    try:
        text, timed, seconds = transcribe_bytes(audio, language, prompt)
    except Exception as exc:  # decoding errors should surface as a clear HTTP error, not a crash
        raise HTTPException(status_code=422, detail=f"Could not transcribe audio: {exc}") from exc

    print(f"[whisper] {len(audio) / 1024:.0f} KiB -> {seconds:.2f}s: {text!r}", flush=True)

    if response_format == "text":
        return PlainTextResponse(text)

    if response_format == "verbose_json":  # OpenAI's shape; lecture notes use the segment times
        return JSONResponse({"text": text, "segments": timed})

    return JSONResponse({"text": text})


def redirect_output_to_log() -> None:
    """Send stdout/stderr to a file. Jarvis launches this hidden, with no console to print to."""
    log_path = os.environ.get("JARVIS_WHISPER_LOG")

    if not log_path:
        return

    Path(log_path).parent.mkdir(parents=True, exist_ok=True)
    log = open(log_path, "a", buffering=1, encoding="utf-8")
    sys.stdout = log
    sys.stderr = log


def exit_when_parent_exits() -> None:
    """Shut down as soon as the launching Jarvis process ends, even if it crashed."""
    parent_pid = os.environ.get("JARVIS_PARENT_PID")

    # Only on Windows, and only when Jarvis gave us its process id.
    if not parent_pid or os.name != "nt":
        return

    import ctypes

    # SYNCHRONIZE: the permission to wait for a process to end.
    synchronize = 0x00100000
    handle = ctypes.windll.kernel32.OpenProcess(synchronize, False, int(parent_pid))

    if not handle:
        # The parent is already gone, so nobody is left to use this server.
        os._exit(0)

    def wait_then_exit() -> None:
        ctypes.windll.kernel32.WaitForSingleObject(handle, 0xFFFFFFFF)
        os._exit(0)

    threading.Thread(target=wait_then_exit, daemon=True).start()


def main() -> int:
    global _model
    redirect_output_to_log()
    exit_when_parent_exits()
    print(f"[whisper] starting at {time.strftime('%Y-%m-%d %H:%M:%S')}", flush=True)
    _model = load_model()

    # Prime the engine so the first real request is not slower than the rest.
    silence = b"RIFF" + (36 + 32000).to_bytes(4, "little") + b"WAVEfmt " + (16).to_bytes(4, "little") \
        + (1).to_bytes(2, "little") + (1).to_bytes(2, "little") + (16000).to_bytes(4, "little") \
        + (32000).to_bytes(4, "little") + (2).to_bytes(2, "little") + (16).to_bytes(2, "little") \
        + b"data" + (32000).to_bytes(4, "little") + bytes(32000)
    try:
        transcribe_bytes(silence, None, None)
    except Exception as exc:
        print(f"[whisper] warm-up skipped: {exc}", flush=True)

    # Load the voice on a background thread so speech-to-text is ready sooner; /health shows when the voice is ready.
    threading.Thread(target=load_tts, daemon=True).start()
    print(f"[whisper] ready on http://{HOST}:{PORT}/v1", flush=True)
    # Start serving. This call blocks until the process ends.
    uvicorn.run(app, host=HOST, port=PORT, log_level="warning")
    return 0


if __name__ == "__main__":
    sys.exit(main())
