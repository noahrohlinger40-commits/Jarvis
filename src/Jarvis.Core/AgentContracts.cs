// Shared plain data types (enums and records) that describe how a request is classified, what the assistant is doing
// (activity events for the UI), and what is saved for a background session. No logic lives here.
namespace Jarvis.Core;

// The route a request takes: run one tool directly, ask a clarifying question, answer simply,
// plan with several tool calls, research, look at the screen, or run in the background.
public enum AgentIntentKind
{
    DirectTool,
    Clarification,
    SimpleReply,
    Planner,
    Research,
    Visual,
    Background
}

// The kind of progress message sent to the UI while a request is being worked on.
public enum AssistantActivityKind
{
    Status,
    Classification,
    Planning,
    ToolStart,
    ToolComplete,
    Reflection,
    Clarification,
    Background,
    Summary,
    Warning,
    Error
}

// A source link shown with an answer.
public sealed record AssistantCitation(string Title, string Url, string Snippet = "");

// Record of one tool run: what was called, with what input, the result, and whether it was checked afterwards.
public sealed record AssistantToolExecution(
    string ToolName,
    string Input,
    string Summary,
    bool Succeeded,
    string VerificationText,
    string OutputText,
    string VerificationStatus = "",
    string VerificationSummary = "");

// One step of a plan and its current status.
public sealed record PlanStepState(string Title, string Status, string Detail);

// Everything saved about a long-running or resumable session so it can be shown or continued later.
public sealed record PersistedAgentSession(
    string SessionId,
    string Status,
    AgentIntentKind Intent,
    string OriginalRequest,
    string LatestInput,
    string FollowUpQuestion,
    string LatestResponse,
    bool RunInBackground,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    PlanStepState[] Steps,
    AssistantToolExecution[] ToolResults,
    AssistantCitation[] Citations);

// One progress message for the UI. IsTransient means it can be replaced by the next message rather than kept in the log.
public sealed record AssistantActivityEvent(
    AssistantActivityKind Kind,
    string Message,
    string SessionId = "",
    bool IsTransient = false,
    string Detail = "");

// Event payload wrapper so progress messages can travel through a normal C# event.
public sealed class AssistantActivityEventArgs : EventArgs
{
    public AssistantActivityEventArgs(AssistantActivityEvent activity)
    {
        Activity = activity;
    }

    public AssistantActivityEvent Activity { get; }
}
