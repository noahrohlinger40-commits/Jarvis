using Jarvis.Core;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Jarvis.App;

// LectureRecorder.cs: records a lecture or call. It mixes the microphone with whatever the computer is playing (loopback) into
// one 16 kHz mono track, writes it to disk as it arrives, and transcribes it in 30 s pieces in the background so the text is almost ready
// when recording stops. It does not use the wake word pipeline; it only borrows the microphone reader (MicrophonePcmStream)
// and the whisper client from OpenAiCompatibleVoiceRuntime.cs. Primary constructor: "options" is available to all methods.
/// <summary>
/// Records the microphone and the computer's own audio (a Zoom call, a lecture video) as one 16 kHz mono
/// track. The audio goes to a WAV file as it comes in, and every ~30 seconds the new part is transcribed by
/// the local Whisper server (about 9x faster than real time on this laptop), so the transcript is nearly
/// done when recording stops.
/// </summary>
internal sealed class LectureRecorder(JarvisOptions options) : ILectureRecorder
{
    // 16,000 samples per second, 2 bytes per sample = 32,000 bytes of audio per second. A piece is 30 seconds (960,000 bytes).
    private const int SampleRate = MicrophonePcmStream.SampleRateHz;
    private const int BytesPerSecond = SampleRate * 2;
    private const int PieceBytes = 30 * BytesPerSecond;

    // Whisper client with no vocabulary prompt (lectures are not commands).
    private readonly OpenAiCompatibleTranscriptionClient _whisper = new(
        options.SpeechRecognitionBaseUrl,
        options.SpeechRecognitionModel,
        options.SpeechRecognitionLanguage,
        prompt: string.Empty, localAiOnly: options.LocalAiOnly);

    // Finished transcript lines, each at its time offset in the recording. Locked when accessed because transcription runs on other threads.
    private readonly List<TranscriptLine> _lines = [];
    // Microphone source with the same gain used for voice commands.
    private MicrophonePcmStream? _microphone;
    // Captures the sound the computer is playing (WASAPI loopback), e.g. the other side of a call.
    private WasapiLoopbackCapture? _computerAudio;
    // Writes the raw mixed audio to a WAV file as it comes in.
    private WaveFileWriter? _wav;
    // Background task that mixes audio and writes the file (see Pump).
    private Task _pump = Task.CompletedTask;
    // Chain of transcription tasks, run one after another.
    private Task _transcription = Task.CompletedTask;
    private string _wavPath = string.Empty;
    // Total audio bytes written, used for the recording's length.
    private long _bytesRecorded;

    public bool IsRecording => _microphone is not null;

    // Opens the microphone and the computer-audio capture and starts a background loop that mixes and saves them.
    public void Start(string wavPath)
    {
        if (!AudioDeviceResolver.TryResolveMicrophoneDevice(options.SpeechRecognitionDeviceName, out var device))
        {
            throw new InvalidOperationException("no microphone was found");
        }

        _wav = new WaveFileWriter(wavPath, new WaveFormat(SampleRate, 16, 1));
        _wavPath = wavPath;
        _bytesRecorded = 0;
        _lines.Clear();
        _transcription = Task.CompletedTask;

        // Gain and target volume come from the same microphone profile as voice commands.
        var profile = MicrophoneProcessingProfileResolver.Resolve(options);
        _microphone = new MicrophonePcmStream(device, 100, profile.InputGainCap, profile.TargetPeakLevel);
        var computerAudio = StartComputerAudio();
        _microphone.StartCapture();
        _pump = Task.Factory.StartNew(() => Pump(computerAudio), TaskCreationOptions.LongRunning);
    }

    // Stops recording, waits for the last piece and any pending transcription, converts to MP3, and returns the transcript lines, the length and the audio path.
    public async Task<LectureRecording> StopAsync(CancellationToken cancellationToken)
    {
        _microphone!.StopCapture();

        try
        {
            await _pump; // queues the last piece for transcription
        }
        catch (Exception)
        {
            // Writing failed partway (a full disk): what was written is still a playable recording.
        }

        _computerAudio?.StopRecording();
        _computerAudio?.Dispose();
        _computerAudio = null;
        _microphone.Dispose();
        _microphone = null;
        _wav!.Dispose();
        _wav = null;

        // MP3 conversion runs while the last transcription finishes.
        var saving = Task.Run(() => SaveAsMp3(_wavPath));
        await _transcription;
        var audioPath = await saving;

        // Lines may arrive out of order from the background tasks; sort by time.
        lock (_lines)
        {
            return new LectureRecording(
                _lines.OrderBy(line => line.At).ToList(),
                TimeSpan.FromSeconds((double)_bytesRecorded / BytesPerSecond),
                audioPath);
        }
    }

    /// <summary>
    /// The computer's own sound, converted to match the microphone, or null when there is no output device.
    /// WASAPI loopback only delivers data while something plays; the buffer reads as silence otherwise.
    /// </summary>
    private ISampleProvider? StartComputerAudio()
    {
        try
        {
            _computerAudio = new WasapiLoopbackCapture();
            // ponytail: the two devices' clocks drift apart slowly; the 5 s buffer absorbs it, with a skip if it
            // ever overflows. Resample against the microphone's clock if long recordings show glitches.
            var buffer = new BufferedWaveProvider(_computerAudio.WaveFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(5),
                DiscardOnBufferOverflow = true
            };
            _computerAudio.DataAvailable += (_, eventArgs) => buffer.AddSamples(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
            _computerAudio.StartRecording();

            var samples = buffer.ToSampleProvider();
            // Fold to mono: stereo is averaged; for surround only the front-left channel is used.
            var mono = samples.WaveFormat.Channels switch
            {
                1 => samples,
                2 => new StereoToMonoSampleProvider(samples),
                _ => new MultiplexingSampleProvider([samples], 1) // surround: the front-left channel
            };
            return new WdlResamplingSampleProvider(mono, SampleRate);
        }
        catch (Exception)
        {
            _computerAudio?.Dispose();
            _computerAudio = null;
            return null;
        }
    }

    // The recording loop: every 100 ms it takes microphone audio, adds the computer audio, writes it to the file, and collects 30 s pieces to transcribe.
    private void Pump(ISampleProvider? computerAudio)
    {
        var microphone = new byte[BytesPerSecond / 10]; // 100 ms
        // Matching buffer of float samples (-1..1) for the computer audio.
        var computer = new float[microphone.Length / 2];
        var piece = new MemoryStream();
        var pieceStart = 0L;

        while (true)
        {
            // Blocks until the microphone has 100 ms; returns 0 once recording stops.
            // "& ~1" clears the lowest bit so the byte count is even, i.e. a whole number of 2-byte samples.
            var read = _microphone!.Read(microphone, 0, microphone.Length) & ~1;

            if (read == 0)
            {
                break;
            }

            // Computer audio is read in the same amount as the microphone chunk so the two stay in step.
            computerAudio?.Read(computer, 0, read / 2);

            // Mix: convert the computer sample from float (-1..1) to a 16-bit number and add the microphone sample; clamp so the sum cannot wrap around.
            for (var index = 0; index < read / 2; index++)
            {
                var mixed = BitConverter.ToInt16(microphone, index * 2) + (computer[index] * short.MaxValue);
                var sample = (short)Math.Clamp(mixed, short.MinValue, short.MaxValue);
                microphone[index * 2] = (byte)sample;
                microphone[(index * 2) + 1] = (byte)(sample >> 8);
            }

            _wav!.Write(microphone, 0, read);
            piece.Write(microphone, 0, read);
            _bytesRecorded += read;

            // When a piece is long enough, cut it at a quiet moment, send the front part off for transcription, and keep the rest for the next piece.
            if (piece.Length >= PieceBytes)
            {
                var audio = piece.ToArray();
                var cut = QuietestPoint(audio);
                Transcribe(audio[..cut], pieceStart);
                pieceStart += cut;
                piece.SetLength(0);
                piece.Write(audio, cut, audio.Length - cut);
            }
        }

        // At the end, transcribe what is left if it is longer than half a second.
        if (piece.Length > BytesPerSecond / 2)
        {
            Transcribe(piece.ToArray(), pieceStart);
        }
    }

    /// Middle of the quietest 50 ms frame in the last 5 seconds of the piece (smallest sum of squared samples), so no word is split between two transcriptions.
    private static int QuietestPoint(byte[] audio)
    {
        const int frame = BytesPerSecond / 20; // 50 ms
        var best = audio.Length;
        var bestEnergy = double.MaxValue;

        for (var start = Math.Max(0, audio.Length - (5 * BytesPerSecond)); start + frame <= audio.Length; start += frame)
        {
            var energy = 0.0;

            for (var index = start; index < start + frame; index += 2)
            {
                double sample = BitConverter.ToInt16(audio, index);
                energy += sample * sample;
            }

            if (energy < bestEnergy)
            {
                bestEnergy = energy;
                best = start + (frame / 2);
            }
        }

        return best & ~1;
    }

    /// Queued after the previous piece so pieces are transcribed one at a time, in order; offset is where the piece starts in the recording.
    private void Transcribe(byte[] audio, long startByte)
    {
        var offset = TimeSpan.FromSeconds((double)startByte / BytesPerSecond);
        _transcription = _transcription.ContinueWith(_ => TranscribeAsync(audio, offset), TaskScheduler.Default).Unwrap();
    }

    // Transcribes one piece with up to 3 attempts (waiting 2 s, then 4 s between tries). If it still fails, a placeholder line is added.
    // Each line's time is shifted by the piece's start offset.
    private async Task TranscribeAsync(byte[] audio, TimeSpan offset)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var lines = await _whisper.TranscribeLinesAsync(audio, SampleRate, CancellationToken.None);

                lock (_lines)
                {
                    _lines.AddRange(lines.Select(line => line with { At = offset + line.At }));
                }

                return;
            }
            catch (Exception) when (attempt < 3)
            {
                // The speech server may be busy with a voice command or restarting.
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
            }
            catch (Exception)
            {
                lock (_lines)
                {
                    _lines.Add(new TranscriptLine(offset, "(This part couldn't be transcribed. It's still in the recording.)"));
                }

                return;
            }
        }
    }

    /// <summary>
    /// 48 kbps is the MP3 encoder's highest rate for 16 kHz mono: about 21 MB an hour, against 115 MB as WAV.
    /// If conversion fails, the WAV is kept, so the recording is never lost.
    /// </summary>
    private static string SaveAsMp3(string wavPath)
    {
        var mp3Path = Path.ChangeExtension(wavPath, ".mp3");

        try
        {
            MediaFoundationApi.Startup();

            using (var reader = new WaveFileReader(wavPath))
            {
                MediaFoundationEncoder.EncodeToMp3(reader, mp3Path, 48_000);
            }

            File.Delete(wavPath);
            return mp3Path;
        }
        catch (Exception)
        {
            return wavPath;
        }
    }
}
