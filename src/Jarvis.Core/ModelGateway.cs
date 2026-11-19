// ModelGateway.cs: how Jarvis talks to its language model. Every "ask the AI" call in the app (the planner,
// vision, notes, the Brain tool) goes through IModelGateway.CompleteAsync, which turns a request into an
// OpenAI-style HTTP call: POST {PlannerBaseUrl}/v1/chat/completions. In practice that server is LM Studio on
// this PC (a localhost URL), but any OpenAI-compatible server works. The URL, model names and API key come from
// JarvisOptions (the Planner* settings in jarvis.settings.json).
// Model routes: the caller picks a ModelRoute (Fast, Reasoning, Vision or Deep). Each route maps to a configured
// model name, with a fallback chain (see GetRouteModels) so a missing model falls back to another one.
// Queueing: ModelGatewayFactory.Create wraps the real gateway in ScheduledModelGateway (ModelWorkScheduler.cs),
// so only a few model calls run at once and foreground (user-facing) work goes ahead of background work.
// If the planner is disabled or no allowed provider exists, DisabledModelGateway is returned instead.
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Jarvis.Core;

// Which kind of model a call wants; the gateway maps each to a model name from the settings:
// Fast = quick small model for short replies, Reasoning = the main model, Vision = reads images, Deep = slow off-turn thinking.
public enum ModelRoute
{
    Fast,
    Reasoning,
    Vision,
    /// <summary>Slow, off-turn thinking (brain think): a bigger local model when one is configured, else the reasoning model.</summary>
    Deep
}

// One piece of a chat message: either plain text (Type "text") or an image given by URL/data URL (Type "image_url").
public sealed record ChatMessageContentPart(string Type, string Text = "", string Url = "")
{
    public static ChatMessageContentPart FromText(string text) => new("text", text ?? string.Empty);

    public static ChatMessageContentPart FromImageUrl(string url) => new("image_url", string.Empty, url ?? string.Empty);
}

// One message in the conversation. Role is "system", "user", "assistant" or "tool". ToolCallId links a "tool"
// message back to the tool call it answers; ToolCalls holds tool calls the assistant itself asked for.
public sealed record ChatMessage(
    string Role,
    ChatMessageContentPart[] Content,
    string ToolCallId = "",
    ChatToolCall[]? ToolCalls = null);

// A tool the model may call. Every tool takes a single free-text "input" argument (see SerializeTool).
public sealed record ChatToolDefinition(
    string Name,
    string Description,
    string InputDescription = "Optional tool input.");

// A tool call the model asked for: an id, the tool name, and the arguments as a raw JSON string.
public sealed record ChatToolCall(string Id, string Name, string ArgumentsJson);

// Everything needed for one model call. ModelOverride forces a specific model name instead of the route's default;
// User is an optional label sent along as the OpenAI "user" field.
public sealed record ChatCompletionRequest(
    ChatMessage[] Messages,
    ChatToolDefinition[]? Tools = null,
    double Temperature = 0.2,
    string ModelOverride = "",
    string User = "");

// The model's answer: reply text, any tool calls, which model name answered, and a "profile/provider" label.
public sealed record ChatCompletionResponse(
    string Content,
    ChatToolCall[] ToolCalls,
    string Model,
    string ProviderLabel);

// The one door to the language model. Implementations: OpenAiCompatibleModelGateway (real HTTP calls),
// DisabledModelGateway (always unavailable) and ScheduledModelGateway (adds the queue around another gateway).
public interface IModelGateway
{
    bool IsAvailable { get; }

    string Status { get; }

    Task<ChatCompletionResponse> CompleteAsync(
        ChatCompletionRequest request,
        ModelRoute route,
        CancellationToken cancellationToken);
}

// Builds the right gateway from the settings. The app calls this at startup and again when settings change.
public static class ModelGatewayFactory
{
    public static IModelGateway Create(string workspaceRoot, JarvisOptions options)
    {
        if (!options.PlannerEnabled)
        {
            return new DisabledModelGateway("disabled in settings");
        }

        var profiles = BuildProfiles(options);

        // Local-only AI mode: drop any provider that is not a loopback (same-computer) server.
        profiles.RemoveAll(profile => !LocalAiPolicy.AllowsModelProvider(options, profile.Provider, profile.BaseUrl));

        if (options.LocalAiOnly && profiles.Count == 0)
            return new DisabledModelGateway("local-only AI mode requires a local provider on this computer; hosted routes are blocked");

        if (profiles.Count == 0)
        {
            return new DisabledModelGateway("no usable model provider is configured");
        }

        // One inference budget per process: the app rebuilds the gateway on settings changes while earlier requests
        // may still run. ponytail: capacity is fixed at first use, so changing ModelMaxConcurrentRequests takes effect on restart.
        // Capacity is how many model calls may run at the same moment (1 to 4). The scheduler is shared across rebuilds.
        var capacity = Math.Clamp(options.ModelMaxConcurrentRequests, 1, 4);
        var scheduler = LazyInitializer.EnsureInitialized(ref _sharedScheduler, () => new ModelWorkScheduler(capacity));
        return new ScheduledModelGateway(new OpenAiCompatibleModelGateway(workspaceRoot, options, profiles), capacity, scheduler);
    }

    // The single queue shared by every gateway built in this process.
    private static ModelWorkScheduler? _sharedScheduler;

    // Collects the usable providers. Today there is only one, the "primary" profile built from the Planner* settings.
    private static List<ResolvedProviderProfile> BuildProfiles(JarvisOptions options)
    {
        var profiles = new List<ResolvedProviderProfile>();

        var primary = BuildPrimaryProfile(options);

        if (primary is not null)
        {
            profiles.Add(primary);
        }

        return profiles;
    }

    // Turns the Planner* settings into one provider profile; returns null when no base URL is set.
    private static ResolvedProviderProfile? BuildPrimaryProfile(JarvisOptions options)
    {
        var provider = NormalizeProvider(options.PlannerProvider);

        if (string.IsNullOrWhiteSpace(options.PlannerBaseUrl))
        {
            return null;
        }

        var requestModel = ResolveRequestModel(
            provider,
            options.PlannerModel,
            options.PlannerReasoningModel);

        // If no separate reasoning model is configured, the general model is used for reasoning too.
        var reasoningModel = string.IsNullOrWhiteSpace(options.PlannerReasoningModel)
            ? requestModel
            : options.PlannerReasoningModel.Trim();

        return new ResolvedProviderProfile(
            "primary",
            provider,
            options.PlannerBaseUrl.Trim(),
            requestModel,
            options.PlannerFastModel.Trim(),
            reasoningModel,
            options.PlannerVisionModel.Trim(),
            options.PlannerDeepModel.Trim(),
            ResolveApiKey(
                options.PlannerApiKey,
                options.PlannerApiKeyCredentialTarget,
                options.PlannerApiKeyEnvironmentVariable));
    }

    // Finds the API key from the settings value, Windows Credential Manager, or an environment variable (see ApiKeyResolver).
    private static string ResolveApiKey(string directValue, string credentialTarget, string envVar) =>
        ApiKeyResolver.Resolve(directValue, credentialTarget, envVar);

    // Maps the free-text provider setting to "openai", "local" or the default "openai-compatible".
    private static string NormalizeProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return "openai-compatible";
        }

        var value = provider.Trim().ToLowerInvariant();
        return value switch
        {
            "openai" => "openai",
            "local" => "local",
            _ => "openai-compatible"
        };
    }

    // Picks the general model name: PlannerModel if set, otherwise the reasoning model, otherwise empty.
    private static string ResolveRequestModel(
        string provider,
        string configuredModel,
        string reasoningModel)
    {

        if (!string.IsNullOrWhiteSpace(configuredModel))
        {
            return configuredModel.Trim();
        }

        return string.IsNullOrWhiteSpace(reasoningModel)
            ? string.Empty
            : reasoningModel.Trim();
    }
}

// Stand-in used when AI is switched off or nothing usable is configured. Status explains why; any call throws.
internal sealed class DisabledModelGateway : IModelGateway
{
    public DisabledModelGateway(string reason)
    {
        Status = $"disabled ({reason})";
    }

    public bool IsAvailable => false;

    public string Status { get; }

    public Task<ChatCompletionResponse> CompleteAsync(
        ChatCompletionRequest request,
        ModelRoute route,
        CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Model gateway is not available.");
    }
}

// The real gateway: sends chat requests to an OpenAI-style /chat/completions endpoint and parses the reply.
internal sealed class OpenAiCompatibleModelGateway : IModelGateway
{
    // Web defaults give camelCase names; case-insensitive reading tolerates servers that differ.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _workspaceRoot;
    private readonly JarvisOptions _options;
    private readonly IReadOnlyList<ResolvedProviderProfile> _profiles;
    private readonly HttpClient _httpClient;

    public OpenAiCompatibleModelGateway(string workspaceRoot, JarvisOptions options, IReadOnlyList<ResolvedProviderProfile> profiles)
    {
        _workspaceRoot = workspaceRoot;
        _options = options;
        _profiles = profiles;
        // Timeouts are per request (see SendHttpAsync): a deep pass may legitimately run for minutes.
        _httpClient = LocalAiPolicy.CreateHttpClient(options.LocalAiOnly, Timeout.InfiniteTimeSpan);
    }

    // How long to wait for a normal answer before giving up.
    private static readonly TimeSpan DefaultModelTimeout = TimeSpan.FromSeconds(45);
    // A CPU-bound deep model produces ~9 tokens/s here and the first call also waits for LM Studio to load it.
    private static readonly TimeSpan DeepModelTimeout = TimeSpan.FromMinutes(5);

    // Deep gets the long timeout; every other route gets the normal one.
    private static TimeSpan ModelTimeout(ModelRoute route) => route == ModelRoute.Deep ? DeepModelTimeout : DefaultModelTimeout;

    public bool IsAvailable => _profiles.Count > 0;

    // A one-line description of the configured providers and their fast/reasoning models, shown in the UI.
    public string Status =>
        string.Join(
            " | ",
            _profiles.Select(profile =>
            {
                var fastModel = string.IsNullOrWhiteSpace(profile.FastModel) ? "n/a" : profile.FastModel;
                var reasoningModel = string.IsNullOrWhiteSpace(profile.ReasoningModel) ? "n/a" : profile.ReasoningModel;
                return $"{profile.Name}:{profile.Provider} fast={fastModel} reasoning={reasoningModel}";
            }));

    // The timeout cancels through a linked token; report it as a failure so callers do not mistake it for the user cancelling.
    private async Task<HttpResponseMessage> SendHttpAsync(HttpRequestMessage message, CancellationToken cancellationToken, TimeSpan timeout)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            return await _httpClient.SendAsync(message, timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The local model did not answer within {timeout.TotalSeconds:0} seconds.");
        }
    }

    // Tries each candidate model for this route in order and returns the first success. Ordinary errors (HTTP
    // failures, bad JSON) move on to the next candidate; cancellation and timeouts stop immediately.
    public async Task<ChatCompletionResponse> CompleteAsync(
        ChatCompletionRequest request,
        ModelRoute route,
        CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("No model providers are available.");
        }

        var failures = new List<string>();

        foreach (var candidate in ResolveCandidates(route, request.ModelOverride))
        {
            try
            {
                return await SendAsync(candidate, request, route, cancellationToken);
            }
            // A timeout already cost the full HTTP timeout while holding the model slot; fail now instead of waiting again.
            catch (Exception exception) when (exception is not OperationCanceledException and not TimeoutException)
            {
                failures.Add($"{candidate.Profile.Name}/{candidate.Model}: {TrimError(exception.Message)}");
            }
        }

        throw new InvalidOperationException(
            "All planner model candidates failed. " + string.Join(" | ", failures));
    }

    // Lists the (provider, model) pairs to try, without duplicates. A ModelOverride replaces the route's own list.
    private IEnumerable<ModelCandidate> ResolveCandidates(ModelRoute route, string modelOverride)
    {
        foreach (var profile in _profiles)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(modelOverride))
            {
                if (seen.Add(modelOverride.Trim()))
                {
                    yield return new ModelCandidate(profile, modelOverride.Trim());
                }

                continue;
            }

            foreach (var model in GetRouteModels(profile, route))
            {
                if (seen.Add(model))
                {
                    yield return new ModelCandidate(profile, model);
                }
            }
        }
    }

    // The model fallback chain for each route, first choice first. Fast: fast, then reasoning. Vision: vision, then reasoning.
    // Reasoning: reasoning, then fast. Deep: deep, then reasoning, then fast (Deep and Reasoning share the default branch below).
    private IEnumerable<string> GetRouteModels(ResolvedProviderProfile profile, ModelRoute route)
    {
        switch (route)
        {
            case ModelRoute.Fast:
                if (!string.IsNullOrWhiteSpace(profile.FastModel))
                {
                    yield return profile.FastModel;
                }

                if (!string.IsNullOrWhiteSpace(profile.ReasoningModel)
                    && !string.Equals(profile.ReasoningModel, profile.FastModel, StringComparison.OrdinalIgnoreCase))
                {
                    yield return profile.ReasoningModel;
                }

                break;

            case ModelRoute.Vision:
                if (!string.IsNullOrWhiteSpace(profile.VisionModel))
                {
                    yield return profile.VisionModel;
                }

                if (!string.IsNullOrWhiteSpace(profile.ReasoningModel)
                    && !string.Equals(profile.ReasoningModel, profile.VisionModel, StringComparison.OrdinalIgnoreCase))
                {
                    yield return profile.ReasoningModel;
                }

                break;

            default:
                if (route == ModelRoute.Deep && !string.IsNullOrWhiteSpace(profile.DeepModel))
                {
                    yield return profile.DeepModel;
                }

                if (!string.IsNullOrWhiteSpace(profile.ReasoningModel))
                {
                    yield return profile.ReasoningModel;
                }

                if (!string.IsNullOrWhiteSpace(profile.FastModel)
                    && !string.Equals(profile.FastModel, profile.ReasoningModel, StringComparison.OrdinalIgnoreCase))
                {
                    yield return profile.FastModel;
                }

                break;
        }
    }

    // Makes one HTTP call to one model: builds the JSON body, sends it, checks the status and parses the reply.
    private async Task<ChatCompletionResponse> SendAsync(
        ModelCandidate candidate,
        ChatCompletionRequest request,
        ModelRoute route,
        CancellationToken cancellationToken)
    {
        if (!LocalAiPolicy.AllowsModelProvider(_options, candidate.Profile.Provider, candidate.Profile.BaseUrl))
            throw new InvalidOperationException("Local-only AI mode blocked this model provider.");

        // Privacy guard: if the prompt contains Brain workspace references, only a local loopback "local" provider may see it.
        if (request.Messages.Any(message => message.Content.Any(part => part.Text.Contains(BrainWorkspace.ReferenceMarker, StringComparison.Ordinal))))
        {
            if (!Uri.TryCreate(candidate.Profile.BaseUrl, UriKind.Absolute, out var brainUri) || !brainUri.IsLoopback
                || !string.Equals(candidate.Profile.Provider, "local", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Brain work references are restricted to local loopback models; remote fallback was skipped.");
        }

        // Request body in the OpenAI chat format: model, messages, temperature (plus optional fields added below).
        var payload = new Dictionary<string, object?>
        {
            ["model"] = candidate.Model,
            ["messages"] = request.Messages.Select(SerializeMessage).ToArray(),
            ["temperature"] = request.Temperature
        };

        // Local servers get a reply-length cap per route; hosted servers get none (0 means "do not send a limit").
        var localMaxTokens = ResolveLocalMaxTokens(candidate.Profile, route);

        if (localMaxTokens > 0)
        {
            payload["max_tokens"] = localMaxTokens;
        }

        if (!string.IsNullOrWhiteSpace(request.User))
        {
            payload["user"] = request.User.Trim();
        }

        // Tool calling: send the tool list and let the model decide whether to use one ("auto").
        if (request.Tools is { Length: > 0 })
        {
            payload["tools"] = request.Tools.Select(SerializeTool).ToArray();
            payload["tool_choice"] = "auto";
        }

        var requestJson = JsonSerializer.Serialize(payload, JsonOptions);

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            BuildEndpoint(candidate.Profile.BaseUrl))
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(candidate.Profile.ApiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", candidate.Profile.ApiKey);
        }

        // Send with the per-route timeout, then read the whole body as text.
        using var response = await SendHttpAsync(message, cancellationToken, ModelTimeout(route));
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {TrimError(body)}");
        }

        using var document = JsonDocument.Parse(body);
        var completion = ExtractCompletion(document.RootElement, candidate);

        // Local call recording is configurable; it never changes inference success.
        LogPlannerCall(candidate, route, requestJson, document.RootElement);

        return completion;
    }

    // Guards the log file so two simultaneous model calls do not write into it at the same time.
    private static readonly object PlannerLogLock = new();

    // Optionally appends one JSON line (time, route, model, first answer, full request) to data/planner-log.jsonl.
    // Only for local servers and only when PlannerRecordLocalCalls is on. This is training/debugging data.
    private void LogPlannerCall(ModelCandidate candidate, ModelRoute route, string requestJson, JsonElement root)
    {
        try
        {
            if (!_options.PlannerRecordLocalCalls || !IsLocalLoopbackProfile(candidate.Profile)
                || !root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return;
            }

            var line = $"{{\"timestampUtc\":\"{DateTime.UtcNow:O}\",\"route\":\"{route}\",\"model\":{JsonSerializer.Serialize(candidate.Model)},"
                + $"\"response\":{JsonSerializer.Serialize(choices[0])},\"request\":{requestJson}}}{Environment.NewLine}";
            var path = JarvisDataPaths.Resolve("data/planner-log.jsonl");

            lock (PlannerLogLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, line, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Logging must never break a planner turn.
        }
    }

    // Converts a ChatMessage to the OpenAI JSON shape. A single text part is sent as a plain string; anything
    // else (such as text plus an image) is sent as an array of typed parts.
    private static object SerializeMessage(ChatMessage message)
    {
        object content = message.Content.Length == 1 && string.Equals(message.Content[0].Type, "text", StringComparison.OrdinalIgnoreCase)
            ? message.Content[0].Text
            : message.Content.Select(SerializeContentPart).ToArray();

        var payload = new Dictionary<string, object?>
        {
            ["role"] = message.Role,
            ["content"] = content
        };

        if (!string.IsNullOrWhiteSpace(message.ToolCallId))
        {
            payload["tool_call_id"] = message.ToolCallId;
        }

        if (message.ToolCalls is { Length: > 0 })
        {
            payload["tool_calls"] = message.ToolCalls.Select(toolCall => new Dictionary<string, object?>
            {
                ["id"] = toolCall.Id,
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = toolCall.Name,
                    ["arguments"] = toolCall.ArgumentsJson
                }
            }).ToArray();
        }

        return payload;
    }

    // Converts one part to {"type":"image_url","image_url":{"url":...}} or {"type":"text","text":...}.
    private static object SerializeContentPart(ChatMessageContentPart part)
    {
        return string.Equals(part.Type, "image_url", StringComparison.OrdinalIgnoreCase)
            ? new Dictionary<string, object?>
            {
                ["type"] = "image_url",
                ["image_url"] = new Dictionary<string, object?>
                {
                    ["url"] = part.Url
                }
            }
            : new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = part.Text
            };
    }

    // Describes a tool as an OpenAI "function" whose only parameter is a string called "input".
    private static object SerializeTool(ChatToolDefinition tool)
    {
        return new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["function"] = new Dictionary<string, object?>
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["input"] = new Dictionary<string, object?>
                        {
                            ["type"] = "string",
                            ["description"] = tool.InputDescription
                        }
                    },
                    ["additionalProperties"] = false
                }
            }
        };
    }

    // Reads choices[0].message from the response JSON (text plus tool calls); throws if there are no choices.
    private static ChatCompletionResponse ExtractCompletion(JsonElement root, ModelCandidate candidate)
    {
        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Model response did not contain choices.");
        }

        var message = choices[0].GetProperty("message");
        var content = ExtractMessageContent(message);
        var toolCalls = ExtractToolCalls(message);

        return new ChatCompletionResponse(
            content,
            toolCalls,
            candidate.Model,
            $"{candidate.Profile.Name}/{candidate.Profile.Provider}");
    }

    // The reply text can be a plain string or an array of parts with "text" fields; both are handled.
    private static string ExtractMessageContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
        {
            return string.Empty;
        }

        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Join(
                string.Empty,
                content.EnumerateArray().Select(element =>
                    element.TryGetProperty("text", out var text)
                        ? text.GetString() ?? string.Empty
                        : string.Empty)),
            _ => string.Empty
        };
    }

    // Reads message.tool_calls. Entries with no function or no name are skipped; a missing id gets a random one.
    private static ChatToolCall[] ExtractToolCalls(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var toolCalls)
            || toolCalls.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<ChatToolCall>();

        foreach (var toolCall in toolCalls.EnumerateArray())
        {
            if (!toolCall.TryGetProperty("function", out var function))
            {
                continue;
            }

            var id = toolCall.TryGetProperty("id", out var idElement)
                ? idElement.GetString() ?? Guid.NewGuid().ToString("n")
                : Guid.NewGuid().ToString("n");

            var name = GetString(function, "name");
            var arguments = GetString(function, "arguments") ?? "{}";

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            results.Add(new ChatToolCall(id, name.Trim(), arguments));
        }

        return results.ToArray();
    }

    // Turns a base URL into the full chat endpoint, adding /chat/completions unless it is already there.
    private static string BuildEndpoint(string baseUrl)
    {
        var trimmed = NormalizeOpenAiCompatibleBaseUrl(baseUrl);

        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return $"{trimmed}/chat/completions";
    }

    // A bare host such as http://localhost:1234 gets "/v1" added, because OpenAI-style APIs live under /v1.
    private static string NormalizeOpenAiCompatibleBaseUrl(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return trimmed;
        }

        var path = uri.AbsolutePath.TrimEnd('/');

        if (string.IsNullOrWhiteSpace(path))
        {
            return $"{trimmed}/v1";
        }

        return trimmed;
    }

    // Reads a property as text; non-string values (for example arguments sent as an object) are converted with ToString.
    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.ToString();
    }

    // Shortens long server error messages so they stay readable in logs and failure lists.
    private static string TrimError(string text)
    {
        const int maxLength = 320;
        var value = text.Trim();
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    // Maximum reply length (in tokens) for local models only, chosen per route to keep a small local model fast.
    private static int ResolveLocalMaxTokens(ResolvedProviderProfile profile, ModelRoute route)
    {
        if (!IsLocalLoopbackProfile(profile))
        {
            return 0;
        }

        return route switch
        {
            // Fast also carries spoken replies and the end-of-workflow summary; 128 tokens cut them mid-sentence on the small local model.
            ModelRoute.Fast => 256,
            ModelRoute.Vision => 192,
            // Room for a plan plus a reasoning model's thinking tokens; the brain tool keeps ~3,300 characters of plan + critique anyway.
            ModelRoute.Deep => 600,
            _ => 384
        };
    }

    // True when the provider runs on this computer: provider "local", or a localhost / 127.0.0.1 address.
    private static bool IsLocalLoopbackProfile(ResolvedProviderProfile profile)
    {
        if (string.Equals(profile.Provider, "local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase);
    }

    // One attempt to make: which provider and which model name.
    private sealed record ModelCandidate(ResolvedProviderProfile Profile, string Model);
}

// A fully worked-out provider: name, kind, server URL, the model name for each route and the API key.
internal sealed record ResolvedProviderProfile(
    string Name,
    string Provider,
    string BaseUrl,
    string RequestModel,
    string FastModel,
    string ReasoningModel,
    string VisionModel,
    string DeepModel,
    string ApiKey);
