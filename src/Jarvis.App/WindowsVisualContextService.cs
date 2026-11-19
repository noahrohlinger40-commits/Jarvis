// WindowsVisualContextService.cs: Jarvis's "eyes" (IVisualContextService in Jarvis.Core): screenshots of a display or window,
// the monitor list, and analysis of images, scans and text files via the language model's vision route (ModelRoute.Vision) or fast text route.
// The agent's screen and document tools call it (created in AppBootstrapper.cs). The "OCR" and "UI detection" here are not Windows OCR
// or UI Automation: the screenshot goes to a vision model asked to answer in a fixed JSON shape. Screenshots are saved as PNGs and old ones pruned.
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.Core;

namespace Jarvis.App;

internal sealed class WindowsVisualContextService : IVisualContextService
{
    // File types analyze image accepts (the vision model reads these).
    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".gif",
        ".bmp",
        ".webp",
        ".tif",
        ".tiff"
    };

    // File types analyze document reads as plain text.
    private static readonly HashSet<string> SupportedTextDocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt",
        ".md",
        ".json",
        ".cs",
        ".csproj",
        ".sln",
        ".xml",
        ".yml",
        ".yaml",
        ".html",
        ".css",
        ".js",
        ".ts",
        ".tsx",
        ".jsx",
        ".ps1",
        ".cmd",
        ".bat",
        ".csv",
        ".log",
        ".ini",
        ".config",
        ".toml"
    };

    // How model-returned JSON is read: web-style camelCase names, ignoring letter case.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    // Where relative file paths are resolved from.
    private readonly string _workspaceRoot;
    // Settings used here: capture folder and how many screenshots to keep.
    private readonly JarvisOptions _options;
    // The language-model client; IsAvailable is false when no model provider is set up.
    private readonly IModelGateway _modelGateway;

    public WindowsVisualContextService(string workspaceRoot, JarvisOptions options, IModelGateway modelGateway)
    {
        _workspaceRoot = workspaceRoot;
        _options = options;
        _modelGateway = modelGateway;
    }

    // "screen analyze": take a screenshot (display or window chosen from the text in input), then ask the vision model a question about it. If no model is available or the model fails, the screenshot is still saved and reported.
    public async Task<ToolResult> AnalyzeCurrentScreenAsync(string input, CancellationToken cancellationToken)
    {
        CapturedScreen capture;

        try
        {
            // ParseScreenRequest works out what to capture and what to ask from the user's words; CaptureScreen takes the picture.
            capture = CaptureScreen(ParseScreenRequest(input));
        }
        // Cancellation is passed on; any other failure becomes a failed ToolResult rather than an exception.
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed("Visual analysis failed.", exception.Message, "The visual context service could not capture the requested display.");
        }

        // Default question when the user only named a target.
        var question = string.IsNullOrWhiteSpace(capture.Request.Question)
            ? "Describe the captured display, the visible UI, notable text, and any state that matters right now."
            : capture.Request.Question;

        if (!_modelGateway.IsAvailable)
        {
            return BuildCaptureResult(
                capture,
                "Captured a fresh screenshot, but no vision-capable model is configured. Ask `screen ocr`, `screen ui`, or `analyze image` again after wiring a planner vision model.",
                succeeded: true);
        }

        // The text prompt: facts about the capture (displays, active window) followed by the question. The image goes alongside it below.
        var prompt = string.Join(
            Environment.NewLine,
            BuildCapturePromptContext(capture),
            $"Question: {question}");

        try
        {
            var content = await CompleteVisionPromptAsync(
                "You are Jarvis. Analyze the supplied desktop screenshot. Stay grounded in the image and metadata. Call out the active window, visible applications, meaningful text, and actionable UI state. If something is uncertain, say so plainly.",
                [ChatMessageContentPart.FromText(prompt), await BuildImagePartAsync(capture.FilePath, cancellationToken)],
                cancellationToken);

            content = string.IsNullOrWhiteSpace(content)
                ? $"I captured {capture.TargetLabel}, but the vision model returned an empty response."
                : content.Trim();

            // The first result string is what the model sees and may say; VerificationText and SummaryText are short notes for the agent's checks and the spoken reply.
            return new ToolResult(
                $"Visual analysis for {capture.FilePath}:{Environment.NewLine}{content}",
                VerificationText: $"Captured {capture.TargetLabel} to {capture.FilePath} and analyzed it with the vision route.",
                SummaryText: FirstSentence(content));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return BuildCaptureResult(
                capture,
                $"Captured the screenshot, but model analysis failed: {TrimError(exception.Message)}",
                succeeded: false);
        }
    }

    // "screen capture": just save the screenshot and describe it; no model involved.
    public Task<ToolResult> CaptureScreenAsync(string input, CancellationToken cancellationToken)
    {
        try
        {
            var capture = CaptureScreen(ParseScreenRequest(input));
            return Task.FromResult(BuildCaptureResult(capture, "Capture completed.", succeeded: true));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Task.FromResult(Failed("Screen capture failed.", exception.Message, "The visual context service could not save the requested screenshot."));
        }
    }

    // "screen ocr": ask the vision model to read all visible text and return JSON (summary, fullText, textBlocks).
    public async Task<ToolResult> ExtractScreenTextAsync(string input, CancellationToken cancellationToken)
    {
        CapturedScreen capture;

        try
        {
            capture = CaptureScreen(ParseScreenRequest(input));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed("Screen OCR failed.", exception.Message, "The visual context service could not capture the requested display.");
        }

        if (!_modelGateway.IsAvailable)
        {
            return BuildCaptureResult(
                capture,
                "OCR-style extraction requires a vision-capable model route. The screenshot was still captured for later inspection.",
                succeeded: false);
        }

        var prompt = string.Join(
            Environment.NewLine,
            BuildCapturePromptContext(capture),
            string.IsNullOrWhiteSpace(capture.Request.Question)
                ? "Extract the visible text in reading order and summarize what the text suggests about the current task."
                : $"Additional instruction: {capture.Request.Question}");

        try
        {
            // The reply is asked to be pure JSON, matching OcrExtractionContract.
            var content = await CompleteVisionPromptAsync(
                "You are Jarvis. Perform OCR-style extraction on the supplied desktop screenshot. Return only JSON with keys `summary`, `fullText`, and `textBlocks`. Each `textBlocks` item must contain `text`, `region`, and `purpose`.",
                [ChatMessageContentPart.FromText(prompt), await BuildImagePartAsync(capture.FilePath, cancellationToken)],
                cancellationToken);

            // If the model ignored the format and returned prose, ParseJson gives null and the raw text is shown instead.
            var extraction = ParseJson<OcrExtractionContract>(content);

            if (extraction is null)
            {
                return new ToolResult(
                    $"Screen OCR for {capture.FilePath}:{Environment.NewLine}{content}",
                    VerificationText: $"Captured {capture.TargetLabel} to {capture.FilePath} and asked the vision route to extract text.",
                    SummaryText: "Extracted visible screen text.");
            }

            var lines = new List<string>
            {
                $"Screen OCR for {capture.FilePath}:",
                $"- Summary: {Coalesce(extraction.Summary, "No OCR summary returned.")}",
                $"- Full text: {Coalesce(extraction.FullText, "No text returned.")}"
            };

            if (extraction.TextBlocks is { Length: > 0 })
            {
                lines.Add("- Text blocks:");

                // Show at most 10 text blocks to keep the result short.
                foreach (var block in extraction.TextBlocks.Take(10))
                {
                    lines.Add($"  - {Coalesce(block.Region, "unknown region")} | {Coalesce(block.Purpose, "unspecified")} | {Coalesce(block.Text, "(empty)")}");
                }
            }

            return new ToolResult(
                string.Join(Environment.NewLine, lines),
                VerificationText: $"Captured {capture.TargetLabel} to {capture.FilePath} and extracted OCR-style text with the vision route.",
                SummaryText: FirstSentence(Coalesce(extraction.Summary, extraction.FullText)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return BuildCaptureResult(
                capture,
                $"Captured the screenshot, but OCR-style extraction failed: {TrimError(exception.Message)}",
                succeeded: false);
        }
    }

    // "screen ui": ask the vision model to list the visible UI elements as JSON (UiDetectionContract).
    public async Task<ToolResult> DetectScreenUiAsync(string input, CancellationToken cancellationToken)
    {
        CapturedScreen capture;

        try
        {
            capture = CaptureScreen(ParseScreenRequest(input));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed("Structured UI detection failed.", exception.Message, "The visual context service could not capture the requested display.");
        }

        if (!_modelGateway.IsAvailable)
        {
            return BuildCaptureResult(
                capture,
                "Structured UI extraction requires a vision-capable model route. The screenshot was still captured for later inspection.",
                succeeded: false);
        }

        var prompt = string.Join(
            Environment.NewLine,
            BuildCapturePromptContext(capture),
            string.IsNullOrWhiteSpace(capture.Request.Question)
                ? "Identify the major UI elements, controls, visible text, and likely interaction targets."
                : $"Additional instruction: {capture.Request.Question}");

        try
        {
            // The reply is asked to be pure JSON, matching UiDetectionContract.
            var content = await CompleteVisionPromptAsync(
                "You are Jarvis. Detect structured UI elements from the supplied desktop screenshot. Return only JSON with keys `summary`, `applicationGuess`, and `uiElements`. Each `uiElements` item must contain `label`, `role`, `text`, `state`, `region`, and `importance`.",
                [ChatMessageContentPart.FromText(prompt), await BuildImagePartAsync(capture.FilePath, cancellationToken)],
                cancellationToken);

            var detection = ParseJson<UiDetectionContract>(content);

            if (detection is null)
            {
                return new ToolResult(
                    $"Structured UI detection for {capture.FilePath}:{Environment.NewLine}{content}",
                    VerificationText: $"Captured {capture.TargetLabel} to {capture.FilePath} and asked the vision route to detect structured UI elements.",
                    SummaryText: "Detected screen UI elements.");
            }

            var lines = new List<string>
            {
                $"Structured UI detection for {capture.FilePath}:",
                $"- Summary: {Coalesce(detection.Summary, "No UI summary returned.")}",
                $"- Application guess: {Coalesce(detection.ApplicationGuess, "Unknown")}"
            };

            if (detection.UiElements is { Length: > 0 })
            {
                lines.Add("- UI elements:");

                // Show at most 12 elements.
                foreach (var element in detection.UiElements.Take(12))
                {
                    lines.Add($"  - {Coalesce(element.Region, "unknown region")} | {Coalesce(element.Role, "control")} | {Coalesce(element.Label, "unnamed")} | text={Coalesce(element.Text, "(none)")} | state={Coalesce(element.State, "unknown")} | importance={Coalesce(element.Importance, "normal")}");
                }
            }

            return new ToolResult(
                string.Join(Environment.NewLine, lines),
                VerificationText: $"Captured {capture.TargetLabel} to {capture.FilePath} and extracted structured UI elements with the vision route.",
                SummaryText: FirstSentence(Coalesce(detection.Summary, detection.ApplicationGuess)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return BuildCaptureResult(
                capture,
                $"Captured the screenshot, but structured UI detection failed: {TrimError(exception.Message)}",
                succeeded: false);
        }
    }

    // "screen monitors": list the connected displays and mark the active one.
    public Task<ToolResult> ListDisplaysAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var displays = GetDisplays();
            var activeWindow = TryGetActiveWindowSnapshot();
            var activeDisplay = ResolveActiveDisplay(displays, activeWindow);
            var lines = new List<string>
            {
                "Display inventory:",
                $"- Displays detected: {displays.Length}",
                $"- Active display: {(activeDisplay is null ? "Unknown" : DescribeDisplay(activeDisplay))}"
            };

            foreach (var display in displays)
            {
                var isActive = activeDisplay is not null && display.Index == activeDisplay.Index ? " | active" : string.Empty;
                lines.Add($"- {DescribeDisplay(display)}{isActive}");
            }

            return Task.FromResult(new ToolResult(
                string.Join(Environment.NewLine, lines),
                VerificationText: $"Enumerated {displays.Length} connected display(s).",
                SummaryText: $"Detected {displays.Length} display(s)."));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Task.FromResult(Failed("Display inventory failed.", exception.Message, "The visual context service could not enumerate displays."));
        }
    }

    // "analyze image <path> [:: question]": check the file exists and has a supported image type, then ask the vision model.
    public async Task<ToolResult> AnalyzeImageAsync(string input, CancellationToken cancellationToken)
    {
        var payload = ParsePayload(input);

        if (string.IsNullOrWhiteSpace(payload.Path))
        {
            return new ToolResult(
                "Usage: analyze image <path> or analyze image <path> :: <question>",
                Succeeded: false,
                VerificationText: "No image path was provided.",
                SummaryText: "Missing image path.");
        }

        var resolvedPath = ResolvePath(payload.Path);

        if (!File.Exists(resolvedPath))
        {
            return new ToolResult(
                $"Image file not found: {resolvedPath}",
                Succeeded: false,
                VerificationText: "The requested image path does not exist.",
                SummaryText: "Image file not found.");
        }

        if (!SupportedImageExtensions.Contains(Path.GetExtension(resolvedPath)))
        {
            return new ToolResult(
                $"Unsupported image type: {resolvedPath}",
                Succeeded: false,
                VerificationText: "The file extension is not in the supported image list.",
                SummaryText: "Unsupported image type.");
        }

        if (!_modelGateway.IsAvailable)
        {
            return Unavailable("Image understanding is unavailable because no model provider is configured.");
        }

        var question = string.IsNullOrWhiteSpace(payload.Question)
            ? "Describe the image and call out anything visually important."
            : payload.Question;

        return await AnalyzeSingleImageAsync(
            resolvedPath,
            question,
            $"Read the image from {resolvedPath} and analyzed it with the vision route.",
            "Visual analysis",
            cancellationToken);
    }

    // "analyze document <path> [:: question]": route by file type: images go to the vision model, PDFs are refused (no PDF reader), supported text files are read and summarized, anything else is rejected.
    public async Task<ToolResult> AnalyzeDocumentAsync(string input, CancellationToken cancellationToken)
    {
        var payload = ParsePayload(input);

        if (string.IsNullOrWhiteSpace(payload.Path))
        {
            return new ToolResult(
                "Usage: analyze document <path> or analyze document <path> :: <question>",
                Succeeded: false,
                VerificationText: "No document path was provided.",
                SummaryText: "Missing document path.");
        }

        var resolvedPath = ResolvePath(payload.Path);

        if (!File.Exists(resolvedPath))
        {
            return new ToolResult(
                $"Document not found: {resolvedPath}",
                Succeeded: false,
                VerificationText: "The requested document path does not exist.",
                SummaryText: "Document file not found.");
        }

        var extension = Path.GetExtension(resolvedPath);

        if (SupportedImageExtensions.Contains(extension))
        {
            return await AnalyzeImageDocumentAsync(resolvedPath, payload.Question, cancellationToken);
        }

        if (string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return new ToolResult(
                "I can't read PDF files. Open the PDF, or give me a text or image file.",
                Succeeded: false,
                VerificationText: "The file is a PDF, which analyze document does not read, so nothing was read.",
                SummaryText: "PDF files are not supported.");
        }

        if (!SupportedTextDocumentExtensions.Contains(extension))
        {
            return new ToolResult(
                $"Unsupported document type: {resolvedPath}",
                Succeeded: false,
                VerificationText: "The file extension is not in the supported text-document list.",
                SummaryText: "Unsupported document type.");
        }

        return await AnalyzeTextDocumentAsync(resolvedPath, payload.Question, cancellationToken);
    }

    // Read a text file, build a few facts about it (type, character count, line count) and hand it to the summarizer.
    private async Task<ToolResult> AnalyzeTextDocumentAsync(string resolvedPath, string question, CancellationToken cancellationToken)
    {
        var documentText = await File.ReadAllTextAsync(resolvedPath, cancellationToken);

        if (string.IsNullOrWhiteSpace(documentText))
        {
            return new ToolResult(
                $"Document is empty: {resolvedPath}",
                Succeeded: false,
                VerificationText: "The file was read successfully but did not contain text.",
                SummaryText: "Document is empty.");
        }

        var metadataLines = new[]
        {
            $"Document type: text {Path.GetExtension(resolvedPath)}",
            $"Characters: {documentText.Length}",
            $"Lines: {CountLines(documentText)}"
        };

        return await AnalyzeStructuredTextDocumentAsync(
            resolvedPath,
            "text document",
            documentText,
            question,
            metadataLines,
            cancellationToken);
    }

    // A scan, receipt or form saved as an image: ask the vision model to read it and return JSON (DocumentAnalysisContract).
    private async Task<ToolResult> AnalyzeImageDocumentAsync(string resolvedPath, string question, CancellationToken cancellationToken)
    {
        if (!_modelGateway.IsAvailable)
        {
            return Unavailable("Image-based document OCR requires a vision-capable model route.");
        }

        var effectiveQuestion = string.IsNullOrWhiteSpace(question)
            ? "Extract the document text, identify the document type, and summarize the important details."
            : question.Trim();

        try
        {
            var content = await CompleteVisionPromptAsync(
                "You are Jarvis. The supplied image is a document, scan, receipt, form, slide, or OCR-heavy page. Perform OCR and visual document understanding. Return only JSON with keys `summary`, `documentType`, `confidence`, `extractedTextPreview`, `caveats`, `keyPoints`, `actionItems`, `sections`, and `keyFields`. Each `sections` item must contain `heading` and `purpose`. Each `keyFields` item must contain `label` and `value`.",
                [ChatMessageContentPart.FromText($"Question: {effectiveQuestion}{Environment.NewLine}Path: {resolvedPath}"),
                 await BuildImagePartAsync(resolvedPath, cancellationToken)],
                cancellationToken);

            var analysis = ParseJson<DocumentAnalysisContract>(content);

            if (analysis is null)
            {
                return new ToolResult(
                    $"Document OCR for {resolvedPath}:{Environment.NewLine}{content}",
                    VerificationText: $"Read the image-based document from {resolvedPath} and analyzed it with the vision route.",
                    SummaryText: "Analyzed image-based document.");
            }

            return BuildStructuredDocumentResult(
                resolvedPath,
                analysis,
                "image document",
                $"Read the image-based document from {resolvedPath} and analyzed it with the vision route.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ToolResult(
                $"Document OCR failed: {TrimError(exception.Message)}",
                Succeeded: false,
                VerificationText: $"The visual context service could not analyze the document image at {resolvedPath}.",
                SummaryText: "Document OCR failed.");
        }
    }

    // Summarize a text document: use only the first 16,000 characters (to stay within the model's context), and ask the fast text model for JSON. Without a model, return a short preview instead.
    private async Task<ToolResult> AnalyzeStructuredTextDocumentAsync(
        string resolvedPath,
        string sourceKind,
        string documentText,
        string question,
        IReadOnlyList<string> metadataLines,
        CancellationToken cancellationToken)
    {
        var excerpt = TruncateDocumentText(documentText, 16000);

        if (!_modelGateway.IsAvailable)
        {
            return new ToolResult(
                BuildDocumentPreview(resolvedPath, sourceKind, excerpt, metadataLines),
                VerificationText: "The file was read successfully without model summarization.",
                SummaryText: $"Loaded a document preview from {resolvedPath}.");
        }

        var effectiveQuestion = string.IsNullOrWhiteSpace(question)
            ? "Summarize the document and highlight the most important details."
            : question.Trim();

        // System message tells the model the JSON keys to return; the user message carries the question, path, facts and excerpt.
        var messages = new[]
        {
            new ChatMessage("system", [ChatMessageContentPart.FromText("You are Jarvis. Analyze the supplied document excerpt and return only JSON with keys `summary`, `documentType`, `confidence`, `extractedTextPreview`, `caveats`, `keyPoints`, `actionItems`, `sections`, and `keyFields`. Each `sections` item must contain `heading` and `purpose`. Each `keyFields` item must contain `label` and `value`. Keep the analysis grounded in the provided text; if OCR or PDF extraction appears incomplete, say so in `caveats`.")]),
            new ChatMessage("user", [ChatMessageContentPart.FromText(
                string.Join(Environment.NewLine, BuildDocumentPromptLines(resolvedPath, effectiveQuestion, metadataLines, excerpt)))])
        };

        try
        {
            var response = await _modelGateway.CompleteAsync(
                // Temperature 0.1 = nearly deterministic answers.
                new ChatCompletionRequest(messages, Temperature: 0.1),
                ModelRoute.Fast,
                cancellationToken);

            var content = response.Content?.Trim() ?? string.Empty;
            var analysis = ParseJson<DocumentAnalysisContract>(content);

            if (analysis is null)
            {
                var fallback = string.IsNullOrWhiteSpace(content)
                    ? $"I read the document at {resolvedPath}, but the analysis model returned an empty response."
                    : content;

                return new ToolResult(
                    $"Document analysis for {resolvedPath}:{Environment.NewLine}{fallback}",
                    VerificationText: $"Read the document from {resolvedPath} and analyzed the excerpt with the fast model route.",
                    SummaryText: FirstSentence(fallback));
            }

            return BuildStructuredDocumentResult(
                resolvedPath,
                analysis,
                sourceKind,
                $"Read the document from {resolvedPath} and analyzed the excerpt with the fast model route.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ToolResult(
                $"Document analysis failed: {TrimError(exception.Message)}",
                Succeeded: false,
                VerificationText: $"The document was read, but model analysis failed for {resolvedPath}.",
                SummaryText: "Document analysis failed.");
        }
    }

    // Turn the parsed JSON into readable lines, with every list capped (8 to 10 entries) so the reply stays short.
    private static ToolResult BuildStructuredDocumentResult(
        string resolvedPath,
        DocumentAnalysisContract analysis,
        string sourceKind,
        string verificationText)
    {
        var lines = new List<string>
        {
            $"Document analysis for {resolvedPath}:",
            $"- Source: {sourceKind}",
            $"- Document type: {Coalesce(analysis.DocumentType, "Unknown")}",
            $"- Summary: {Coalesce(analysis.Summary, "No summary returned.")}",
            $"- Confidence: {Coalesce(analysis.Confidence, "Unknown")}"
        };

        if (!string.IsNullOrWhiteSpace(analysis.ExtractedTextPreview))
        {
            lines.Add($"- Extracted text preview: {analysis.ExtractedTextPreview.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(analysis.Caveats))
        {
            lines.Add($"- Caveats: {analysis.Caveats.Trim()}");
        }

        if (analysis.KeyPoints is { Length: > 0 })
        {
            lines.Add("- Key points:");
            lines.AddRange(analysis.KeyPoints.Where(item => !string.IsNullOrWhiteSpace(item)).Take(8).Select(item => $"  - {item.Trim()}"));
        }

        if (analysis.KeyFields is { Length: > 0 })
        {
            lines.Add("- Key fields:");
            lines.AddRange(analysis.KeyFields
                .Where(item => !string.IsNullOrWhiteSpace(item.Label) || !string.IsNullOrWhiteSpace(item.Value))
                .Take(10)
                .Select(item => $"  - {Coalesce(item.Label, "field")}: {Coalesce(item.Value, "(missing)")}"));
        }

        if (analysis.Sections is { Length: > 0 })
        {
            lines.Add("- Sections:");
            lines.AddRange(analysis.Sections
                .Where(item => !string.IsNullOrWhiteSpace(item.Heading) || !string.IsNullOrWhiteSpace(item.Purpose))
                .Take(10)
                .Select(item => $"  - {Coalesce(item.Heading, "untitled")} | {Coalesce(item.Purpose, "no purpose returned")}"));
        }

        if (analysis.ActionItems is { Length: > 0 })
        {
            lines.Add("- Action items:");
            lines.AddRange(analysis.ActionItems.Where(item => !string.IsNullOrWhiteSpace(item)).Take(8).Select(item => $"  - {item.Trim()}"));
        }

        return new ToolResult(
            string.Join(Environment.NewLine, lines),
            VerificationText: verificationText,
            SummaryText: FirstSentence(Coalesce(analysis.Summary, analysis.DocumentType)));
    }

    // A plain preview used when no model is available.
    private static string BuildDocumentPreview(
        string resolvedPath,
        string sourceKind,
        string excerpt,
        IReadOnlyList<string> metadataLines)
    {
        var lines = new List<string>
        {
            $"Document preview for {resolvedPath}:",
            $"- Source: {sourceKind}"
        };

        lines.AddRange(metadataLines.Select(line => $"- {line}"));
        lines.Add($"- Extracted text preview: {BuildPreviewText(excerpt, 1600)}");
        return string.Join(Environment.NewLine, lines);
    }

    // The lines sent to the model: question, path, facts about the file, then the text excerpt.
    private static IReadOnlyList<string> BuildDocumentPromptLines(
        string resolvedPath,
        string question,
        IReadOnlyList<string> metadataLines,
        string excerpt)
    {
        var lines = new List<string>
        {
            $"Question: {question}",
            $"Path: {resolvedPath}"
        };

        lines.AddRange(metadataLines);
        lines.Add($"Document excerpt:{Environment.NewLine}{excerpt}");
        return lines;
    }

    // Collapse the text to one line and cut it to maxCharacters, marking the cut.
    private static string TruncateDocumentText(string documentText, int maxCharacters)
    {
        var normalized = NormalizeExtractedDocumentText(documentText);

        if (normalized.Length <= maxCharacters)
        {
            return normalized;
        }

        return normalized[..maxCharacters].TrimEnd() + Environment.NewLine + "[document truncated]";
    }

    // Same as above but for the short preview, ending in "..." when cut.
    private static string BuildPreviewText(string documentText, int maxCharacters)
    {
        var normalized = NormalizeExtractedDocumentText(documentText);

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "No text preview available.";
        }

        return normalized.Length <= maxCharacters
            ? normalized
            : normalized[..maxCharacters].TrimEnd() + "...";
    }

    // Number of lines = number of newline characters plus one.
    private static int CountLines(string documentText)
    {
        if (string.IsNullOrEmpty(documentText))
        {
            return 0;
        }

        return documentText.Count(character => character == '\n') + 1;
    }

    // Make text one tidy line: remove null characters, turn line breaks into spaces, collapse runs of whitespace.
    private static string NormalizeExtractedDocumentText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sanitized = value
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .ReplaceLineEndings(" ");

        // \s+ matches any run of whitespace (spaces, tabs, newlines); each run becomes one space.
        sanitized = Regex.Replace(sanitized, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
        return sanitized;
    }

    // Ask the vision model about one image file and wrap its answer (or the error) in a ToolResult.
    private async Task<ToolResult> AnalyzeSingleImageAsync(
        string filePath,
        string question,
        string verificationText,
        string prefix,
        CancellationToken cancellationToken)
    {
        try
        {
            var content = await CompleteVisionPromptAsync(
                "You are Jarvis. Analyze the supplied image and answer the user's question concisely.",
                [ChatMessageContentPart.FromText(question), await BuildImagePartAsync(filePath, cancellationToken)],
                cancellationToken);

            content = string.IsNullOrWhiteSpace(content)
                ? $"The vision model inspected {filePath}, but it returned an empty response."
                : content.Trim();

            return new ToolResult(
                $"{prefix} for {filePath}:{Environment.NewLine}{content}",
                VerificationText: verificationText,
                SummaryText: FirstSentence(content));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ToolResult(
                $"{prefix} failed: {TrimError(exception.Message)}",
                Succeeded: false,
                VerificationText: $"The visual context service could not analyze {filePath}.",
                SummaryText: $"{prefix} failed.");
        }
    }

    // Send one system message plus the user's parts (text and image) to the vision route and return the reply text.
    private async Task<string> CompleteVisionPromptAsync(
        string systemPrompt,
        IReadOnlyList<ChatMessageContentPart> userParts,
        CancellationToken cancellationToken)
    {
        var messages = new[]
        {
            new ChatMessage("system", [ChatMessageContentPart.FromText(systemPrompt)]),
            new ChatMessage("user", userParts.ToArray())
        };

        var response = await _modelGateway.CompleteAsync(
            new ChatCompletionRequest(messages, Temperature: 0.1),
            ModelRoute.Vision,
            cancellationToken);

        return response.Content?.Trim() ?? string.Empty;
    }

    // Turn an image file into the "data URL" form (base64 text inside the request) that vision APIs accept. TIFF is converted to PNG first because vision models do not take TIFF.
    private async Task<ChatMessageContentPart> BuildImagePartAsync(string filePath, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(filePath);

        if (extension is ".tif" or ".tiff")
        {
            using var bitmap = new Bitmap(filePath);
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return ChatMessageContentPart.FromImageUrl(BuildDataUrl("rendered.png", stream.ToArray()));
        }

        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        return ChatMessageContentPart.FromImageUrl(BuildDataUrl(filePath, bytes));
    }

    // Take the actual screenshot. Steps: find displays and windows, work out the rectangle to capture, save as PNG, delete old captures.
    private CapturedScreen CaptureScreen(ScreenRequest request)
    {
        var displays = GetDisplays();

        if (displays.Length == 0)
        {
            throw new InvalidOperationException("No displays were detected.");
        }

        var activeWindow = TryGetActiveWindowSnapshot();
        // For "active window" this is the foreground window; for a named window it is the best title match.
        var targetWindow = ResolveTargetWindow(request, activeWindow);
        var targetDisplays = ResolveTargetDisplays(request, displays, activeWindow, targetWindow);
        // The rectangle to copy: all displays combined, one window, or one display.
        var bounds = request.TargetKind switch
        {
            ScreenTargetKind.AllDisplays => CombineBounds(targetDisplays.Select(display => display.Bounds)),
            ScreenTargetKind.ActiveWindow or ScreenTargetKind.NamedWindow => targetWindow?.Bounds
                ?? throw new InvalidOperationException("The requested window could not be resolved for capture."),
            _ => targetDisplays[0].Bounds
        };

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException("The requested capture target did not resolve to visible bounds.");
        }

        var captureDirectory = ResolveCaptureDirectory();
        Directory.CreateDirectory(captureDirectory);
        // File name looks like screen-primary-20260101-120000.png.
        var filePath = Path.Combine(captureDirectory, BuildCaptureFileName(request, targetDisplays, targetWindow));

        // Copy the pixels from the screen into a bitmap (GDI+ CopyFromScreen), then save it.
        using (var bitmap = new Bitmap(bounds.Width, bounds.Height))
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
            bitmap.Save(filePath, ImageFormat.Png);
        }

        // Keep only the newest few screenshots.
        PruneCaptureDirectory(captureDirectory);

        return new CapturedScreen(
            request,
            filePath,
            bounds,
            displays,
            targetDisplays,
            activeWindow,
            targetWindow,
            BuildTargetLabel(request, targetDisplays, targetWindow));
    }

    // Text summary of a capture (file, target, bounds, displays, windows) with the extra detail line.
    private ToolResult BuildCaptureResult(CapturedScreen capture, string detail, bool succeeded)
    {
        var lines = new List<string>
        {
            $"Capture saved: {capture.FilePath}",
            $"- Target: {capture.TargetLabel}",
            $"- Bounds: {capture.Bounds.Left},{capture.Bounds.Top} {capture.Bounds.Width}x{capture.Bounds.Height}",
            $"- Displays detected: {capture.AllDisplays.Length}",
            $"- Active window: {(capture.ActiveWindow is null ? "Unavailable" : $"{capture.ActiveWindow.ProcessName} (PID {capture.ActiveWindow.ProcessId}) | {capture.ActiveWindow.Title}")}",
            $"- Target window: {(capture.TargetWindow is null ? "N/A" : $"{capture.TargetWindow.ProcessName} (PID {capture.TargetWindow.ProcessId}) | {capture.TargetWindow.Title}")}",
            $"- Detail: {detail}"
        };

        return new ToolResult(
            string.Join(Environment.NewLine, lines),
            Succeeded: succeeded,
            VerificationText: $"Saved a screenshot for {capture.TargetLabel} to {capture.FilePath}.",
            SummaryText: $"Captured {capture.TargetLabel}.");
    }

    // Use the folder from settings, or data\captures, resolved to Jarvis's data folder.
    private string ResolveCaptureDirectory()
    {
        return JarvisDataPaths.Resolve(string.IsNullOrWhiteSpace(_options.VisualCaptureDirectory)
            ? Path.Combine("data", "captures")
            : _options.VisualCaptureDirectory);
    }

    // Turn the user's words into a ScreenRequest: which target, which display or window, and what question to ask about it.
    private static ScreenRequest ParseScreenRequest(string input)
    {
        var trimmed = input.Trim();
        var (payload, explicitQuestion) = ParsePayload(trimmed);
        var target = DetectScreenTarget(payload);
        var question = string.IsNullOrWhiteSpace(explicitQuestion)
            ? ExtractScreenQuestion(payload, target)
            : explicitQuestion.Trim();

        return new ScreenRequest(target.Kind, target.DisplayIndex, target.WindowTarget, question, trimmed);
    }

    // Finds the target from phrases: "all displays"/"entire desktop", "primary monitor", "active display", "active window", "window <name>", "app <name>", "display 2"/"monitor 2"/"screen 2"; otherwise the active display.
    private static ScreenTargetRequest DetectScreenTarget(string input)
    {
        var trimmed = input.Trim();
        var normalized = NormalizeForMatching(input);

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new ScreenTargetRequest(ScreenTargetKind.ActiveDisplay, 0, string.Empty, string.Empty);
        }

        // Phrases that mean every monitor.
        if (normalized.Contains("all displays", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("all monitors", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("whole desktop", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("entire desktop", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("virtual screen", StringComparison.OrdinalIgnoreCase))
        {
            return new ScreenTargetRequest(ScreenTargetKind.AllDisplays, 0, string.Empty, trimmed);
        }

        // Phrases that mean the primary (main) monitor.
        if (normalized.Contains("primary display", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("primary monitor", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("main display", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("main monitor", StringComparison.OrdinalIgnoreCase))
        {
            return new ScreenTargetRequest(ScreenTargetKind.PrimaryDisplay, 0, string.Empty, trimmed);
        }

        // Phrases that mean whichever monitor holds the active window.
        if (normalized.Contains("active display", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("active monitor", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("current display", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("current monitor", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("focused display", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("focused monitor", StringComparison.OrdinalIgnoreCase))
        {
            return new ScreenTargetRequest(ScreenTargetKind.ActiveDisplay, 0, string.Empty, trimmed);
        }

        // Phrases that mean the foreground window.
        if (StartsWithCommand(trimmed, "active window")
            || StartsWithCommand(trimmed, "current window")
            || StartsWithCommand(trimmed, "focused window")
            || StartsWithCommand(trimmed, "foreground window"))
        {
            return new ScreenTargetRequest(ScreenTargetKind.ActiveWindow, 0, string.Empty, trimmed);
        }

        // "window chrome" captures the window that best matches "chrome".
        if (StartsWithCommand(trimmed, "window"))
        {
            var windowTarget = TrimCommandPrefix(trimmed, "window");
            return new ScreenTargetRequest(
                string.IsNullOrWhiteSpace(windowTarget) ? ScreenTargetKind.ActiveWindow : ScreenTargetKind.NamedWindow,
                0,
                windowTarget,
                trimmed);
        }

        // "app chrome" works the same way as "window chrome".
        if (StartsWithCommand(trimmed, "app"))
        {
            var windowTarget = TrimCommandPrefix(trimmed, "app");
            return new ScreenTargetRequest(
                string.IsNullOrWhiteSpace(windowTarget) ? ScreenTargetKind.ActiveWindow : ScreenTargetKind.NamedWindow,
                0,
                windowTarget,
                trimmed);
        }

        // Matches "display 2", "monitor2" or "screen 3": the word, optional spaces, then a number, capturing the number.
        var match = Regex.Match(normalized, @"\b(?:display|monitor|screen)\s*(\d+)\b", RegexOptions.IgnoreCase);

        if (match.Success && int.TryParse(match.Groups[1].Value, out var displayIndex) && displayIndex > 0)
        {
            return new ScreenTargetRequest(ScreenTargetKind.SpecificDisplay, displayIndex, string.Empty, trimmed);
        }

        return new ScreenTargetRequest(ScreenTargetKind.ActiveDisplay, 0, string.Empty, string.Empty);
    }

    // The question is meant to be whatever is left of the text after the part that named the target (for example "monitor 2 what is open" gives "what is open").
    private static string ExtractScreenQuestion(string payload, ScreenTargetRequest target)
    {
        if (!string.IsNullOrWhiteSpace(target.ConsumedInput))
        {
            var remainder = payload.Length >= target.ConsumedInput.Length
                ? payload[target.ConsumedInput.Length..].TrimStart(' ', ',', ':', ';', '-')
                : string.Empty;

            if (!string.IsNullOrWhiteSpace(remainder))
            {
                return remainder;
            }
        }

        return LooksLikeTargetOnly(payload) ? string.Empty : payload.Trim();
    }

    // True when the text only names a target and asks nothing ("screen", "all monitors", "window 2"), so no custom question should be sent.
    private static bool LooksLikeTargetOnly(string input)
    {
        var normalized = NormalizeForMatching(input);

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return true;
        }

        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "screen",
            "screens",
            "display",
            "displays",
            "monitor",
            "monitors",
            "window",
            "windows",
            "app",
            "all",
            "primary",
            "main",
            "active",
            "current",
            "focused",
            "foreground",
            "capture",
            "screenshot",
            "ocr",
            "text",
            "ui",
            "elements"
        };

        return tokens.All(token => allowed.Contains(token) || int.TryParse(token, out _));
    }

    // Windows' list of monitors, numbered from 1.
    private static DisplayDescriptor[] GetDisplays()
    {
        return Screen.AllScreens
            .Select((screen, index) => new DisplayDescriptor(index + 1, screen.DeviceName, screen.Primary, screen.Bounds, screen.WorkingArea))
            .OrderBy(display => display.Index)
            .ToArray();
    }

    // Which monitors to capture. For a window: every monitor it touches. Otherwise by target kind: all, the primary, a numbered one (an error if it does not exist), or the one holding the active window.
    private static DisplayDescriptor[] ResolveTargetDisplays(
        ScreenRequest request,
        IReadOnlyList<DisplayDescriptor> displays,
        ActiveWindowSnapshot? activeWindow,
        ActiveWindowSnapshot? targetWindow)
    {
        if (request.TargetKind is ScreenTargetKind.ActiveWindow or ScreenTargetKind.NamedWindow)
        {
            if (targetWindow is not null)
            {
                var windowDisplays = displays
                    .Where(display => display.Bounds.IntersectsWith(targetWindow.Bounds) || display.Bounds.Contains(targetWindow.Bounds.Location))
                    .ToArray();

                if (windowDisplays.Length > 0)
                {
                    return windowDisplays;
                }
            }

            // Fallbacks if the window is not on any monitor: the active monitor, then the primary, then the first.
            return [ResolveActiveDisplay(displays, activeWindow) ?? displays.FirstOrDefault(display => display.IsPrimary) ?? displays[0]];
        }

        return request.TargetKind switch
        {
            ScreenTargetKind.AllDisplays => displays.ToArray(),
            ScreenTargetKind.PrimaryDisplay => [displays.FirstOrDefault(display => display.IsPrimary) ?? displays[0]],
            ScreenTargetKind.SpecificDisplay => [displays.FirstOrDefault(display => display.Index == request.DisplayIndex)
                ?? throw new InvalidOperationException($"Display {request.DisplayIndex} was not found. Use `screen monitors` to inspect the current display list.")],
            _ => [ResolveActiveDisplay(displays, activeWindow) ?? displays.FirstOrDefault(display => display.IsPrimary) ?? displays[0]]
        };
    }

    // Which window to capture: the foreground window, or the best match for a name, or none for display captures.
    private static ActiveWindowSnapshot? ResolveTargetWindow(ScreenRequest request, ActiveWindowSnapshot? activeWindow)
    {
        return request.TargetKind switch
        {
            ScreenTargetKind.ActiveWindow => activeWindow,
            ScreenTargetKind.NamedWindow => string.IsNullOrWhiteSpace(request.WindowTarget) ? null : TryFindWindowSnapshot(request.WindowTarget),
            _ => null
        };
    }

    // Which monitor is the user working on: the one the foreground window sits on, otherwise the one with the mouse cursor.
    private static DisplayDescriptor? ResolveActiveDisplay(
        IReadOnlyList<DisplayDescriptor> displays,
        ActiveWindowSnapshot? activeWindow)
    {
        if (activeWindow is not null)
        {
            foreach (var display in displays)
            {
                if (display.Bounds.IntersectsWith(activeWindow.Bounds)
                    || display.Bounds.Contains(activeWindow.Bounds.Location))
                {
                    return display;
                }
            }
        }

        var cursor = Cursor.Position;
        return displays.FirstOrDefault(display => display.Bounds.Contains(cursor));
    }

    // The smallest rectangle containing all the given rectangles (used for "all displays").
    private static Rectangle CombineBounds(IEnumerable<Rectangle> rectangles)
    {
        var bounds = Rectangle.Empty;

        foreach (var rectangle in rectangles)
        {
            bounds = bounds == Rectangle.Empty
                ? rectangle
                : Rectangle.Union(bounds, rectangle);
        }

        return bounds;
    }

    // Build the screenshot's file name from the target and the current time.
    private static string BuildCaptureFileName(
        ScreenRequest request,
        IReadOnlyList<DisplayDescriptor> targetDisplays,
        ActiveWindowSnapshot? targetWindow)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var label = request.TargetKind switch
        {
            ScreenTargetKind.AllDisplays => "all-displays",
            ScreenTargetKind.PrimaryDisplay => "primary",
            ScreenTargetKind.SpecificDisplay => $"display-{request.DisplayIndex}",
            ScreenTargetKind.ActiveWindow => "active-window",
            ScreenTargetKind.NamedWindow => "window-" + SanitizeFileSegment(
                string.IsNullOrWhiteSpace(targetWindow?.ProcessName)
                    ? request.WindowTarget
                    : $"{targetWindow.ProcessName}-{targetWindow.Title}"),
            _ => targetDisplays.Count > 0 ? $"display-{targetDisplays[0].Index}" : "active"
        };

        return $"screen-{label}-{timestamp}.png";
    }

    // Short human-readable name of what was captured, for messages.
    private static string BuildTargetLabel(
        ScreenRequest request,
        IReadOnlyList<DisplayDescriptor> targetDisplays,
        ActiveWindowSnapshot? targetWindow)
    {
        if (request.TargetKind == ScreenTargetKind.AllDisplays)
        {
            return $"all displays ({targetDisplays.Count})";
        }

        if (request.TargetKind is ScreenTargetKind.ActiveWindow or ScreenTargetKind.NamedWindow)
        {
            if (targetWindow is null)
            {
                return request.TargetKind == ScreenTargetKind.ActiveWindow
                    ? "active window"
                    : $"window {request.WindowTarget}";
            }

            var title = string.IsNullOrWhiteSpace(targetWindow.Title) ? targetWindow.ProcessName : targetWindow.Title;
            return request.TargetKind == ScreenTargetKind.ActiveWindow
                ? $"active window ({title})"
                : $"window {title} [{targetWindow.ProcessName}]";
        }

        var display = targetDisplays.FirstOrDefault();

        if (display is null)
        {
            return "display";
        }

        return request.TargetKind switch
        {
            ScreenTargetKind.PrimaryDisplay => $"primary display {display.Index} ({display.DeviceName})",
            ScreenTargetKind.SpecificDisplay => $"display {display.Index} ({display.DeviceName})",
            _ => $"active display {display.Index} ({display.DeviceName})"
        };
    }

    // Facts about the capture (target, size, monitors, windows) put in front of the vision model's question.
    private string BuildCapturePromptContext(CapturedScreen capture)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Capture target: {capture.TargetLabel}");
        builder.AppendLine($"Capture bounds: {capture.Bounds.Left},{capture.Bounds.Top} {capture.Bounds.Width}x{capture.Bounds.Height}");
        builder.AppendLine($"Displays detected: {capture.AllDisplays.Length}");

        foreach (var display in capture.TargetDisplays)
        {
            builder.AppendLine($"Target display: {DescribeDisplay(display)}");
        }

        if (capture.ActiveWindow is not null)
        {
            builder.AppendLine(
                $"Active window: {capture.ActiveWindow.ProcessName} (PID {capture.ActiveWindow.ProcessId}) | {capture.ActiveWindow.Title} | bounds {capture.ActiveWindow.Bounds.Left},{capture.ActiveWindow.Bounds.Top} {capture.ActiveWindow.Bounds.Width}x{capture.ActiveWindow.Bounds.Height}");
        }
        else
        {
            builder.AppendLine("Active window: unavailable");
        }

        if (capture.TargetWindow is not null)
        {
            builder.AppendLine(
                $"Target window: {capture.TargetWindow.ProcessName} (PID {capture.TargetWindow.ProcessId}) | {capture.TargetWindow.Title} | bounds {capture.TargetWindow.Bounds.Left},{capture.TargetWindow.Bounds.Top} {capture.TargetWindow.Bounds.Width}x{capture.TargetWindow.Bounds.Height}");
        }

        return builder.ToString().TrimEnd();
    }

    // One-line description of a monitor: number, device name, bounds and work area (the area not covered by the taskbar).
    private static string DescribeDisplay(DisplayDescriptor display)
    {
        var primary = display.IsPrimary ? " | primary" : string.Empty;
        return $"Display {display.Index}{primary} | {display.DeviceName} | bounds {display.Bounds.Left},{display.Bounds.Top} {display.Bounds.Width}x{display.Bounds.Height} | work area {display.WorkingArea.Left},{display.WorkingArea.Top} {display.WorkingArea.Width}x{display.WorkingArea.Height}";
    }

    // Make text safe for a file name: lower-case, bad characters and spaces become "-", repeated dashes collapse, at most 40 characters.
    private static string SanitizeFileSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "window";
        }

        var invalidChars = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value
            .ToLowerInvariant()
            .Select(character => invalidChars.Contains(character) || char.IsWhiteSpace(character) ? '-' : character)
            .ToArray());

        // "-{2,}" matches two or more dashes in a row, e.g. "a---b" becomes "a-b".
        sanitized = Regex.Replace(sanitized, "-{2,}", "-", RegexOptions.CultureInvariant).Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "window" : sanitized[..Math.Min(40, sanitized.Length)];
    }

    // Describe the foreground window (handle, program, process id, title, position), or null if it cannot be read.
    private static ActiveWindowSnapshot? TryGetActiveWindowSnapshot()
    {
        try
        {
            var handle = GetForegroundWindow();

            if (handle == IntPtr.Zero)
            {
                return null;
            }

            _ = GetWindowThreadProcessId(handle, out var processId);

            if (processId == 0)
            {
                return null;
            }

            var title = ReadWindowTitle(handle);
            var bounds = GetWindowBounds(handle);
            using var process = Process.GetProcessById((int)processId);
            return new ActiveWindowSnapshot(handle, process.ProcessName, (int)processId, title, bounds);
        }
        catch
        {
            return null;
        }
    }

    // Find the visible window whose title or program name best matches the words in query, using ScoreWindowMatch; the highest score wins.
    private static ActiveWindowSnapshot? TryFindWindowSnapshot(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var normalizedQuery = NormalizeForMatching(query);
        ActiveWindowSnapshot? bestMatch = null;
        var bestScore = 0;

        // Only windows that belong to a program's main window and have a title are considered.
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.HasExited)
                {
                    continue;
                }

                var handle = process.MainWindowHandle;
                var title = process.MainWindowTitle;

                if (handle == IntPtr.Zero || string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var score = ScoreWindowMatch(query, normalizedQuery, title, process.ProcessName);

                if (score <= bestScore)
                {
                    continue;
                }

                bestScore = score;
                bestMatch = new ActiveWindowSnapshot(handle, process.ProcessName, process.Id, title, GetWindowBounds(handle));
            }
            // Some processes deny access; skip them.
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return bestMatch;
    }

    // Scores how well a window matches the query (0 = no match): exact title 120, program 110, normalized 100 and 96, then "contains" title 82, program 74. Only the ranking order matters.
    private static int ScoreWindowMatch(string rawQuery, string normalizedQuery, string title, string processName)
    {
        var normalizedTitle = NormalizeForMatching(title);
        var normalizedProcessName = NormalizeForMatching(processName);
        var score = 0;

        if (string.Equals(title, rawQuery, StringComparison.OrdinalIgnoreCase))
        {
            score = Math.Max(score, 120);
        }

        if (string.Equals(processName, rawQuery, StringComparison.OrdinalIgnoreCase))
        {
            score = Math.Max(score, 110);
        }

        if (string.Equals(normalizedTitle, normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            score = Math.Max(score, 100);
        }

        if (string.Equals(normalizedProcessName, normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            score = Math.Max(score, 96);
        }

        if (normalizedTitle.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            score = Math.Max(score, 82);
        }

        if (normalizedProcessName.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            score = Math.Max(score, 74);
        }

        return score;
    }

    // The window's rectangle on screen, or empty if Windows cannot give it.
    private static Rectangle GetWindowBounds(IntPtr handle)
    {
        return GetWindowRect(handle, out var rect)
            ? Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom)
            : Rectangle.Empty;
    }

    // Read a window's title text via the Win32 API.
    private static string ReadWindowTitle(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);

        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    // Delete screenshots beyond the newest N (from settings, at least 1). Errors are ignored because cleanup is optional.
    private void PruneCaptureDirectory(string captureDirectory)
    {
        var retentionCount = Math.Max(1, _options.VisualCaptureRetentionCount);

        try
        {
            var files = new DirectoryInfo(captureDirectory)
                .EnumerateFiles("screen-*.png", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(retentionCount)
                .ToArray();

            foreach (var file in files)
            {
                file.Delete();
            }
        }
        catch
        {
        }
    }

    // Turn a user-supplied path into a full path: strip quotes, expand a leading "~" to the user's home folder, and treat relative paths as relative to the workspace folder.
    private string ResolvePath(string target)
    {
        var trimmed = target.Trim().Trim('"');

        if (Path.IsPathRooted(trimmed))
        {
            return Path.GetFullPath(trimmed);
        }

        if (trimmed.StartsWith('~'))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            trimmed = Path.Combine(home, trimmed[1..].TrimStart('\\', '/'));
        }

        return Path.GetFullPath(Path.Combine(_workspaceRoot, trimmed));
    }

    // Split "path :: question" at the first double colon. Without "::" the whole text is the path (or target) and the question is empty.
    private static (string Path, string Question) ParsePayload(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return (string.Empty, string.Empty);
        }

        var parts = input.Split("::", 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 ? (parts[0], parts[1]) : (input.Trim(), string.Empty);
    }

    // Build the "data:image/png;base64,..." text. Unknown types are labelled PNG (screenshots and converted TIFFs are PNG).
    private static string BuildDataUrl(string filePath, byte[] bytes)
    {
        var mimeType = Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            _ => "image/png"
        };

        return $"data:{mimeType};base64,{Convert.ToBase64String(bytes)}";
    }

    // Standard failure results used by the methods above.
    private static ToolResult Unavailable(string message)
    {
        return new ToolResult(
            message,
            Succeeded: false,
            VerificationText: "No routed model provider is available for visual understanding.",
            SummaryText: "Visual understanding is unavailable.");
    }

    private static ToolResult Failed(string heading, string error, string verification)
    {
        return new ToolResult(
            $"{heading} {TrimError(error)}",
            Succeeded: false,
            VerificationText: verification,
            SummaryText: heading);
    }

    // The first sentence (up to the first . ! or ?), or the first 160 characters if there is no punctuation. Used for spoken summaries.
    private static string FirstSentence(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.ReplaceLineEndings(" ").Trim();
        var punctuationIndex = normalized.IndexOfAny(['.', '!', '?']);

        if (punctuationIndex <= 0)
        {
            return normalized.Length <= 160 ? normalized : normalized[..160] + "...";
        }

        return normalized[..(punctuationIndex + 1)];
    }

    // Shorten an error message to 220 characters.
    private static string TrimError(string text)
    {
        const int maxLength = 220;
        var value = text.Trim();
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    // Lower-case, replace punctuation with spaces and collapse repeated spaces, so "Google Chrome - Home" and "google chrome home" compare equal.
    private static string NormalizeForMatching(string value)
    {
        var chars = value
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) || char.IsWhiteSpace(character) ? character : ' ')
            .ToArray();

        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    // True if the input is exactly the command word or starts with it followed by a space (so "window" matches "window chrome" but not "windows").
    private static bool StartsWithCommand(string input, string value)
    {
        return input.Equals(value, StringComparison.OrdinalIgnoreCase)
            || input.StartsWith(value + " ", StringComparison.OrdinalIgnoreCase);
    }

    // Remove the command word and any leading separators (space, comma, colon, semicolon, dash).
    private static string TrimCommandPrefix(string input, params string[] commands)
    {
        foreach (var command in commands)
        {
            if (StartsWithCommand(input, command))
            {
                return input[command.Length..].TrimStart(' ', ',', ':', ';', '-');
            }
        }

        return input.Trim();
    }

    // Read model-returned text as JSON of type T. Returns null if it is empty or not valid, so callers can fall back to showing the raw text.
    private static T? ParseJson<T>(string content) where T : class
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var candidate = ExtractJson(content);

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(candidate, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    // Models often wrap JSON in a markdown code fence or add chatter. Remove a leading code fence, then keep only the text from the first { to the last }.
    private static string ExtractJson(string content)
    {
        var trimmed = content.Trim();

        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBreak = trimmed.IndexOf('\n');

            if (firstBreak >= 0)
            {
                trimmed = trimmed[(firstBreak + 1)..];
                var fenceIndex = trimmed.LastIndexOf("```", StringComparison.Ordinal);

                if (fenceIndex >= 0)
                {
                    trimmed = trimmed[..fenceIndex];
                }
            }
        }

        var firstBrace = trimmed.IndexOf('{');
        var lastBrace = trimmed.LastIndexOf('}');

        return firstBrace >= 0 && lastBrace > firstBrace
            ? trimmed[firstBrace..(lastBrace + 1)]
            : trimmed;
    }

    // The primary text if it has any, otherwise the fallback.
    private static string Coalesce(string? primary, string? fallback)
    {
        return string.IsNullOrWhiteSpace(primary)
            ? (fallback ?? string.Empty)
            : primary.Trim();
    }

    // Win32 calls from user32.dll: foreground window, title, owning process and screen rectangle.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    // Win32 RECT (left, top, right, bottom in screen pixels).
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // Data shapes used inside this file. ScreenRequest: what to capture and what to ask.
    private sealed record ScreenRequest(ScreenTargetKind TargetKind, int DisplayIndex, string WindowTarget, string Question, string RawInput);

    // ScreenTargetRequest: the target found in the words, and the text that named it (ConsumedInput).
    private sealed record ScreenTargetRequest(ScreenTargetKind Kind, int DisplayIndex, string WindowTarget, string ConsumedInput);

    // One monitor: 1-based number, device name, whether it is the primary, its full bounds and its work area.
    private sealed record DisplayDescriptor(int Index, string DeviceName, bool IsPrimary, Rectangle Bounds, Rectangle WorkingArea);

    // A window: handle, program name, process id, title and position.
    private sealed record ActiveWindowSnapshot(IntPtr Handle, string ProcessName, int ProcessId, string Title, Rectangle Bounds);

    // The result of a capture: the request, the saved file, the captured rectangle, all monitors, the captured monitors, and the active/target windows.
    private sealed record CapturedScreen(
        ScreenRequest Request,
        string FilePath,
        Rectangle Bounds,
        DisplayDescriptor[] AllDisplays,
        DisplayDescriptor[] TargetDisplays,
        ActiveWindowSnapshot? ActiveWindow,
        ActiveWindowSnapshot? TargetWindow,
        string TargetLabel);

    // The JSON shapes the model is asked to return for "screen ocr", "screen ui" and document analysis. Property names must match the keys named in the prompts above.
    private sealed record OcrExtractionContract(string Summary, string FullText, OcrTextBlockContract[] TextBlocks);

    private sealed record OcrTextBlockContract(string Text, string Region, string Purpose);

    private sealed record UiDetectionContract(string Summary, string ApplicationGuess, UiElementContract[] UiElements);

    private sealed record UiElementContract(string Label, string Role, string Text, string State, string Region, string Importance);

    private sealed record DocumentAnalysisContract(
        string Summary,
        string DocumentType,
        string Confidence,
        string ExtractedTextPreview,
        string Caveats,
        string[] KeyPoints,
        string[] ActionItems,
        DocumentSectionContract[] Sections,
        DocumentFieldContract[] KeyFields);

    private sealed record DocumentSectionContract(string Heading, string Purpose);

    private sealed record DocumentFieldContract(string Label, string Value);

    // What a screen request points at.
    private enum ScreenTargetKind
    {
        ActiveDisplay,
        PrimaryDisplay,
        AllDisplays,
        SpecificDisplay,
        ActiveWindow,
        NamedWindow
    }
}
