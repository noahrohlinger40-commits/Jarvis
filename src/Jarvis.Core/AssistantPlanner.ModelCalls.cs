// One slice of the StructuredAssistantPlanner class (see AssistantPlanner.cs): the single place where the planner talks
// to the language model, with a small retry wrapper. Every other slice calls ExecuteModelCallAsync instead of the
// model gateway directly, so retry behaviour is the same everywhere.
namespace Jarvis.Core;

internal sealed partial class StructuredAssistantPlanner
{
    // Sends one request to the model and retries on ordinary failures, then rethrows the last error.
    // "operation" is only a label for the warning messages shown in the UI.
    private async Task<ChatCompletionResponse> ExecuteModelCallAsync(
        ChatCompletionRequest request,
        string operation,
        string sessionId,
        IProgress<AssistantActivityEvent>? activity,
        ModelRoute route,
        CancellationToken cancellationToken)
    {
        // PlannerRetryCount is the number of extra tries, so the total is that plus the first attempt (never fewer than one).
        var totalAttempts = Math.Max(1, _options.PlannerRetryCount + 1);
        Exception? lastError = null;

        for (var attempt = 1; attempt <= totalAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await _modelGateway.CompleteAsync(request, route, cancellationToken);
            }
            // A model timeout is not retried: each attempt would hold the shared model slot for the full timeout again.
            catch (Exception exception) when (exception is not OperationCanceledException and not TimeoutException)
            {
                lastError = exception;

                if (attempt >= totalAttempts)
                {
                    break;
                }

                activity?.Report(new AssistantActivityEvent(
                    AssistantActivityKind.Warning,
                    $"Model call for {operation} failed. Retrying {attempt} of {totalAttempts - 1}.",
                    sessionId,
                    Detail: TrimError(exception.Message)));

                // Waits a little longer after each failure (200 ms, then 400 ms, ...) before trying again.
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken);
            }
        }

        throw lastError ?? new InvalidOperationException($"The model call for {operation} failed.");
    }
}
