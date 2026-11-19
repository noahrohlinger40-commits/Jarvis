// OpenAiCompatibleStreamingSpeaker.cs: Jarvis's preferred voice. Sends the reply text to an OpenAI-style /audio/speech
// endpoint (the local Kokoro server on 127.0.0.1:8178) and plays the audio as it arrives, so speech starts before the whole
// sentence is rendered. Created by SpeakerFactory (Speech.cs). Falls back to the Windows voice (WindowsSpeechSpeaker) if the server fails.
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jarvis.Core;
using NAudio.Wave;

namespace Jarvis.App;

// Speaker that streams audio from a text-to-speech HTTP server through the sound card (NAudio).
internal sealed class OpenAiCompatibleStreamingSpeaker : ISpeaker
{
    // JSON settings for the request: camelCase names, and empty (null) fields are left out.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // Protects _activeSpeechCts, which is touched by the speaking task and by Interrupt.
    private readonly object _sync = new();
    // One reply at a time.
    private readonly SemaphoreSlim _gate = new(1, 1);
    // No timeout on the HTTP client: a long reply can stream for minutes; cancellation (interrupt) ends it instead.
    private readonly HttpClient _httpClient;
    private readonly JarvisOptions _options;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly string _voice;
    private readonly string _apiKey;
    // Windows voice used if the speech server cannot be reached.
    private readonly WindowsSpeechSpeaker _fallbackSpeaker;
    // Lets Interrupt stop the reply currently playing.
    private CancellationTokenSource? _activeSpeechCts;

    private OpenAiCompatibleStreamingSpeaker(
        JarvisOptions options,
        string endpoint,
        string model,
        string voice,
        string apiKey)
    {
        _options = options;
        _endpoint = endpoint;
        _model = model;
        _voice = voice;
        _apiKey = apiKey;
        _fallbackSpeaker = new WindowsSpeechSpeaker(options);
        _httpClient = LocalAiPolicy.CreateHttpClient(options.LocalAiOnly, Timeout.InfiniteTimeSpan);
    }

    public event EventHandler<SpeakerStatusChangedEventArgs>? StatusChanged;

    public SpeakerStatusSnapshot CurrentStatus { get; private set; } = new(false, string.Empty, DateTimeOffset.UtcNow);

    // Builds the speaker if settings allow it; returns false (so the Windows voice is used) when no model is set, the URL is invalid,
    // local-only mode would send speech to a remote host, or a remote host has no API key.
    public static bool TryCreate(JarvisOptions options, out ISpeaker? speaker)
    {
        speaker = null;

        var model = options.VoiceModel?.Trim();

        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        var baseUrl = ResolveVoiceBaseUrl(options);

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            return false;
        }

        if (options.LocalAiOnly && !LocalAiPolicy.IsLoopbackHttpEndpoint(baseUrl)) return false;

        var apiKey = ResolveVoiceApiKey(options);

        if (string.IsNullOrWhiteSpace(apiKey)
            && !string.Equals(baseUri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(baseUri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Default voice name when none is configured.
        var voice = string.IsNullOrWhiteSpace(options.VoiceName)
            ? "coral"
            : options.VoiceName.Trim();

        speaker = new OpenAiCompatibleStreamingSpeaker(
            options,
            BuildEndpoint(baseUrl),
            model,
            voice,
            apiKey);
        return true;
    }

    // Requests the audio, then plays the raw PCM as a live stream. Server errors switch to the Windows voice.
    public async Task SpeakAsync(string message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var normalized = message.Trim();

        await _gate.WaitAsync(cancellationToken);

        using var interruptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            lock (_sync)
            {
                _activeSpeechCts = interruptCts;
            }

            PublishStatus(true, normalized);

            using var request = BuildRequest(normalized);
            // ResponseHeadersRead returns as soon as the headers arrive, so audio can be played while the body is still downloading.
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                interruptCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(interruptCts.Token);
                throw new InvalidOperationException(
                    $"speech API HTTP {(int)response.StatusCode}: {TrimForError(body)}");
            }

            await using var audioStream = await response.Content.ReadAsStreamAsync(interruptCts.Token);

            await PlayPcmStreamAsync(audioStream, interruptCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            // The speech server is down, still loading its voice, or refused the request, and nothing
            // has played yet. Say it with the built-in Windows voice rather than staying silent.
            await _fallbackSpeaker.SpeakAsync(normalized, interruptCts.Token);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeSpeechCts, interruptCts))
                {
                    _activeSpeechCts = null;
                }
            }

            PublishStatus(false, string.Empty);
            _gate.Release();
        }
    }

    // Stops playback now, including the fallback voice.
    public void Interrupt()
    {
        CancellationTokenSource? cts;

        lock (_sync)
        {
            cts = _activeSpeechCts;
        }

        try
        {
            cts?.Cancel();
        }
        catch
        {
        }

        _fallbackSpeaker.Interrupt();
    }

    // Builds the POST request: model, voice, the text and the raw PCM format as JSON, plus a bearer key if one is set.
    private HttpRequestMessage BuildRequest(string message)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["voice"] = _voice,
            ["input"] = message,
            ["response_format"] = "pcm"
        };

        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        return request;
    }

    // Plays raw 24 kHz, 16-bit, mono audio while it downloads. Audio goes into a buffer holding up to 8 seconds that a sound-card
    // player drains; when the buffer is full the download waits instead of dropping audio.
    private async Task PlayPcmStreamAsync(Stream audioStream, CancellationToken cancellationToken)
    {
        // 24000 samples per second, 16 bits, one channel: the format the speech server sends in "pcm" mode.
        var waveFormat = new WaveFormat(24000, 16, 1);
        var provider = new BufferedWaveProvider(waveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferLength = waveFormat.AverageBytesPerSecond * 8
        };

        using var output = new WaveOutEvent();
        output.Volume = 1.0f;
        output.Init(provider);
        output.Play();

        var buffer = new byte[8192];
        var outputGain = ResolveOutputGain();
        // A network read can end halfway through a 16-bit sample. That byte is held back and put in
        // front of the next read, or every later sample would be scaled with its bytes misaligned.
        var carriedByte = 0;

        try
        {
            while (true)
            {
                var bytesRead = await audioStream.ReadAsync(buffer.AsMemory(carriedByte, buffer.Length - carriedByte), cancellationToken);

                if (bytesRead <= 0)
                {
                    break;
                }

                var total = carriedByte + bytesRead;
                var whole = total & ~1;

                if (Math.Abs(outputGain - 1.0) > 0.001)
                {
                    MicrophonePcmStream.ScalePcm16Mono(buffer, whole, outputGain);
                }

                // A local voice renders faster than real time; wait for room rather than drop the tail.
                // Wait for free space in the buffer.
                while (provider.BufferedBytes + whole > provider.BufferLength)
                {
                    await Task.Delay(30, cancellationToken);
                }

                provider.AddSamples(buffer, 0, whole);
                carriedByte = total - whole;

                if (carriedByte == 1)
                {
                    buffer[0] = buffer[total - 1];
                }
            }

            // After the last data arrives, wait until the buffer has actually finished playing.
            while (provider.BufferedBytes > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(30, cancellationToken);
            }
        }
        finally
        {
            provider.ClearBuffer();
            output.Stop();
        }
    }

    // Final volume multiplier: the speaker volume setting limited to 0.10..2.0, then 60% in whisper mode (quiet speaking mode), floor 0.05.
    private double ResolveOutputGain()
    {
        var gain = Math.Clamp(_options.SpeakerVolumeMultiplier, 0.10, 2.0);

        if (_options.WhisperModeEnabled)
        {
            gain *= 0.60;
        }

        return Math.Clamp(gain, 0.05, 2.0);
    }

    // Records and broadcasts the new speaking state.
    private void PublishStatus(bool isSpeaking, string message)
    {
        CurrentStatus = new SpeakerStatusSnapshot(isSpeaking, message, DateTimeOffset.UtcNow);
        StatusChanged?.Invoke(this, new SpeakerStatusChangedEventArgs(CurrentStatus));
    }

    // Accepts a base URL or a full /audio/speech URL.
    private static string BuildEndpoint(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');

        if (trimmed.EndsWith("/audio/speech", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return $"{trimmed}/audio/speech";
    }

    // The speech server defaults to the same one used for transcription when no separate voice URL is set.
    private static string ResolveVoiceBaseUrl(JarvisOptions options)
    {
        return string.IsNullOrWhiteSpace(options.VoiceBaseUrl)
            ? options.SpeechRecognitionBaseUrl
            : options.VoiceBaseUrl;
    }

    // Uses the voice-specific API key, or falls back to the transcription key.
    private static string ResolveVoiceApiKey(JarvisOptions options)
    {
        var voiceApiKey = ApiKeyResolver.Resolve(
            options.VoiceApiKey,
            options.VoiceApiKeyCredentialTarget,
            options.VoiceApiKeyEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(voiceApiKey))
        {
            return voiceApiKey;
        }

        return ApiKeyResolver.Resolve(
            options.SpeechRecognitionApiKey,
            options.SpeechRecognitionApiKeyCredentialTarget,
            options.SpeechRecognitionApiKeyEnvironmentVariable);
    }

    // Shortens error bodies to 500 characters.
    private static string TrimForError(string text)
    {
        const int maxLength = 500;
        var value = text.Trim();
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }
}
