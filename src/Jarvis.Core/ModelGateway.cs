// ModelGateway.cs: how Jarvis talks to its language model. Every "ask the AI" call in the app (the planner,
// vision, notes, the Brain tool) goes through IModelGateway.CompleteAsync, which turns a request into an
// OpenAI-style HTTP call: POST {PlannerBaseUrl}/v1/chat/completions. In practice that server is Jarvis's own llama-server on
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

// Everything needed for one model call. User is an optional label sent along as the OpenAI "user" field.
public sealed record ChatCompletionRequest(
    ChatMessage[] Messages,
    ChatToolDefinition[]? Tools = null,
    double Temperature = 0.2,
    string User = "");

// The model's answer: reply text, any tool calls, and which model name answered.
public sealed record ChatCompletionResponse(
    string Content,
    ChatToolCall[] ToolCalls,
    string Model);

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

        var profile = BuildProfile(options);

        // Local-only AI mode: drop a provider that is not a loopback (same-computer) server.
        if (profile is not null && !LocalAiPolicy.AllowsModelProvider(options, profile.Provider, profile.BaseUrl))
        {
            profile = null;
        }

        if (profile is null)
        {
            return new DisabledModelGateway(options.LocalAiOnly
                ? "local-only AI mode requires a local provider on this computer; hosted routes are blocked"
                : "no usable model provider is configured");
        }

        return Schedule(workspaceRoot, options, profile);
    }

    // Gateway for the escalation model (see AssistantPlanner.Escalation.cs): the same gateway class and queue, pointed at
    // PlannerEscalationBaseUrl with that one model on every route. Null when escalation is off (no model set), the planner is
    // off, or the address fails the same local-only check as the planner.
    public static IModelGateway? CreateEscalation(string workspaceRoot, JarvisOptions options)
    {
        if (!options.PlannerEnabled
            || string.IsNullOrWhiteSpace(options.PlannerEscalationModel)
            || string.IsNullOrWhiteSpace(options.PlannerEscalationBaseUrl)
            || !LocalAiPolicy.AllowsModelProvider(options, "local", options.PlannerEscalationBaseUrl))
        {
            return null;
        }

        var model = options.PlannerEscalationModel.Trim();
        return Schedule(workspaceRoot, options, new ResolvedProviderProfile("local", options.PlannerEscalationBaseUrl.Trim(), model, model, string.Empty, model, string.Empty));
    }

    // One inference budget per process: the app rebuilds the gateway on settings changes while earlier requests
    // may still run. ponytail: capacity is fixed at first use, so changing ModelMaxConcurrentRequests takes effect on restart.
    // Capacity is how many model calls may run at the same moment (1 to 4). The scheduler is shared across rebuilds.
    private static IModelGateway Schedule(string workspaceRoot, JarvisOptions options, ResolvedProviderProfile profile)
    {
        var capacity = Math.Clamp(options.ModelMaxConcurrentRequests, 1, 4);
        var scheduler = LazyInitializer.EnsureInitialized(ref _sharedScheduler, () => new ModelWorkScheduler(capacity));
        return new ScheduledModelGateway(new OpenAiCompatibleModelGateway(workspaceRoot, options, profile), capacity, scheduler);
    }

    // The single queue shared by every gateway built in this process.
    private static ModelWorkScheduler? _sharedScheduler;

    // Turns the Planner* settings into the one provider profile; returns null when no base URL is set.
    private static ResolvedProviderProfile? BuildProfile(JarvisOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.PlannerBaseUrl))
        {
            return null;
        }

        // If no separate reasoning model is configured, the general model is used for reasoning too.
        var reasoningModel = string.IsNullOrWhiteSpace(options.PlannerReasoningModel)
            ? options.PlannerModel?.Trim() ?? string.Empty
            : options.PlannerReasoningModel.Trim();

        return new ResolvedProviderProfile(
            NormalizeProvider(options.PlannerProvider),
            options.PlannerBaseUrl.Trim(),
            options.PlannerFastModel.Trim(),
            reasoningModel,
            options.PlannerVisionModel.Trim(),
            options.PlannerDeepModel.Trim(),
            // The API key comes from the settings value, Windows Credential Manager, or an environment variable (see ApiKeyResolver).
            ApiKeyResolver.Resolve(
                options.PlannerApiKey,
                options.PlannerApiKeyCredentialTarget,
                options.PlannerApiKeyEnvironmentVariable));
    }

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
    private readonly ResolvedProviderProfile _profile;
    private readonly HttpClient _httpClient;

    public OpenAiCompatibleModelGateway(string workspaceRoot, JarvisOptions options, ResolvedProviderProfile profile)
    {
        _workspaceRoot = workspaceRoot;
        _options = options;
        _profile = profile;
        // Timeouts are per request (see SendHttpAsync): a deep pass may legitimately run for minutes.
        _httpClient = LocalAiPolicy.CreateHttpClient(options.LocalAiOnly, Timeout.InfiniteTimeSpan);
    }

    // How long to wait for a normal answer before giving up.
    private static readonly TimeSpan DefaultModelTimeout = TimeSpan.FromSeconds(45);
    // A CPU-bound deep model produces ~9 tokens/s here and the first call also waits for the server to load it.
    private static readonly TimeSpan DeepModelTimeout = TimeSpan.FromMinutes(5);

    // Deep gets the long timeout; every other route gets the normal one.
    private static TimeSpan ModelTimeout(ModelRoute route) => route == ModelRoute.Deep ? DeepModelTimeout : DefaultModelTimeout;

    public bool IsAvailable => true;

    // A one-line description of the configured provider and its fast/reasoning models, shown in the UI.
    public string Status =>
        $"{_profile.Provider} fast={(string.IsNullOrWhiteSpace(_profile.FastModel) ? "n/a" : _profile.FastModel)} reasoning={(string.IsNullOrWhiteSpace(_profile.ReasoningModel) ? "n/a" : _profile.ReasoningModel)}";

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

    // Tries each model for this route in order and returns the first success. Ordinary errors (HTTP failures, bad JSON)
    // move on to the next model; cancellation and timeouts stop immediately.
    public async Task<ChatCompletionResponse> CompleteAsync(
        ChatCompletionRequest request,
        ModelRoute route,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();

        foreach (var model in GetRouteModels(route))
        {
            try
            {
                return await SendAsync(model, request, route, cancellationToken);
            }
            // A timeout already cost the full HTTP timeout while holding the model slot; fail now instead of waiting again.
            catch (Exception exception) when (exception is not OperationCanceledException and not TimeoutException)
            {
                failures.Add($"{model}: {TrimError(exception.Message)}");
            }
        }

        throw new InvalidOperationException(
            "All planner model candidates failed. " + string.Join(" | ", failures));
    }

    // The model fallback chain for each route, first choice first, blanks and repeats (ignoring case) dropped.
    // Fast: fast, reasoning. Vision: vision, reasoning. Deep: deep, reasoning, fast. Reasoning: reasoning, fast.
    private IEnumerable<string> GetRouteModels(ModelRoute route)
    {
        string[] chain = route switch
        {
            ModelRoute.Fast => [_profile.FastModel, _profile.ReasoningModel],
            ModelRoute.Vision => [_profile.VisionModel, _profile.ReasoningModel],
            ModelRoute.Deep => [_profile.DeepModel, _profile.ReasoningModel, _profile.FastModel],
            _ => [_profile.ReasoningModel, _profile.FastModel]
        };

        return chain.Where(model => !string.IsNullOrWhiteSpace(model)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    // Makes one HTTP call to one model: builds the JSON body, sends it, checks the status and parses the reply.
    private async Task<ChatCompletionResponse> SendAsync(
        string model,
        ChatCompletionRequest request,
        ModelRoute route,
        CancellationToken cancellationToken)
    {
        // Privacy guard: if the prompt contains Brain workspace references, only a local loopback "local" provider may see it.
        if (request.Messages.Any(message => message.Content.Any(part => part.Text.Contains(BrainWorkspace.ReferenceMarker, StringComparison.Ordinal))))
        {
            if (!Uri.TryCreate(_profile.BaseUrl, UriKind.Absolute, out var brainUri) || !brainUri.IsLoopback
                || !string.Equals(_profile.Provider, "local", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Brain work references are restricted to local loopback models; remote fallback was skipped.");
        }

        // Request body in the OpenAI chat format: model, messages, temperature (plus optional fields added below).
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = request.Messages.Select(SerializeMessage).ToArray(),
            ["temperature"] = request.Temperature
        };

        // Local servers get a reply-length cap per route; hosted servers get none (0 means "do not send a limit").
        var localMaxTokens = ResolveLocalMaxTokens(_profile, route);

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
            BuildEndpoint(_profile.BaseUrl))
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(_profile.ApiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _profile.ApiKey);
        }

        // Send with the per-route timeout, then read the whole body as text.
        using var response = await SendHttpAsync(message, cancellationToken, ModelTimeout(route));
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {TrimError(body)}");
        }

        using var document = JsonDocument.Parse(body);
        var completion = ExtractCompletion(document.RootElement, model);

        // Local call recording is configurable; it never changes inference success.
        LogPlannerCall(model, route, requestJson, document.RootElement);

        return completion;
    }

    // Guards the log file so two simultaneous model calls do not write into it at the same time.
    private static readonly object PlannerLogLock = new();

    // Optionally appends one JSON line (time, route, model, first answer, full request) to data/planner-log.jsonl.
    // Only for local servers and only when PlannerRecordLocalCalls is on. This is training/debugging data.
    private void LogPlannerCall(string model, ModelRoute route, string requestJson, JsonElement root)
    {
        try
        {
            if (!_options.PlannerRecordLocalCalls || !IsLocalLoopbackProfile(_profile)
                || !root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return;
            }

            var line = $"{{\"timestampUtc\":\"{DateTime.UtcNow:O}\",\"route\":\"{route}\",\"model\":{JsonSerializer.Serialize(model)},"
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
    private static ChatCompletionResponse ExtractCompletion(JsonElement root, string model)
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
            model);
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

    // A bare host such as http://localhost:8179 gets "/v1" added, because OpenAI-style APIs live under /v1.
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
}

// A fully worked-out provider: kind, server URL, the model name for each route and the API key.
internal sealed record ResolvedProviderProfile(
    string Provider,
    string BaseUrl,
    string FastModel,
    string ReasoningModel,
    string VisionModel,
    string DeepModel,
    string ApiKey);
