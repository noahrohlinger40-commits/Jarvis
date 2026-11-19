// MicrophonePcmStream.cs: a "microphone as a Stream" adapter used by LectureRecorder. NAudio records a chosen microphone and queues
// the chunks; Stream.Read blocks until audio arrives. Also boosts quiet microphones (automatic gain) and reports a level for the meter.
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Jarvis.App;

/// <summary>
/// Captures 16 kHz mono PCM from a specific input device, applies a gentle automatic gain boost,
/// and exposes the audio as a blocking <see cref="Stream"/>.
/// </summary>
internal sealed class MicrophonePcmStream : Stream
{
    // 16 kHz, the standard sample rate for speech recognition.
    public const int SampleRateHz = 16_000;

    // Cap on queued chunks (about 256 x buffer length of audio); beyond this new audio is dropped rather than blocking.
    private const int MaxQueuedChunks = 256;

    // Conversational speech sits around 0.05-0.10 RMS; this maps it to roughly 0.25-0.5 for the meter.
    private const double LevelDisplayScale = 5.0;

    // Thread-safe queue between the recording thread (producer) and the reader's Read call (consumer).
    private readonly BlockingCollection<byte[]> _chunks = new(MaxQueuedChunks);
    private readonly WaveInEvent _waveIn;
    // Most the automatic gain may multiply the volume by, and the peak level (0..1) it aims for.
    private readonly double _inputGainCap;
    private readonly double _targetPeakLevel;
    // Current gain, smoothed so volume does not jump between chunks.
    private double _smoothedGain = 1.0;
    // The chunk currently being handed out to Read, and how far into it we are.
    private byte[] _current = [];
    private int _currentOffset;
    // Bytes returned to the reader so far; reported as the stream Position.
    private long _totalBytesRead;
    private bool _captureStopped;
    private bool _disposed;

    // Opens the given input device. Buffer length is limited to 20-200 ms.
    public MicrophonePcmStream(int deviceNumber, int bufferMilliseconds, double inputGainCap, double targetPeakLevel)
    {
        _inputGainCap = Math.Max(1.0, inputGainCap);
        _targetPeakLevel = Math.Clamp(targetPeakLevel, 0.1, 0.9);
        DeviceName = WaveInEvent.GetCapabilities(deviceNumber).ProductName;

        _waveIn = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            BufferMilliseconds = Math.Clamp(bufferMilliseconds, 20, 200),
            WaveFormat = new WaveFormat(SampleRateHz, 16, 1)
        };
        _waveIn.DataAvailable += OnDataAvailable;
    }

    public string DeviceName { get; }

    /// <summary>
    /// Raised for every captured buffer with a 0-1 level (scaled RMS after gain).
    /// </summary>
    public event Action<double>? LevelMeasured;

    // Starts recording from the microphone.
    public void StartCapture()
    {
        _waveIn.StartRecording();
    }

    /// <summary>
    /// Stops the microphone and signals end-of-stream so a blocked reader wakes up and returns 0.
    /// </summary>
    public void StopCapture()
    {
        if (_captureStopped)
        {
            return;
        }

        _captureStopped = true;

        try
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.StopRecording();
        }
        catch
        {
        }

        try
        {
            _chunks.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // Called by NAudio per chunk: copies it (NAudio reuses its buffer), boosts it and queues it.
    private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        if (eventArgs.BytesRecorded < 2)
        {
            return;
        }

        var chunk = new byte[eventArgs.BytesRecorded - (eventArgs.BytesRecorded % 2)];
        Buffer.BlockCopy(eventArgs.Buffer, 0, chunk, 0, chunk.Length);
        // The chunk length is rounded down to a whole number of 2-byte samples so none is split.
        ApplyAutomaticGain(chunk);

        try
        {
            // Drop the chunk rather than block the capture thread if the reader falls far behind.
            _chunks.TryAdd(chunk);
        }
        catch (InvalidOperationException)
        {
            // Also covers ObjectDisposedException: capture was stopped while a buffer was in flight.
        }
    }

    // Measures peak and RMS (average loudness), then scales the samples so the loudest reaches the target peak, within limits.
    private void ApplyAutomaticGain(byte[] pcm16Mono)
    {
        var peak = 0.0;
        var sumSquares = 0.0;
        var sampleCount = pcm16Mono.Length / 2;

        for (var index = 0; index < pcm16Mono.Length; index += 2)
        {
            var sample = BitConverter.ToInt16(pcm16Mono, index) / 32768.0;
            var magnitude = Math.Abs(sample);
            peak = Math.Max(peak, magnitude);
            sumSquares += sample * sample;
        }

        // RMS: square each sample, average, square root. 0 is silence and 1 is full scale.
        var rms = Math.Sqrt(sumSquares / Math.Max(1, sampleCount));
        double targetGain;

        if (peak < 0.004 && rms < 0.0008)
        {
            // True silence: do not amplify the noise floor.
            targetGain = 1.0;
        }
        else
        {
            // Gain = target peak / actual peak, at least 1 (never turn quiet audio down) and at most the cap. The peak is floored at 0.025
            // so near-silence is not multiplied by a huge number.
            targetGain = Math.Clamp(_targetPeakLevel / Math.Max(peak, 0.025), 1.0, _inputGainCap);
        }

        // Smoothing: 76% previous gain, 24% new, so the volume changes gradually.
        _smoothedGain = (_smoothedGain * 0.76) + (targetGain * 0.24);

        // Gains up to 1.02 count as 1 (no change, saves work).
        var appliedGain = _smoothedGain <= 1.02 ? 1.0 : _smoothedGain;
        LevelMeasured?.Invoke(Math.Clamp(rms * appliedGain * LevelDisplayScale, 0, 1));

        if (appliedGain == 1.0)
        {
            return;
        }

        ScalePcm16Mono(pcm16Mono, pcm16Mono.Length, _smoothedGain);
    }

    // Multiplies every 16-bit sample in the first byteCount bytes by the gain and clamps to the 16-bit range so it cannot wrap around into noise.
    internal static void ScalePcm16Mono(byte[] pcmBuffer, int byteCount, double gain)
    {
        var samples = MemoryMarshal.Cast<byte, short>(pcmBuffer.AsSpan(0, Math.Max(0, byteCount)));

        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = (short)Math.Clamp((int)Math.Round(samples[index] * gain), short.MinValue, short.MaxValue);
        }
    }

    // Fills the whole requested buffer, waiting for more microphone audio if needed.
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        // A short read looks like end-of-stream to callers, so block until the whole request is
        // filled. Only a stopped capture (queue completed and drained) may return fewer bytes.
        var totalCopied = 0;

        while (totalCopied < count)
        {
            if (_currentOffset >= _current.Length)
            {
                byte[]? next;

                try
                {
                    if (!_chunks.TryTake(out next, Timeout.Infinite))
                    {
                        break;
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                _current = next;
                _currentOffset = 0;
            }

            var bytesToCopy = Math.Min(count - totalCopied, _current.Length - _currentOffset);
            Buffer.BlockCopy(_current, _currentOffset, buffer, offset + totalCopied, bytesToCopy);
            _currentOffset += bytesToCopy;
            totalCopied += bytesToCopy;
        }

        _totalBytesRead += totalCopied;
        return totalCopied;
    }

    // Stops the microphone and frees the queue.
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            StopCapture();
            _waveIn.Dispose();
            _chunks.Dispose();
        }

        base.Dispose(disposing);
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    // A live microphone has no fixed length.
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _totalBytesRead;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
