// AppBootstrapper.cs: startup wiring, called once from Program.cs before any window exists.
// CreateAsync loads settings, makes sure the local model server is up (Jarvis's own llama-server),
// starts the local speech server, and builds the JarvisAssistant with all of its services.
// Everything here is "best effort" where it can be: speech server failures are logged, not fatal.
// Only a planner engine that cannot start or never becomes ready aborts startup.
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jarvis.Core;

namespace Jarvis.App;

internal static class AppBootstrapper
{
    // Used for the warm-up request body and model lists; case-insensitive because servers differ.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    // Builds the whole app context in order: settings, speech server, model server, desktop service, assistant, speaker, voice.
    public static async Task<JarvisDesktopContext> CreateAsync(CancellationToken cancellationToken)
    {
        var workspaceRoot = ResolveWorkspaceRoot();
        var options = await JarvisSettingsLoader.LoadAsync(workspaceRoot, cancellationToken);
        // Started first and not awaited, so the speech model loads while the planner warms up.
        StartLocalSpeechServerInBackground(workspaceRoot, options);
        await EnsureLocalPlannerReadyAsync(options, cancellationToken);
        var desktopService = new DesktopAutomationService(workspaceRoot, options.ApplicationAliases, options.SiteAliases, options.FileSearchRoots);
        // Scan installed apps in the background so the first "open" command is instant.
        desktopService.WarmApplicationIndexInBackground();
        JarvisCommandCatalog.KnownApplicationNameCheck = desktopService.IsKnownApplicationName;
        IDesktopAutomationService desktopAutomation = desktopService;
        var assistant = await CreateAssistantAsync(workspaceRoot, options, desktopAutomation, cancellationToken);
        ISpeaker speaker = SpeakerFactory.Create(options);
        IVoiceRuntime voiceRuntime = VoiceRuntimeFactory.Create(options);

        return new JarvisDesktopContext(workspaceRoot, options, desktopAutomation, assistant, speaker, voiceRuntime);
    }

    // Constructs the JarvisAssistant and every service it depends on. Public because the main form calls it again
    // when the user changes settings and the assistant has to be rebuilt.
    public static async Task<JarvisAssistant> CreateAssistantAsync(
        string workspaceRoot,
        JarvisOptions options,
        IDesktopAutomationService desktopAutomation,
        CancellationToken cancellationToken)
    {
        var memoryStore = new JsonMemoryStore(JarvisDataPaths.Resolve(options.MemoryFilePath));
        IWeatherService weatherService = new OpenMeteoWeatherService();
        IWebResearchService webResearchService = new DuckDuckGoWebResearchService();
        var transcriptStore = new JsonLineTranscriptStore(JarvisDataPaths.Resolve(options.TranscriptFilePath));
        IAgentStateStore agentStateStore = new JsonAgentStateStore(JarvisDataPaths.Resolve(options.AgentStateFilePath));
        IPendingApprovalStore pendingApprovalStore = new JsonPendingApprovalStore(JarvisDataPaths.Resolve(options.PendingApprovalFilePath));
        var modelGateway = ModelGatewayFactory.Create(workspaceRoot, options);
        IVisualContextService visualContextService = new WindowsVisualContextService(workspaceRoot, options, modelGateway);
        var tutor = new TutorService(modelGateway, new WindowsTutorHost(), JarvisDataPaths.Resolve("data/automation/lessons.json"));
        var planner = AssistantPlannerFactory.Create(options, modelGateway, agentStateStore, webResearchService, ModelGatewayFactory.CreateEscalation(workspaceRoot, options));
        var toolSafety = new ToolSafetyService(options);
        var assistant = new JarvisAssistant(
            options,
            memoryStore,
            desktopAutomation,
            weatherService,
            webResearchService,
            visualContextService,
            transcriptStore,
            agentStateStore,
            planner,
            pendingApprovalStore,
            toolSafety,
            workspaceRoot,
            modelGateway,
            new UiaBrowserPageReader(),
            new LectureRecorder(options),
            tutor);
        await assistant.InitializeAsync(cancellationToken);
        return assistant;
    }

    // Finds the folder holding jarvis.settings.json by walking up from the exe folder (bin/Release/...) to the project root.
    // Falls back to the current directory if no settings file exists anywhere above.
    private static string ResolveWorkspaceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            var settingsPath = Path.Combine(current.FullName, "jarvis.settings.json");

            if (File.Exists(settingsPath))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    // Makes sure the local language model server answers and the planner model is loaded and warm, or throws.
    // Skipped entirely when the planner is disabled, is a cloud service, or local AI use is not allowed.
    private static async Task EnsureLocalPlannerReadyAsync(JarvisOptions options, CancellationToken cancellationToken)
    {
        if (!options.PlannerEnabled || !LooksLikeLocalPlanner(options)
            || !LocalAiPolicy.AllowsModelProvider(options, options.PlannerProvider, options.PlannerBaseUrl))
        {
            return;
        }

        var model = ResolvePlannerWarmupModel(options);

        if (string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        // If something already serves on the planner port, leave it alone; otherwise try Jarvis's own engine first.
        var startedOwnEngine = !await IsLocalApiReachableAsync(options.PlannerBaseUrl, cancellationToken)
            && TryStartOwnLlmEngine(options, model);

        // Nothing answers on the planner port and Jarvis's own engine could not start (the reason is in llm-server.log): stop now with a clear message.
        if (!startedOwnEngine && !await IsLocalApiReachableAsync(options.PlannerBaseUrl, cancellationToken))
        {
            throw new InvalidOperationException(
                "The local planner is unavailable: Jarvis's own llama-server did not start. Check %LOCALAPPDATA%\\Jarvis\\data\\llm-server.log or run scripts\\Setup-LocalLlm.ps1 once, then launch Jarvis again.");
        }

        // Poll the models endpoint every 0.5 s until the planner model is listed or the deadline passes.
        // The deadline is 90 s when Jarvis just launched its own engine (loading a model is slow), otherwise 30 s.
        using var httpClient = LocalAiPolicy.CreateHttpClient(options.LocalAiOnly, Timeout.InfiniteTimeSpan);

        var plannerApiKey = ApiKeyResolver.Resolve(
            options.PlannerApiKey,
            options.PlannerApiKeyCredentialTarget,
            options.PlannerApiKeyEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(plannerApiKey))
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", plannerApiKey);
        }

        var modelsEndpoint = BuildLocalPlannerEndpoint(options.PlannerBaseUrl, "models");
        var chatEndpoint = BuildLocalPlannerEndpoint(options.PlannerBaseUrl, "chat/completions");
        // Overall deadline for the model to appear; each single request is limited to 12 s inside the loop.
        var deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(startedOwnEngine ? 90 : 30);
        var modelFound = false;

        while (DateTimeOffset.UtcNow < deadlineUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // llama-server exits at once when the model will not load or the port is already taken.
            // Give whatever holds the port a short chance (15 s) before giving up.
            if (startedOwnEngine && _ownLlmProcess is { HasExited: true })
            {
                startedOwnEngine = false;
                deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(15);
            }

            try
            {
                using var modelsTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                modelsTimeout.CancelAfter(TimeSpan.FromSeconds(12));
                var modelsPayload = await httpClient.GetStringAsync(modelsEndpoint, modelsTimeout.Token);

                if (ModelExists(modelsPayload, model))
                {
                    modelFound = true;
                    break;
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Includes per-request timeouts: keep retrying until the overall deadline instead of aborting startup.
            }

            await Task.Delay(500, cancellationToken);
        }

        // The model never showed up: stop startup with a message telling Noah where to look.
        if (!modelFound)
        {
            throw new InvalidOperationException(
                $"The local planner model `{model}` is not ready at {modelsEndpoint}. Check %LOCALAPPDATA%\\Jarvis\\data\\llm-server.log or run scripts\\Setup-LocalLlm.ps1 once, then launch Jarvis again.");
        }

        // Warm-up: one tiny chat request so the model is loaded into memory now, not on the first real command.
        // The 120 s limit allows for a large model loading from disk.
        var warmupPayload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "system",
                    ["content"] = "Answer immediately and briefly."
                },
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = "Reply with exactly: ok"
                }
            },
            ["temperature"] = 0,
            ["max_tokens"] = 96
        };

        using var warmupRequest = new HttpRequestMessage(HttpMethod.Post, chatEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(warmupPayload, JsonOptions), Encoding.UTF8, "application/json")
        };

        using var warmupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        warmupTimeout.CancelAfter(TimeSpan.FromSeconds(120));
        using var warmupResponse = await httpClient.SendAsync(warmupRequest, warmupTimeout.Token);
        var warmupBody = await warmupResponse.Content.ReadAsStringAsync(warmupTimeout.Token);

        if (!warmupResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"The local planner warm-up request failed at {chatEndpoint}: HTTP {(int)warmupResponse.StatusCode}. {TrimStartupError(warmupBody)}");
        }
    }

    // The llama-server process Jarvis launched itself, if any; used to notice it crashed and to kill it on exit.
    private static Process? _ownLlmProcess;
    // stdout and stderr lines arrive on separate threads; appends to one file must not overlap.
    private static readonly object OwnLlmLogLock = new();

    /// <summary>
    /// Starts Jarvis's own llama-server when scripts\Setup-LocalLlm.ps1 has installed it.
    /// Returns false (and logs why) when it is not installed or cannot start.
    /// </summary>
    private static bool TryStartOwnLlmEngine(JarvisOptions options, string model)
    {
        var llmHome = Path.Combine(JarvisDataPaths.Home, "llm");
        var binDirectory = Path.Combine(llmHome, "bin");
        var serverPath = Path.Combine(binDirectory, "llama-server.exe");
        var modelPath = Path.Combine(llmHome, "planner.gguf");
        var logPath = JarvisDataPaths.Resolve("data/llm-server.log");

        if (!File.Exists(serverPath) || !File.Exists(modelPath))
        {
            AppendSpeechServerLog(logPath, $"Jarvis's own llama-server is not installed (needs {serverPath} and {modelPath}). Run scripts\\Setup-LocalLlm.ps1.");
            return false;
        }

        try
        {
            var plannerUri = new Uri(BuildLocalPlannerEndpoint(options.PlannerBaseUrl, string.Empty).TrimEnd('/'));
            var startInfo = new ProcessStartInfo
            {
                FileName = serverPath,
                WorkingDirectory = binDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            // Command-line flags for llama-server: the model file, the name to answer to, host and port taken from the planner URL,
            // context size (at least 4096), all layers on the GPU (-ngl 99), one request at a time (-np 1).
            foreach (var argument in new[]
            {
                "-m", modelPath, "--alias", model, "--host", plannerUri.Host, "--port", plannerUri.Port.ToString(),
                "-c", Math.Max(4096, options.PlannerLocalContextLength).ToString(), "-ngl", "99", "-np", "1",
                // q8 KV cache halves context memory on the 4 GB GPU at no measurable quality cost (16k fits where f16 spilled).
                "-fa", "on", "-ctk", "q8_0", "-ctv", "q8_0",
                "-t", Math.Clamp(options.PlannerLocalCpuThreads, 1, Environment.ProcessorCount).ToString(),
                "-tb", Math.Clamp(options.PlannerLocalCpuThreads, 1, Environment.ProcessorCount).ToString(), "--jinja", "--no-webui"
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            var process = Process.Start(startInfo);

            if (process is null)
            {
                return false;
            }

            DataReceivedEventHandler append = (_, e) =>
            {
                if (e.Data is not null)
                {
                    lock (OwnLlmLogLock)
                    {
                        AppendSpeechServerLog(logPath, e.Data);
                    }
                }
            };
            process.OutputDataReceived += append;
            process.ErrorDataReceived += append;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            _ownLlmProcess = process;
            // ponytail: killed on normal exit only; a crash leaves it running and the next launch reuses it (reachability check). Use a Job Object if orphans become a problem.
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try
                {
                    _ownLlmProcess?.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Already gone.
                }
            };

            AppendSpeechServerLog(logPath, $"Started Jarvis's own llama-server as `{model}` on {plannerUri.Host}:{plannerUri.Port}.");
            return true;
        }
        catch (Exception exception)
        {
            AppendSpeechServerLog(logPath, $"Could not start Jarvis's own llama-server: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Launches the bundled local Whisper server when speech recognition points at localhost and it
    /// is not already running. The server exits by itself when Jarvis exits. Does nothing if the
    /// local speech environment has not been set up (see scripts\Setup-LocalSpeech.ps1).
    /// </summary>
    private static void StartLocalSpeechServerInBackground(string workspaceRoot, JarvisOptions options)
    {
        // Only run when speech recognition is on, the address is this machine, and the provider is one the local server handles.
        if (!options.SpeechRecognitionEnabled
            || !Uri.TryCreate(options.SpeechRecognitionBaseUrl, UriKind.Absolute, out var speechUri)
            || !speechUri.IsLoopback
            || !SpeechProviders.IsOpenAiCompatible(options.SpeechRecognitionProvider))
        {
            return;
        }

        // Lives outside the project on purpose: the project is often inside OneDrive, which corrupts
        // large model files and Python environments.
        var speechHome = Environment.GetEnvironmentVariable("JARVIS_SPEECH_HOME");

        if (string.IsNullOrWhiteSpace(speechHome))
        {
            speechHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Jarvis",
                "whisper");
        }

        var pythonPath = Path.Combine(speechHome, "venv", "Scripts", "python.exe");
        var serverScriptPath = Path.Combine(workspaceRoot, "scripts", "whisper-server.py");
        var logPath = JarvisDataPaths.Resolve("data/whisper-server.log");

        if (!File.Exists(pythonPath) || !File.Exists(serverScriptPath))
        {
            AppendSpeechServerLog(
                logPath,
                $"Local speech is not set up (looked for {pythonPath}). Run scripts\\Setup-LocalSpeech.ps1.");
            return;
        }

        // Off the startup path: probing a closed localhost port can take a couple of seconds on Windows.
        _ = Task.Run(async () =>
        {
            try
            {
                if (await IsLocalApiReachableAsync(options.SpeechRecognitionBaseUrl, CancellationToken.None))
                {
                    return;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = pythonPath,
                    WorkingDirectory = workspaceRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add(serverScriptPath);
                startInfo.Environment["JARVIS_WHISPER_HOST"] = speechUri.Host;
                startInfo.Environment["JARVIS_WHISPER_PORT"] = speechUri.Port.ToString();
                startInfo.Environment["JARVIS_WHISPER_LOG"] = logPath;
                startInfo.Environment["JARVIS_WHISPER_MODELS"] = Path.Combine(speechHome, "models");
                startInfo.Environment["JARVIS_PARENT_PID"] = Environment.ProcessId.ToString();

                if (!string.IsNullOrWhiteSpace(options.SpeechRecognitionModel))
                {
                    startInfo.Environment["JARVIS_WHISPER_MODEL"] = options.SpeechRecognitionModel.Trim();
                }

                // The server keeps running on its own; only the local handle is released here.
                using var serverProcess = Process.Start(startInfo);
            }
            catch (Exception exception)
            {
                AppendSpeechServerLog(logPath, $"Could not start the local speech server: {exception.Message}");
            }
        });
    }

    // Appends one timestamped line to a log file.
    private static void AppendSpeechServerLog(string logPath, string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, $"[{DateTimeOffset.Now:u}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging is best effort.
        }
    }

    // Quick check whether anything answers HTTP at this address.
    private static async Task<bool> IsLocalApiReachableAsync(string baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            // A server's first answer after idling can take a few seconds; a closed port still refuses instantly.
            using var probeClient = LocalAiPolicy.CreateHttpClient(true, TimeSpan.FromSeconds(10));

            // Any HTTP response, even an auth error, proves the server is up.
            using var response = await probeClient.GetAsync(
                BuildLocalPlannerEndpoint(baseUrl, "models"),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            return true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    // True when the planner is the "local" provider or its URL points at localhost / 127.0.0.1.
    private static bool LooksLikeLocalPlanner(JarvisOptions options)
    {
        if (string.Equals(options.PlannerProvider, "local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!Uri.TryCreate(options.PlannerBaseUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase);
    }

    // Picks the model used for warm-up: the fast model if set, else the reasoning model, else the main planner model.
    private static string ResolvePlannerWarmupModel(JarvisOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.PlannerFastModel))
        {
            return options.PlannerFastModel.Trim();
        }

        if (!string.IsNullOrWhiteSpace(options.PlannerReasoningModel))
        {
            return options.PlannerReasoningModel.Trim();
        }

        return options.PlannerModel.Trim();
    }

    // Builds a URL like http://host:port/v1/models; adds the "/v1" part when the configured address has no path.
    private static string BuildLocalPlannerEndpoint(string baseUrl, string relativePath)
    {
        var trimmedBaseUrl = baseUrl.Trim().TrimEnd('/');

        if (!Uri.TryCreate(trimmedBaseUrl, UriKind.Absolute, out var uri))
        {
            return $"{trimmedBaseUrl}/{relativePath}";
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        var normalizedBaseUrl = string.IsNullOrWhiteSpace(path)
            ? $"{trimmedBaseUrl}/v1"
            : trimmedBaseUrl;

        return $"{normalizedBaseUrl}/{relativePath}";
    }

    // Checks the /models response for the wanted model id.
    private static bool ModelExists(string modelsPayload, string model)
    {
        try
        {
            using var document = JsonDocument.Parse(modelsPayload);

            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idElement)
                    || idElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (string.Equals(idElement.GetString(), model, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    // Shortens long process output so error messages stay readable.
    private static string TrimStartupError(string text)
    {
        const int maxLength = 240;
        var value = text.Trim();
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }
}
