// JarvisMainForm.Runtime.cs: everything that happens while the window runs: refresh and reminders, prompts, audio pickers,
// saving settings, voice turns, approvals and status display. Sections are marked with "-----" lines. UI-thread rule:
// background callbacks (voice, assistant, speaker, tutor) must BeginInvoke to touch controls; clicks and timer ticks can use them directly.
using System.Drawing;
using System.Media;
using System.Text;
using System.Windows.Forms;
using Jarvis.App;
using Jarvis.Core;
using NAudio.Wave;


namespace Jarvis.App;

internal sealed partial class JarvisMainForm
{
    // ----- Periodic refresh and reminders -----
    // Fires every 1.4 s (refresh timer). Re-reads assistant state to update the screen, then announces due reminders.
    // The _isRefreshingUiState flag stops a slow refresh from overlapping the next tick. async void is required for an event handler,
    // so every exception is swallowed inside rather than crashing the app.
    private async void OnRefreshTimerTick(object? sender, EventArgs eventArgs)
    {
        if (_isRefreshingUiState || _cancellationTokenSource.IsCancellationRequested)
        {
            return;
        }

        _isRefreshingUiState = true;

        try
        {
            await RefreshRuntimeStateAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }

        // Guarded on its own, so a failing screen refresh can never silence reminders.
        try
        {
            await AnnounceDueRemindersAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
        finally
        {
            _isRefreshingUiState = false;
        }
    }

    // Speaks and shows any reminder whose time has arrived, then marks it completed.
    private async Task AnnounceDueRemindersAsync()
    {
        // Waits for a quiet moment so a reminder never talks over a reply.
        if (_isSubmitting || _context.Speaker.CurrentStatus.IsSpeaking)
        {
            return;
        }

        var token = _cancellationTokenSource.Token;
        var nowUtc = DateTimeOffset.UtcNow;
        var due = (await _context.Assistant.GetPendingRemindersAsync(token))
            .Where(note => note.ReminderAtUtc <= nowUtc)
            .ToList();

        if (due.Count == 0)
        {
            return;
        }

        // Marked done before speaking, so a failed announcement can't repeat on every tick.
        foreach (var note in due)
        {
            await _context.Assistant.UpdateMemoryAsync(
                new MemoryUpdateRequest(
                    note.Id, note.Content, note.Kind, note.Category, note.Privacy, note.Tags ?? [], note.ImportanceScore,
                    note.RetentionPolicy, note.ExpiresAtUtc, note.UserApprovedRetention, note.ReminderAtUtc, note.ReminderText,
                    "completed", note.Entities),
                token);
        }

        var items = due.Select(note => (string.IsNullOrWhiteSpace(note.ReminderText) ? note.Content : note.ReminderText).TrimEnd('.'));
        var message = $"Reminder: {string.Join("; ", items)}.";
        AppendMessage(_currentOptions.AssistantName.ToUpperInvariant(), message, Color.FromArgb(106, 220, 197));
        // Not awaited, so the rest of the screen keeps refreshing while it speaks.
        _ = TrySpeakAsync(message, token);
    }

    // Pulls fresh data from the assistant for each part of the screen.
    private async Task RefreshRuntimeStateAsync()
    {
        await RefreshPendingApprovalUiAsync();
        await RefreshClarificationUiAsync();
        await RefreshSessionSnapshotAsync();
        await RefreshBackgroundSummaryAsync();
        await RefreshMissionBriefAsync();
    }

    // Shows or hides the Approve/Deny card depending on whether an approval is pending.
    private async Task RefreshPendingApprovalUiAsync()
    {
        var pendingApproval = await _context.Assistant.GetPendingApprovalAsync(_cancellationTokenSource.Token);
        UpdateApprovalUi(pendingApproval);
    }

    // If Jarvis asked a follow-up question, shows it in the response box and logs it once (the snapshot key avoids logging the same question on every tick).
    private async Task RefreshClarificationUiAsync()
    {
        var session = await _context.Assistant.GetPendingClarificationSessionAsync(_cancellationTokenSource.Token);

        if (session is null)
        {
            if (!_isSubmitting && _pendingVoiceClarification is null)
            {
                _isAwaitingFollowUpInput = false;
                _awaitingFollowUpDetail = string.Empty;
            }

            _lastClarificationSnapshotKey = string.Empty;
            return;
        }

        _activeSessionId = session.SessionId;
        _isAwaitingFollowUpInput = true;
        _awaitingFollowUpDetail = session.FollowUpQuestion;

        if (!_isSubmitting
            && !_isAssistantResponseStreaming
            && _pendingVoiceClarification is null)
        {
            var promptEcho = $"FOLLOW-UP REQUIRED  |  {TrimForSingleLine(session.FollowUpQuestion, 124)}";
            var livePacket = BuildClarificationConsoleText(session);

            if (!string.Equals(_promptEchoLabel.Text, promptEcho, StringComparison.Ordinal))
            {
                _promptEchoLabel.Text = promptEcho;
            }

            if (!string.Equals(_liveResponseBox.Text, livePacket, StringComparison.Ordinal))
            {
                _liveResponseBox.Text = livePacket;
            }
        }

        var snapshotKey = BuildClarificationSnapshotKey(session);

        if (string.Equals(snapshotKey, _lastClarificationSnapshotKey, StringComparison.Ordinal))
        {
            return;
        }

        _lastClarificationSnapshotKey = snapshotKey;

        AppendRuntimeMessage("Follow-up required before Jarvis can continue the current workflow.");
        AppendTimelineEntry(
            "CLARIFICATION",
            TrimForSingleLine(session.FollowUpQuestion, 180),
            BuildClarificationWhy(session),
            Color.FromArgb(255, 210, 132));
        AddActivityCard(
            "Clarification",
            "Awaiting operator input",
            TrimForSingleLine(session.FollowUpQuestion, 108),
            Color.FromArgb(255, 210, 132));
    }

    // Updates the "Plan and result" box for the active session.
    private async Task RefreshSessionSnapshotAsync()
    {
        if (string.IsNullOrWhiteSpace(_activeSessionId))
        {
            _sessionBox.Text = _lastSessionSummary;
            return;
        }

        var session = await _context.Assistant.GetSessionAsync(_activeSessionId, _cancellationTokenSource.Token);

        if (session is null)
        {
            _activeSessionId = string.Empty;
            _sessionBox.Text = _lastSessionSummary;
            return;
        }

        _sessionBox.Text = BuildSessionSummary(session);
    }

    // Shows up to three queued background sessions in the activity card.
    private async Task RefreshBackgroundSummaryAsync()
    {
        var backgroundSessions = await _context.Assistant.GetBackgroundSessionsAsync(_cancellationTokenSource.Token);

        if (backgroundSessions.Count == 0)
        {
            _backgroundLabel.Text = "Background queue clear.";
            return;
        }

        _backgroundLabel.Text = string.Join(
            "  |  ",
            backgroundSessions
                .Take(3)
                .Select(session => $"{TrimForSingleLine(session.OriginalRequest, 34)} [{session.Status}]"));
    }

    // Rebuilds the mission strip text from a snapshot of the desktop (active window, time). Falls back to a generic strip if that fails.
    private async Task RefreshMissionBriefAsync()
    {
        string nextText;

        try
        {
            var snapshot = await _context.DesktopAutomation.GetContextSnapshotAsync(_cancellationTokenSource.Token);
            nextText = BuildMissionBrief(snapshot);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            nextText = BuildMissionBrief(null);
        }

        _missionBriefText = nextText;

        if (!string.Equals(_missionBriefLabel.Text, nextText, StringComparison.Ordinal))
        {
            _missionBriefLabel.Text = nextText;
        }
    }

    // ----- Prompt processing (typed or spoken text becomes an assistant turn) -----
    // Called by the Send button and Enter key; empty promptOverride means "use the text box".
    private async Task SubmitPromptAsync(string? promptOverride = null)
    {
        var input = promptOverride ?? _inputBox.Text.Trim();
        await ProcessPromptAsync(input, "YOU", promptOverride is null);
    }

    // The single path every request goes through (typed, quick buttons, voice, approvals).
    // source labels who sent it (YOU, VOICE, SURFACE, APPROVAL...). clearInput empties the text box; displayedInput is what to show in chat if different from the real text.
    private async Task ProcessPromptAsync(string input, string source, bool clearInput, string? displayedInput = null)
    {
        // Ignore new input while one is already being processed or the app is closing.
        if (_cancellationTokenSource.IsCancellationRequested || _isSubmitting)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        // A pending low-confidence voice command may consume this text as its confirmation.
        if (await TryResolvePendingVoiceClarificationAsync(input, source, clearInput))
        {
            return;
        }

        // "No, I said..." style corrections to the previous voice command are handled first.
        if (await TryHandleVoiceCorrectionDirectiveAsync(input, source, clearInput))
        {
            return;
        }

        // Mark busy and reset the follow-up state before the model call.
        _isSubmitting = true;
        _isAwaitingFollowUpInput = false;
        _awaitingFollowUpDetail = string.Empty;
        _followUpAnswerDeadlineUtc = DateTimeOffset.MinValue;
        _lastAssistantKind = AssistantActivityKind.Planning;
        _lastAssistantDetail = "Planning the next response.";
        _currentMissionObjective = DescribeMissionObjective(input);
        ClearAssistantStateOverride();
        CompleteAssistantMessageStream();
        SetBusyState(true);
        SetAssistantStateOverride(SurfaceState.Thinking, "Analyzing the directive and preparing the next response.");

        // Show the request on screen, optionally say a short acknowledgement (voice only), then ask the assistant to handle it.
        try
        {
            if (clearInput)
            {
                _inputBox.Clear();
            }

            var renderedInput = displayedInput ?? input;
            await RefreshMissionBriefAsync();
            _promptEchoLabel.Text = $"LATEST DIRECTIVE  |  {TrimForSingleLine(renderedInput, 132)}";
            _assistantStatusLabel.Text = $"Mission acknowledged. {BuildImmediateAcknowledgement(input)}";
            _liveResponseBox.Text = BuildProcessingConsoleText(source, renderedInput, input);
            AppendOperatorMessage(source, renderedInput);
            AppendTimelineEntry("REQUEST", renderedInput, $"{source} routed into the active assistant runtime.", Color.FromArgb(255, 191, 105));

            if (ShouldSpeakVoiceAcknowledgement(source))
            {
                await TrySpeakAsync(BuildVoiceAcknowledgement(input), _cancellationTokenSource.Token);
            }

            // The assistant does the real work here: plans, calls tools, may pause for approval.
            var turn = await _context.Assistant.HandleAsync(input, _cancellationTokenSource.Token);
            _activeSessionId = turn.SessionId;
            _currentMissionObjective = BuildPostTurnObjective(turn, _pendingApproval);
            await RefreshRuntimeStateAsync();

            // Show the answer: speech and the typed reveal run at the same time.
            var renderedResponse = BuildDisplayResponse(turn, _pendingApproval);
            // The reply may need a spoken form different from the written one.
            // Speech starts with the typed reveal, not after it: waiting added 0.25-1.7 s per turn.
            var speechTask = _pendingApproval is not null
                ? TrySpeakAsync("I need your approval before I continue. It's on screen.", _cancellationTokenSource.Token)
                : SpokenReply.Build(renderedResponse, turn.SpeakInFull) is { Length: > 0 } spoken
                    ? TrySpeakAsync(spoken, _cancellationTokenSource.Token)
                    : Task.CompletedTask;
            await StreamLiveResponseAsync(renderedResponse, _cancellationTokenSource.Token);
            ApplyTurnSummary(turn, _pendingApproval, renderedResponse);
            _isAwaitingFollowUpInput = turn.IsFollowUpQuestion;
            _awaitingFollowUpDetail = turn.IsFollowUpQuestion
                ? renderedResponse
                : string.Empty;

            if (turn.IsFollowUpQuestion)
            {
                await RefreshClarificationUiAsync();
            }

            // Wait for speech to finish before the turn counts as done.
            await speechTask;

            if (turn.ShouldExit)
            {
                ExitApplication();
            }
        }
        // Cancelled (app closing or user stop): just close the stream cleanly.
        catch (OperationCanceledException)
        {
            CompleteAssistantMessageStream();
        }
        // Any other failure is shown in the chat, timeline and status instead of crashing.
        catch (Exception exception)
        {
            CompleteAssistantMessageStream();
            ClearAssistantStateOverride();
            var message = $"Runtime fault: {exception.Message}";
            _liveResponseBox.Text = message;
            AppendSystemMessage(message);
            AddActivityCard("Runtime Fault", "Execution error", exception.Message, Color.FromArgb(255, 138, 101));
            AppendTimelineEntry("ERROR", message, "Unhandled exception during prompt processing.", Color.FromArgb(255, 138, 101));
            _lastAssistantKind = AssistantActivityKind.Error;
            _lastAssistantDetail = exception.Message;
            _currentMissionObjective = "Recovering from a runtime fault.";
            UpdateSurfaceState(SurfaceState.Error, TrimForSingleLine(exception.Message, 120));
        }
        // Always runs: unlock the UI, restore idle text, and handle voice follow-ups and queued commands.
        finally
        {
            _isSubmitting = false;
            _isAssistantResponseStreaming = false;
            ClearAssistantStateOverride();
            SetBusyState(false);
            if (_pendingApproval is null && !_isAwaitingFollowUpInput)
            {
                _currentMissionObjective = "Stand by for the next directive.";
            }
            if (!_cancellationTokenSource.IsCancellationRequested)
            {
                await RefreshMissionBriefAsync();
            }
            RecomputeSurfaceState();
            _inputBox.Focus();

            // After speaking a question or approval request, accept the answer without another wake word.
            if (source == "VOICE"
                && (_isAwaitingFollowUpInput || _pendingApproval is not null)
                && !_cancellationTokenSource.IsCancellationRequested)
            {
                _currentVoiceRuntime.ListenForFollowUp();
                _followUpAnswerDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(30);
            }

            // If the user spoke while this turn was running, run that queued command now. BeginInvoke lets this method finish first.
            var queuedVoiceDirective = _queuedVoiceDirective;
            _queuedVoiceDirective = null;

            if (queuedVoiceDirective is not null
                && !_cancellationTokenSource.IsCancellationRequested
                && CanUseUi())
            {
                BeginInvoke(new Action(() =>
                {
                    if (CanUseUi())
                    {
                        _ = RouteQueuedVoiceDirectiveAsync(queuedVoiceDirective);
                    }
                }));
            }
        }
    }

    // Approve/Deny buttons simply send the word "approve" or "deny" through the normal prompt path.
    private async Task HandleApprovalDecisionAsync(bool approved)
    {
        if (_pendingApproval is null || _isSubmitting)
        {
            return;
        }

        await ProcessPromptAsync(
            approved ? "approve" : "deny",
            approved ? "APPROVAL" : "DENIAL",
            clearInput: false,
            displayedInput: approved
                ? "Approved the pending major action from the live control surface."
                : "Denied the pending major action from the live control surface.");
    }

    // Opens the memory editor dialog, then refreshes.
    private async Task OpenMemoryWorkbenchAsync()
    {
        using var form = new MemoryWorkbenchForm(_context.Assistant);
        form.ShowDialog(this);
        await RefreshRuntimeStateAsync();
    }

    // Opens the presence settings dialog and applies the chosen options if the user pressed OK.
    private async Task OpenPresenceWorkbenchAsync()
    {
        using var form = new PresenceWorkbenchForm(_currentOptions);

        if (form.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        await ApplyPresenceSettingsAsync(form.SelectedOptions);
    }

    // Types the reply into the response box and chat history in small chunks so it looks like live typing.
    // A new stream cancels the previous one first.
    private async Task StreamLiveResponseAsync(string message, CancellationToken cancellationToken)
    {
        _liveResponseCts?.Cancel();
        _liveResponseCts?.Dispose();
        _liveResponseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _liveResponseCts.Token;

        _isAssistantResponseStreaming = true;
        _lastAssistantKind = AssistantActivityKind.Summary;
        _lastAssistantDetail = "Streaming the latest response.";
        _assistantStatusLabel.Text = "Streaming the latest response.";
        SetAssistantStateOverride(SurfaceState.Speaking, "Streaming the latest response to the console.");
        BeginAssistantMessageStream();
        _liveResponseBox.Clear();

        try
        {
            // Word-by-word animation of a whole article would take about a minute, so long replies appear at once.
            var chunks = message.Length <= 600 ? EnumerateResponseChunks(message) : [message];

            foreach (var chunk in chunks)
            {
                token.ThrowIfCancellationRequested();
                _liveResponseBox.AppendText(chunk);
                _liveResponseBox.SelectionStart = _liveResponseBox.TextLength;
                _liveResponseBox.ScrollToCaret();
                AppendAssistantMessageChunk(chunk);

                var delay = GetResponseChunkDelay(chunk);

                if (delay > 0)
                {
                    await Task.Delay(delay, token);
                }
            }
        }
        finally
        {
            _isAssistantResponseStreaming = false;
            CompleteAssistantMessageStream();

            if (_assistantStateOverride == SurfaceState.Speaking)
            {
                ClearAssistantStateOverride();
            }
        }
    }

    // After a turn: updates the summary box, timeline (response, each tool result, citations) and activity cards.
    private void ApplyTurnSummary(AssistantTurn turn, PendingApprovalAction? pendingApproval, string renderedResponse)
    {
        _lastSessionSummary = BuildTurnSummary(turn, pendingApproval, renderedResponse);
        _sessionBox.Text = _lastSessionSummary;
        AppendTimelineEntry("RESPONSE", TrimForSingleLine(renderedResponse, 180), $"Intent `{turn.Intent}` completed.", Color.FromArgb(106, 220, 197));

        if (turn.ToolResults is not null)
        {
            foreach (var tool in turn.ToolResults)
            {
                AppendTimelineEntry(
                    $"TOOL {tool.ToolName.ToUpperInvariant()}",
                    tool.Summary,
                    tool.VerificationText,
                    tool.Succeeded ? Color.FromArgb(132, 196, 255) : Color.FromArgb(255, 138, 101));

                AddActivityCard(
                    tool.ToolName,
                    tool.Succeeded ? "Result confirmed" : "Result flagged",
                    tool.Summary,
                    tool.Succeeded ? Color.FromArgb(132, 196, 255) : Color.FromArgb(255, 138, 101));
            }
        }

        if (turn.Citations is { Length: > 0 })
        {
            AppendTimelineEntry(
                "CITATIONS",
                $"Attached {turn.Citations.Length} citation{(turn.Citations.Length == 1 ? string.Empty : "s")}.",
                string.Join(" | ", turn.Citations.Take(3).Select(citation => TrimForSingleLine(citation.Title, 40))),
                Color.FromArgb(173, 216, 230));
        }
    }

    // ----- Audio device, wake word and DSP pickers -----
    // Fills the microphone drop-down from the Windows input devices and selects the preferred one.
    // The handler is detached while filling so setting the selection does not trigger a settings save.
    private void RefreshAudioDevices()
    {
        _deviceComboBox.Items.Clear();

        for (var index = 0; index < WaveInEvent.DeviceCount; index++)
        {
            var capabilities = WaveInEvent.GetCapabilities(index);
            _deviceComboBox.Items.Add(capabilities.ProductName);
        }

        if (_deviceComboBox.Items.Count == 0)
        {
            UpdateCalibrationUi();
            return;
        }

        var preferredIndex = AudioDeviceResolver.GetPreferredInputDeviceIndex(_currentOptions.SpeechRecognitionDeviceName);

        if (preferredIndex < 0 || preferredIndex >= _deviceComboBox.Items.Count)
        {
            preferredIndex = 0;
        }

        _deviceComboBox.SelectedIndexChanged -= DeviceComboBoxOnSelectedIndexChanged;
        _deviceComboBox.SelectedIndex = preferredIndex;
        _deviceComboBox.SelectedIndexChanged += DeviceComboBoxOnSelectedIndexChanged;
        UpdateCalibrationUi();
    }

    // Fills the beamforming (multi-microphone) profile picker; the same detach-then-reattach pattern avoids saving while filling.
    private void RefreshBeamformingProfiles()
    {
        _beamformingProfileComboBox.SelectedIndexChanged -= BeamformingProfileComboBoxOnSelectedIndexChanged;
        _beamformingProfileComboBox.Items.Clear();

        foreach (var profile in BeamformingProfileCatalog.All)
        {
            _beamformingProfileComboBox.Items.Add(profile.DisplayName);
        }

        var selectedProfileName = string.IsNullOrWhiteSpace(_currentOptions.SpeechRecognitionBeamformingProfile)
            ? "auto"
            : _currentOptions.SpeechRecognitionBeamformingProfile.Trim();
        var selectedIndex = BeamformingProfileCatalog.All
            .Select((profile, index) => new { profile.Name, index })
            .FirstOrDefault(item => string.Equals(item.Name, selectedProfileName, StringComparison.OrdinalIgnoreCase))
            ?.index ?? 0;

        _beamformingProfileComboBox.SelectedIndex = selectedIndex;
        _beamformingProfileComboBox.SelectedIndexChanged += BeamformingProfileComboBoxOnSelectedIndexChanged;
        UpdateCalibrationUi();
    }

    // Fills the wake word model picker from files found on disk.
    private void RefreshWakeWordAssets()
    {
        _availableWakeWordAssets = WakeWordAssetCatalog.Discover(_context.WorkspaceRoot, _currentOptions);
        _wakeWordAssetComboBox.SelectedIndexChanged -= WakeWordAssetComboBoxOnSelectedIndexChanged;
        _wakeWordAssetComboBox.Items.Clear();

        foreach (var asset in _availableWakeWordAssets)
        {
            _wakeWordAssetComboBox.Items.Add(asset.BuildShortLabel());
        }

        var configuredPath = WakeWordAssetCatalog.NormalizeStoredPath(_currentOptions.SpeechRecognitionWakeWordAssetPath);
        var selectedIndex = _availableWakeWordAssets
            .Select((asset, index) => new { asset.Path, index })
            .FirstOrDefault(item => string.Equals(
                WakeWordAssetCatalog.NormalizeStoredPath(item.Path),
                configuredPath,
                StringComparison.OrdinalIgnoreCase))
            ?.index ?? 0;

        _wakeWordAssetComboBox.SelectedIndex = selectedIndex;
        _wakeWordAssetComboBox.SelectedIndexChanged += WakeWordAssetComboBoxOnSelectedIndexChanged;
        UpdateCalibrationUi();
    }

    // Fills the wake word engine picker.
    private void RefreshWakeEngines()
    {
        _availableWakeWordEngines = WakeWordEngineCatalog.Discover(_context.WorkspaceRoot, _currentOptions);
        _wakeEngineComboBox.SelectedIndexChanged -= WakeEngineComboBoxOnSelectedIndexChanged;
        _wakeEngineComboBox.Items.Clear();

        foreach (var engine in _availableWakeWordEngines)
        {
            _wakeEngineComboBox.Items.Add(engine.BuildShortLabel());
        }

        var configuredPath = WakeWordEngineCatalog.NormalizeStoredPath(_currentOptions.SpeechRecognitionWakeEnginePath);
        var selectedIndex = _availableWakeWordEngines
            .Select((engine, index) => new { engine.Path, index })
            .FirstOrDefault(item => string.Equals(
                WakeWordEngineCatalog.NormalizeStoredPath(item.Path),
                configuredPath,
                StringComparison.OrdinalIgnoreCase))
            ?.index ?? 0;

        _wakeEngineComboBox.SelectedIndex = selectedIndex;
        _wakeEngineComboBox.SelectedIndexChanged += WakeEngineComboBoxOnSelectedIndexChanged;
        UpdateCalibrationUi();
    }

    // Fills the hardware DSP (microphone processing) profile picker.
    private void RefreshHardwareDspProfiles()
    {
        _hardwareDspProfileComboBox.SelectedIndexChanged -= HardwareDspProfileComboBoxOnSelectedIndexChanged;
        _hardwareDspProfileComboBox.Items.Clear();

        foreach (var profile in HardwareDspProfileCatalog.All)
        {
            _hardwareDspProfileComboBox.Items.Add(profile.DisplayName);
        }

        var selectedProfileName = string.IsNullOrWhiteSpace(_currentOptions.SpeechRecognitionHardwareDspProfile)
            ? "auto"
            : _currentOptions.SpeechRecognitionHardwareDspProfile.Trim();
        var selectedIndex = HardwareDspProfileCatalog.All
            .Select((profile, index) => new { profile.Name, index })
            .FirstOrDefault(item => string.Equals(item.Name, selectedProfileName, StringComparison.OrdinalIgnoreCase))
            ?.index ?? 0;

        _hardwareDspProfileComboBox.SelectedIndex = selectedIndex;
        _hardwareDspProfileComboBox.SelectedIndexChanged += HardwareDspProfileComboBoxOnSelectedIndexChanged;
        UpdateCalibrationUi();
    }

    // Updates the calibration and validation status text and enables the buttons only if a microphone exists.
    private void UpdateCalibrationUi()
    {
        var resolved = MicrophoneProcessingProfileResolver.Resolve(_currentOptions);
        _calibrationStatusLabel.Text = TrimForSingleLine(resolved.BuildUiSummary(), 96);
        _validationStatusLabel.Text = resolved.Validation is null
            ? "No validation saved."
            : TrimForSingleLine(
                $"{resolved.Validation.RecommendedConversationMode} | wake {resolved.Validation.WakeWordReadinessScore:P0} | duplex {resolved.Validation.FullDuplexReadinessScore:P0}",
                96);

        var hasAvailableDevice = !string.IsNullOrWhiteSpace(_currentOptions.SpeechRecognitionDeviceName)
            || WaveInEvent.DeviceCount > 0;
        _calibrateMicButton.Enabled = hasAvailableDevice;
        _validateMicButton.Enabled = hasAvailableDevice;
    }

    // Handles a new microphone choice. JarvisOptions is an immutable record, so "with { ... }" below makes a changed copy
    // that replaces _currentOptions; the change is then saved to jarvis.settings.json and the voice runtime is rebuilt to use it.
    private async Task OnDeviceSelectionChangedAsync()
    {
        var deviceName = ResolveSelectedInputDeviceName();

        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return;
        }

        if (string.Equals(deviceName, _currentOptions.SpeechRecognitionDeviceName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _currentOptions = _currentOptions with { SpeechRecognitionDeviceName = deviceName };
        UpdateCalibrationUi();
        await PersistCurrentOptionsAsync();
        await ReloadVoiceRuntimeAsync(_currentOptions);
    }

    // Event handler for the microphone drop-down. The same pattern repeats for the beam, wake word, wake engine and DSP pickers:
    // a thin async void wrapper that catches errors and shows them in chat, around an async method that does the work.
    private async void DeviceComboBoxOnSelectedIndexChanged(object? sender, EventArgs e)
    {
        try
        {
            await OnDeviceSelectionChangedAsync();
        }
        catch (Exception exception)
        {
            AppendSystemMessage($"Microphone selection failed: {exception.Message}");
        }
    }

    // Name of the microphone chosen in the drop-down, or the saved one if nothing is selected.
    private string ResolveSelectedInputDeviceName()
    {
        return _deviceComboBox.SelectedItem as string
            ?? _currentOptions.SpeechRecognitionDeviceName;
    }

    // Copies the drop-down choice into the current settings without saving (used right before calibration).
    private void SyncSelectedInputDeviceIntoCurrentOptions()
    {
        var deviceName = ResolveSelectedInputDeviceName();

        if (string.IsNullOrWhiteSpace(deviceName)
            || string.Equals(deviceName, _currentOptions.SpeechRecognitionDeviceName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _currentOptions = _currentOptions with
        {
            SpeechRecognitionDeviceName = deviceName
        };
    }

    // The user picked a beamforming profile; "off" turns beamforming off entirely.
    private async Task OnBeamformingProfileSelectionChangedAsync()
    {
        if (_beamformingProfileComboBox.SelectedIndex < 0
            || _beamformingProfileComboBox.SelectedIndex >= BeamformingProfileCatalog.All.Count)
        {
            return;
        }

        var selectedProfile = BeamformingProfileCatalog.All[_beamformingProfileComboBox.SelectedIndex];

        if (string.Equals(selectedProfile.Name, _currentOptions.SpeechRecognitionBeamformingProfile, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _currentOptions = _currentOptions with
        {
            SpeechRecognitionBeamformingProfile = selectedProfile.Name,
            SpeechRecognitionBeamformingEnabled = !string.Equals(selectedProfile.Name, "off", StringComparison.OrdinalIgnoreCase)
        };

        UpdateCalibrationUi();
        await PersistCurrentOptionsAsync();
        await ReloadVoiceRuntimeAsync(_currentOptions);
    }

    private async void BeamformingProfileComboBoxOnSelectedIndexChanged(object? sender, EventArgs e)
    {
        try
        {
            await OnBeamformingProfileSelectionChangedAsync();
        }
        catch (Exception exception)
        {
            AppendSystemMessage($"Beamforming profile update failed: {exception.Message}");
        }
    }

    // The user picked a different wake word model file.
    private async Task OnWakeWordAssetSelectionChangedAsync()
    {
        if (_wakeWordAssetComboBox.SelectedIndex < 0
            || _wakeWordAssetComboBox.SelectedIndex >= _availableWakeWordAssets.Count)
        {
            return;
        }

        var selectedAsset = _availableWakeWordAssets[_wakeWordAssetComboBox.SelectedIndex];
        var selectedPath = WakeWordAssetCatalog.NormalizeStoredPath(selectedAsset.Path);
        var currentPath = WakeWordAssetCatalog.NormalizeStoredPath(_currentOptions.SpeechRecognitionWakeWordAssetPath);

        if (string.Equals(selectedPath, currentPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _currentOptions = _currentOptions with
        {
            SpeechRecognitionWakeWordAssetPath = selectedAsset.Path
        };

        UpdateCalibrationUi();
        await PersistCurrentOptionsAsync();
        await ReloadVoiceRuntimeAsync(_currentOptions);
    }

    private async void WakeWordAssetComboBoxOnSelectedIndexChanged(object? sender, EventArgs e)
    {
        try
        {
            await OnWakeWordAssetSelectionChangedAsync();
        }
        catch (Exception exception)
        {
            AppendSystemMessage($"Wake asset update failed: {exception.Message}");
        }
    }

    // The user picked a different wake word engine.
    private async Task OnWakeEngineSelectionChangedAsync()
    {
        if (_wakeEngineComboBox.SelectedIndex < 0
            || _wakeEngineComboBox.SelectedIndex >= _availableWakeWordEngines.Count)
        {
            return;
        }

        var selectedEngine = _availableWakeWordEngines[_wakeEngineComboBox.SelectedIndex];
        var selectedPath = WakeWordEngineCatalog.NormalizeStoredPath(selectedEngine.Path);
        var currentPath = WakeWordEngineCatalog.NormalizeStoredPath(_currentOptions.SpeechRecognitionWakeEnginePath);

        if (string.Equals(selectedPath, currentPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _currentOptions = _currentOptions with
        {
            SpeechRecognitionWakeEnginePath = selectedEngine.Path
        };

        UpdateCalibrationUi();
        await PersistCurrentOptionsAsync();
        await ReloadVoiceRuntimeAsync(_currentOptions);
    }

    private async void WakeEngineComboBoxOnSelectedIndexChanged(object? sender, EventArgs e)
    {
        try
        {
            await OnWakeEngineSelectionChangedAsync();
        }
        catch (Exception exception)
        {
            AppendSystemMessage($"Wake engine update failed: {exception.Message}");
        }
    }

    // The user picked a different hardware DSP profile.
    private async Task OnHardwareDspProfileSelectionChangedAsync()
    {
        if (_hardwareDspProfileComboBox.SelectedIndex < 0
            || _hardwareDspProfileComboBox.SelectedIndex >= HardwareDspProfileCatalog.All.Count)
        {
            return;
        }

        var selectedProfile = HardwareDspProfileCatalog.All[_hardwareDspProfileComboBox.SelectedIndex];

        if (string.Equals(selectedProfile.Name, _currentOptions.SpeechRecognitionHardwareDspProfile, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _currentOptions = _currentOptions with
        {
            SpeechRecognitionHardwareDspProfile = selectedProfile.Name
        };

        UpdateCalibrationUi();
        await PersistCurrentOptionsAsync();
        await ReloadVoiceRuntimeAsync(_currentOptions);
    }

    private async void HardwareDspProfileComboBoxOnSelectedIndexChanged(object? sender, EventArgs e)
    {
        try
        {
            await OnHardwareDspProfileSelectionChangedAsync();
        }
        catch (Exception exception)
        {
            AppendSystemMessage($"Hardware DSP update failed: {exception.Message}");
        }
    }

    // Guided microphone calibration (button click): stops listening, records room noise then a spoken sample,
    // derives a noise gate and gain cap from them, saves the profile and restarts the voice runtime.
    private async Task CalibrateMicrophoneAsync()
    {
        if (_isSubmitting)
        {
            AppendSystemMessage("Calibration is blocked while Jarvis is processing another request.");
            return;
        }

        if (WaveInEvent.DeviceCount <= 0)
        {
            AppendSystemMessage("No microphone is available for calibration.");
            return;
        }

        // Remember whether the mic was listening so it can be turned back on afterwards (in finally).
        SyncSelectedInputDeviceIntoCurrentOptions();

        var wasListening = _currentVoiceRuntime.CurrentStatus.IsListening;

        try
        {
            _calibrateMicButton.Enabled = false;

            if (wasListening)
            {
                await _currentVoiceRuntime.StopAsync(_cancellationTokenSource.Token);
            }

            // Modal message boxes pause the flow so the user knows when to be quiet and when to speak.
            MessageBox.Show(
                this,
                "Microphone calibration starts now.\n\n1. Keep the room quiet for the first capture.\n2. After that prompt, speak naturally toward the selected microphone for a few seconds.",
                "Microphone Calibration",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            AppendRuntimeMessage("Calibration: capturing room noise.");
            // The numbers (2200, 3200) are how many milliseconds of audio to record.
            var ambientSample = await _microphoneCalibrationService.CaptureSampleAsync(_currentOptions, 2200, _cancellationTokenSource.Token);

            MessageBox.Show(
                this,
                "Room-noise capture complete.\n\nNow speak a short command in your normal voice until the next prompt closes.",
                "Microphone Calibration",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            AppendRuntimeMessage("Calibration: capturing speech sample.");
            var speechSample = await _microphoneCalibrationService.CaptureSampleAsync(_currentOptions, 3200, _cancellationTokenSource.Token);
            var resolvedProcessing = ResolveActiveProcessingProfile();
            var calibration = _microphoneCalibrationService.CreateCalibrationProfile(
                _currentOptions,
                ambientSample,
                speechSample,
                resolvedProcessing.EffectiveBeamformingProfileName,
                resolvedProcessing.EffectiveHardwareDspProfileName);
            // Replace any older profile for the same device and settings, keeping the rest, sorted for a stable file.
            var profiles = _currentOptions.SpeechRecognitionCalibrationProfiles
                .Where(profile =>
                    !IsSameCalibrationConfiguration(profile, calibration))
                .Concat([calibration])
                .OrderBy(profile => profile.DeviceName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(profile => NormalizeProfileNameForStorage(profile.PreferredBeamformingProfile), StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _currentOptions = _currentOptions with
            {
                SpeechRecognitionCalibrationProfiles = profiles
            };

            UpdateCalibrationUi();
            await PersistCurrentOptionsAsync();
            await ReloadVoiceRuntimeAsync(_currentOptions);
            AppendRuntimeMessage($"Calibration saved for {_currentOptions.SpeechRecognitionDeviceName}: {calibration.Notes}.");
            AppendTimelineEntry(
                "AUDIO",
                "Microphone calibration saved.",
                $"Gate {calibration.RecommendedVoiceActivityThreshold:P1}, gain cap {calibration.RecommendedInputGainCap:F1}x, beam {calibration.PreferredBeamformingProfile}, DSP {calibration.HardwareDspProfileName}.",
                Color.FromArgb(106, 220, 197));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AppendSystemMessage($"Calibration failed: {exception.Message}");
            AppendTimelineEntry("AUDIO", "Microphone calibration failed.", exception.Message, Color.FromArgb(255, 138, 101));
        }
        // Always re-enable the button and restart listening if it was on before.
        finally
        {
            _calibrateMicButton.Enabled = true;

            if (wasListening
                && !_cancellationTokenSource.IsCancellationRequested
                && !_currentVoiceRuntime.CurrentStatus.IsListening)
            {
                try
                {
                    await _currentVoiceRuntime.StartAsync(_cancellationTokenSource.Token);
                }
                catch
                {
                }
            }
        }
    }

    // Guided validation (button click): records room noise, the wake phrase, a command, and plays a tone to measure how much
    // speaker sound leaks into the microphone ("full-duplex" means listening while talking), then saves scores.
    private async Task ValidateMicrophoneAsync()
    {
        if (_isSubmitting)
        {
            AppendSystemMessage("Validation is blocked while Jarvis is processing another request.");
            return;
        }

        if (WaveInEvent.DeviceCount <= 0)
        {
            AppendSystemMessage("No microphone is available for validation.");
            return;
        }

        SyncSelectedInputDeviceIntoCurrentOptions();

        var wasListening = _currentVoiceRuntime.CurrentStatus.IsListening;

        try
        {
            _validateMicButton.Enabled = false;

            if (wasListening)
            {
                await _currentVoiceRuntime.StopAsync(_cancellationTokenSource.Token);
            }

            MessageBox.Show(
                this,
                "Microphone validation starts now.\n\n1. Keep the room quiet for the first capture.\n2. Say only the wake phrase after the next prompt.\n3. Then say a short command.\n4. Jarvis will play a short tone to estimate full-duplex speaker bleed.",
                "Microphone Validation",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            AppendRuntimeMessage("Validation: capturing room noise.");
            var ambientSample = await _microphoneCalibrationService.CaptureSampleAsync(_currentOptions, 1800, _cancellationTokenSource.Token);

            MessageBox.Show(
                this,
                $"Say only \"{_currentOptions.WakePhrase}\" toward the selected microphone until this prompt closes.",
                "Wake Phrase Validation",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            AppendRuntimeMessage("Validation: capturing wake phrase sample.");
            var wakeSample = await _microphoneCalibrationService.CaptureSampleAsync(_currentOptions, 2200, _cancellationTokenSource.Token);

            MessageBox.Show(
                this,
                "Now say a short command in your normal voice until this prompt closes.",
                "Command Validation",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            AppendRuntimeMessage("Validation: capturing command sample.");
            var speechSample = await _microphoneCalibrationService.CaptureSampleAsync(_currentOptions, 2600, _cancellationTokenSource.Token);

            MessageBox.Show(
                this,
                "Stay quiet for the next capture while Jarvis plays a short validation tone through the speaker.",
                "Full-Duplex Validation",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            AppendRuntimeMessage("Validation: measuring playback leakage.");
            var playbackLeakageSample = await _microphoneCalibrationService.CapturePlaybackLeakageAsync(_currentOptions, 1700, _cancellationTokenSource.Token);
            var resolvedProcessing = ResolveActiveProcessingProfile();
            var validation = _microphoneCalibrationService.CreateValidationProfile(
                _currentOptions,
                ambientSample,
                wakeSample,
                speechSample,
                playbackLeakageSample,
                ResolveEffectiveWakeWordAssetLabel(),
                resolvedProcessing.EffectiveWakeWordAssetPath,
                ResolveEffectiveWakeEngineLabel(),
                resolvedProcessing.EffectiveWakeEnginePath,
                resolvedProcessing.EffectiveBeamformingProfileName,
                resolvedProcessing.EffectiveHardwareDspProfileName);
            var profiles = _currentOptions.SpeechRecognitionValidationProfiles
                .Where(profile =>
                    !IsSameValidationConfiguration(profile, validation))
                .Concat([validation])
                .OrderBy(profile => profile.DeviceName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(profile => WakeWordAssetCatalog.NormalizeStoredPath(profile.WakeWordAssetPath), StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _currentOptions = _currentOptions with
            {
                SpeechRecognitionValidationProfiles = profiles
            };

            UpdateCalibrationUi();
            await PersistCurrentOptionsAsync();
            await ReloadVoiceRuntimeAsync(_currentOptions);
            AppendRuntimeMessage(
                $"Validation saved for {_currentOptions.SpeechRecognitionDeviceName}: {validation.Notes}. Recommended {validation.RecommendedConversationMode}.");
            AppendTimelineEntry(
                "AUDIO",
                "Voice stack validation saved.",
                $"Wake {validation.WakeWordReadinessScore:P0}, duplex {validation.FullDuplexReadinessScore:P0}, engine {validation.WakeEngineName}, beam {validation.BeamformingProfileName}, DSP {validation.HardwareDspProfileName}, recommended mode {validation.RecommendedConversationMode}.",
                // A duplex readiness score of 0.58 or higher shows in teal, lower in amber.
                validation.FullDuplexReadinessScore >= 0.58
                    ? Color.FromArgb(106, 220, 197)
                    : Color.FromArgb(255, 191, 105));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AppendSystemMessage($"Validation failed: {exception.Message}");
            AppendTimelineEntry("AUDIO", "Voice stack validation failed.", exception.Message, Color.FromArgb(255, 138, 101));
        }
        finally
        {
            _validateMicButton.Enabled = true;

            if (wasListening
                && !_cancellationTokenSource.IsCancellationRequested
                && !_currentVoiceRuntime.CurrentStatus.IsListening)
            {
                try
                {
                    await _currentVoiceRuntime.StartAsync(_cancellationTokenSource.Token);
                }
                catch
                {
                }
            }
        }
    }

    // Label of the wake word model actually in use according to settings.
    private string ResolveEffectiveWakeWordAssetLabel()
    {
        return WakeWordAssetCatalog.ResolveConfiguredAsset(_currentOptions)?.BuildShortLabel()
            ?? "Recognizer Default";
    }

    private string ResolveEffectiveWakeEngineLabel()
    {
        var resolved = ResolveActiveProcessingProfile();
        var descriptor = WakeWordEngineCatalog.ResolveDescriptorByPath(
            _context.WorkspaceRoot,
            _currentOptions,
            resolved.EffectiveWakeEnginePath);
        return descriptor.BuildShortLabel();
    }

    // Works out which beam, DSP and wake settings are really in effect (including "auto" choices).
    private ResolvedMicrophoneProcessingProfile ResolveActiveProcessingProfile()
    {
        return MicrophoneProcessingProfileResolver.Resolve(_currentOptions);
    }

    // Two calibration profiles count as the same if device, beam profile and DSP profile match; used to replace instead of duplicate.
    private static bool IsSameCalibrationConfiguration(
        SpeechRecognitionCalibrationProfile existing,
        SpeechRecognitionCalibrationProfile candidate)
    {
        return string.Equals(existing.DeviceName, candidate.DeviceName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                NormalizeProfileNameForStorage(existing.PreferredBeamformingProfile),
                NormalizeProfileNameForStorage(candidate.PreferredBeamformingProfile),
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                NormalizeProfileNameForStorage(existing.HardwareDspProfileName),
                NormalizeProfileNameForStorage(candidate.HardwareDspProfileName),
                StringComparison.OrdinalIgnoreCase);
    }

    // Same idea for validation profiles, which also compare the wake word model and engine (by path, or by name when no path).
    private static bool IsSameValidationConfiguration(
        SpeechRecognitionValidationProfile existing,
        SpeechRecognitionValidationProfile candidate)
    {
        var existingWakeWordAssetPath = WakeWordAssetCatalog.NormalizeStoredPath(existing.WakeWordAssetPath);
        var candidateWakeWordAssetPath = WakeWordAssetCatalog.NormalizeStoredPath(candidate.WakeWordAssetPath);
        var wakeAssetMatches = string.Equals(existingWakeWordAssetPath, candidateWakeWordAssetPath, StringComparison.OrdinalIgnoreCase)
            || (string.IsNullOrWhiteSpace(existingWakeWordAssetPath)
                && string.IsNullOrWhiteSpace(candidateWakeWordAssetPath)
                && string.Equals(existing.WakeWordAssetName, candidate.WakeWordAssetName, StringComparison.OrdinalIgnoreCase));
        var existingWakeEnginePath = WakeWordEngineCatalog.NormalizeStoredPath(existing.WakeEnginePath);
        var candidateWakeEnginePath = WakeWordEngineCatalog.NormalizeStoredPath(candidate.WakeEnginePath);
        var wakeEngineMatches = WakeWordEngineCatalog.StoredPathsMatch(existingWakeEnginePath, candidateWakeEnginePath)
            || (string.IsNullOrWhiteSpace(existingWakeEnginePath)
                && string.IsNullOrWhiteSpace(candidateWakeEnginePath)
                && string.Equals(existing.WakeEngineName, candidate.WakeEngineName, StringComparison.OrdinalIgnoreCase));

        return string.Equals(existing.DeviceName, candidate.DeviceName, StringComparison.OrdinalIgnoreCase)
            && wakeAssetMatches
            && wakeEngineMatches
            && string.Equals(
                NormalizeProfileNameForStorage(existing.BeamformingProfileName),
                NormalizeProfileNameForStorage(candidate.BeamformingProfileName),
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                NormalizeProfileNameForStorage(existing.HardwareDspProfileName),
                NormalizeProfileNameForStorage(candidate.HardwareDspProfileName),
                StringComparison.OrdinalIgnoreCase);
    }

    // Treats an empty profile name as "auto" and ignores case so comparisons are reliable.
    private static string NormalizeProfileNameForStorage(string? profileName)
    {
        return string.IsNullOrWhiteSpace(profileName)
            ? "auto"
            : profileName.Trim().ToLowerInvariant();
    }

    // ----- Saving settings and rebuilding services -----
    // Writes the current settings to jarvis.settings.json. The semaphore makes concurrent saves queue up instead of overlapping.
    private async Task PersistCurrentOptionsAsync()
    {
        await _optionsPersistenceGate.WaitAsync(_cancellationTokenSource.Token);

        try
        {
            _context.Options = _currentOptions;
            await JarvisSettingsLoader.SaveAsync(_context.WorkspaceRoot, _currentOptions, _cancellationTokenSource.Token);
        }
        finally
        {
            _optionsPersistenceGate.Release();
        }
    }

    // Builds a new assistant from new settings (same startup code as launch) and swaps it in, moving the Activity event over.
    private async Task ReplaceAssistantAsync(JarvisOptions options)
    {
        var assistant = await AppBootstrapper.CreateAssistantAsync(
            _context.WorkspaceRoot,
            options,
            _context.DesktopAutomation,
            _cancellationTokenSource.Token);

        _context.Assistant.Activity -= OnAssistantActivity;
        _context.Assistant = assistant;
        _context.Options = options;
        _context.Assistant.Activity += OnAssistantActivity;
    }

    // Applies changed audio settings: rebuild the assistant, stop and dispose the old voice runtime, create a new one,
    // re-attach the event handlers, refresh the pickers, and start listening again. Start errors are ignored here.
    private async Task ReloadVoiceRuntimeAsync(JarvisOptions options)
    {
        _currentOptions = options;
        await ReplaceAssistantAsync(options);

        try
        {
            await _currentVoiceRuntime.StopAsync(_cancellationTokenSource.Token);
        }
        catch
        {
        }

        _currentVoiceRuntime.StatusChanged -= OnVoiceStatusChanged;
        _currentVoiceRuntime.SignalLevelChanged -= OnVoiceSignalChanged;
        _currentVoiceRuntime.CommandRecognized -= OnVoiceCommandRecognized;
        _currentVoiceRuntime.BargeInRequested -= OnVoiceBargeInRequested;
        _currentVoiceRuntime.Dispose();

        _currentVoiceRuntime = VoiceRuntimeFactory.Create(options);
        _context.VoiceRuntime = _currentVoiceRuntime;
        _context.Options = options;
        _currentVoiceRuntime.StatusChanged += OnVoiceStatusChanged;
        _currentVoiceRuntime.SignalLevelChanged += OnVoiceSignalChanged;
        _currentVoiceRuntime.CommandRecognized += OnVoiceCommandRecognized;
        _currentVoiceRuntime.BargeInRequested += OnVoiceBargeInRequested;
        _currentVoiceRuntime.SetPlaybackState(_context.Speaker.CurrentStatus.IsSpeaking);
        RefreshWakeWordAssets();
        RefreshWakeEngines();
        RefreshBeamformingProfiles();
        RefreshHardwareDspProfiles();
        UpdateCalibrationUi();
        UpdateVoiceUi(_currentVoiceRuntime.CurrentStatus, appendToFeed: false);
        UpdateSignalUi(_currentVoiceRuntime.CurrentSignal);

        try
        {
            await _currentVoiceRuntime.StartAsync(_cancellationTokenSource.Token);
        }
        catch
        {
        }
    }

    // Saves persona and style changes from the presence dialog (tone, nickname, pronunciation hints) and rebuilds the
    // assistant and speaker so they take effect; does nothing if nothing actually changed.
    private async Task ApplyPresenceSettingsAsync(JarvisOptions options)
    {
        var normalizedOptions = options with
        {
            AssistantPresencePreset = AssistantPersonaConfiguration.ResolvePresencePreset(options),
            AssistantResponseStyle = AssistantPersonaConfiguration.ResolveResponseStyle(options),
            AssistantUserNickname = options.AssistantUserNickname?.Trim() ?? string.Empty,
            AssistantPronunciationHints = AssistantPersonaConfiguration.ResolvePronunciationHints(options).ToArray(),
            AssistantPersona = options.AssistantPersona?.Trim() ?? string.Empty
        };

        if (string.Equals(_currentOptions.AssistantPresencePreset, normalizedOptions.AssistantPresencePreset, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_currentOptions.AssistantResponseStyle, normalizedOptions.AssistantResponseStyle, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_currentOptions.AssistantUserNickname, normalizedOptions.AssistantUserNickname, StringComparison.Ordinal)
            && _currentOptions.AssistantSmallTalkEnabled == normalizedOptions.AssistantSmallTalkEnabled
            && _currentOptions.AssistantPronunciationHints.SequenceEqual(normalizedOptions.AssistantPronunciationHints, StringComparer.OrdinalIgnoreCase)
            && string.Equals(_currentOptions.AssistantPersona, normalizedOptions.AssistantPersona, StringComparison.Ordinal))
        {
            return;
        }

        _currentOptions = normalizedOptions;
        await PersistCurrentOptionsAsync();
        await ReplaceAssistantAsync(_currentOptions);
        ReplaceSpeaker(_currentOptions);
        await RefreshRuntimeStateAsync();

        var summary = BuildPresenceUpdateSummary(_currentOptions);
        AppendRuntimeMessage(summary);
        AppendTimelineEntry("PRESENCE", "Presence profile updated.", summary, Color.FromArgb(106, 220, 197));
        AddActivityCard("Presence", "Profile updated", summary, Color.FromArgb(106, 220, 197));
    }

    // ----- Microphone, volume and whisper-mode controls -----
    // Mute Mic / Start Mic button: stops listening if on, otherwise starts.
    private async Task ToggleMicrophoneAsync()
    {
        if (_currentVoiceRuntime.CurrentStatus.IsListening)
        {
            await _currentVoiceRuntime.StopAsync(_cancellationTokenSource.Token);
            return;
        }

        await _currentVoiceRuntime.StartAsync(_cancellationTokenSource.Token);
    }

    // Volume slider changed: clamps to 10%-200%, rebuilds the speaker with the new volume and saves it. Ignores changes under 0.1%.
    private async Task UpdateSpeakerVolumeAsync(double volumeMultiplier)
    {
        var normalized = Math.Clamp(volumeMultiplier, 0.10, 2.0);

        if (Math.Abs(_currentOptions.SpeakerVolumeMultiplier - normalized) < 0.001)
        {
            return;
        }

        _currentOptions = _currentOptions with
        {
            SpeakerVolumeMultiplier = normalized
        };

        ReplaceSpeaker(_currentOptions);
        await PersistCurrentOptionsAsync();
    }

    // "Whisper" checkbox: switches Jarvis to a quieter speaking mode, rebuilds the speaker and saves.
    private async Task UpdateWhisperModeAsync(bool whisperEnabled)
    {
        if (_currentOptions.WhisperModeEnabled == whisperEnabled)
        {
            return;
        }

        _currentOptions = _currentOptions with
        {
            WhisperModeEnabled = whisperEnabled
        };

        ReplaceSpeaker(_currentOptions);
        await PersistCurrentOptionsAsync();
    }

    // ----- Voice turn handling -----
    // Events from the voice runtime, speaker and assistant arrive on background threads. Each handler below follows one pattern:
    // bail out if the window is closing; if not on the UI thread (InvokeRequired), BeginInvoke the same work onto it; otherwise do it directly.
    // The voice runtime reports a status change (listening, muted, error...).
    private void OnVoiceStatusChanged(object? sender, VoiceStatusChangedEventArgs eventArgs)
    {
        if (!CanUseUi())
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() =>
            {
                if (CanUseUi())
                {
                    UpdateVoiceUi(eventArgs.Status, appendToFeed: true);
                }
            }));
            return;
        }

        UpdateVoiceUi(eventArgs.Status, appendToFeed: true);
    }

    // The voice runtime reports a new microphone level reading (fires many times per second).
    private void OnVoiceSignalChanged(object? sender, VoiceSignalChangedEventArgs eventArgs)
    {
        if (!CanUseUi())
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() =>
            {
                if (CanUseUi())
                {
                    UpdateSignalUi(eventArgs.Signal);
                }
            }));
            return;
        }

        UpdateSignalUi(eventArgs.Signal);
    }

    // The speaker started or stopped talking. The voice runtime is told first (it must ignore Jarvis's own voice) before touching the UI.
    private void OnSpeakerStatusChanged(object? sender, SpeakerStatusChangedEventArgs eventArgs)
    {
        _currentVoiceRuntime.SetPlaybackState(eventArgs.Status.IsSpeaking);

        if (!CanUseUi())
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() =>
            {
                if (CanUseUi())
                {
                    UpdateSpeakerUi(eventArgs.Status);
                }
            }));
            return;
        }

        UpdateSpeakerUi(eventArgs.Status);
    }

    // The assistant reports what it is doing (planning, running a tool, error...); the screen is updated on the UI thread.
    private void OnAssistantActivity(object? sender, AssistantActivityEventArgs eventArgs)
    {
        if (!CanUseUi())
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() =>
            {
                if (CanUseUi())
                {
                    UpdateAssistantRuntimeUi(eventArgs.Activity);
                }
            }));
            return;
        }

        UpdateAssistantRuntimeUi(eventArgs.Activity);
    }

    // The voice runtime heard a command (after wake word / speech recognition); hands it to HandleVoiceCommandAsync on the UI thread.
    private void OnVoiceCommandRecognized(object? sender, VoiceCommandRecognizedEventArgs eventArgs)
    {
        if (!CanUseUi())
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() =>
            {
                if (CanUseUi())
                {
                    _ = HandleVoiceCommandAsync(eventArgs);
                }
            }));
            return;
        }

        _ = HandleVoiceCommandAsync(eventArgs);
    }

    // Decides what to do with a heard voice command, in this order: lecture recording filter, talking over Jarvis,
    // busy with another request, low-confidence confirmation, and finally normal processing.
    private async Task HandleVoiceCommandAsync(VoiceCommandRecognizedEventArgs eventArgs)
    {
        // While a lecture records, whatever the wake gate lets through is most likely the lecture, so only the
        // stop command is acted on and nothing else gets a reply. It skips the low-confidence check, whose
        // "did you mean" would wait for a yes this gate drops.
        if (_context.Assistant.IsRecordingLecture)
        {
            if (eventArgs.Text == "lecture notes stop")
            {
                await ProcessPromptAsync(eventArgs.Text, "VOICE", clearInput: false);
            }

            return;
        }

        // Talking over a reply only counts with the wake word ("Jarvis, stop", "Jarvis, that's good, thank you"):
        // loudness can't tell the user from Jarvis's own voice coming back through a speaker. The words aren't
        // acted on, since at speaker volume they're mixed with the echo; Jarvis goes quiet and listens instead.
        var speaker = _context.Speaker.CurrentStatus;

        if (speaker.IsSpeaking)
        {
            // IsEcho checks whether the heard words are just Jarvis's own reply picked up by the microphone; echoes are ignored.
            if (!SpokenReply.IsEcho(eventArgs.RawText, speaker.Message))
            {
                _context.Speaker.Interrupt();
                _currentVoiceRuntime.ListenForFollowUp();
                AddBargeInTimelineEntry("wake word");
            }

            return;
        }

        // Busy: if the user had just interrupted Jarvis (within 4 seconds), keep this command and run it after the interrupted turn cleans up; otherwise reject it.
        if (_isSubmitting)
        {
            if (DateTimeOffset.UtcNow - _lastBargeInUtc < TimeSpan.FromSeconds(4))
            {
                _queuedVoiceDirective = QueuedVoiceDirective.From(eventArgs);
                AppendRuntimeMessage(
                    eventArgs.RequiresClarification
                        ? "Low-confidence voice directive queued behind the interrupted response."
                        : "Voice command queued behind the interrupted response.");
                AppendTimelineEntry(
                    "VOICE",
                    eventArgs.RequiresClarification ? "Barge-in confirmation queued." : "Barge-in command queued.",
                    eventArgs.RequiresClarification
                        ? "The assistant is finishing the current turn cleanup before asking for a low-confidence voice confirmation."
                        : "The assistant is finishing the current turn cleanup before routing the new voice command.",
                    Color.FromArgb(255, 191, 105));
                return;
            }

            AppendSystemMessage("Voice command ignored because the assistant is already processing another request.");
            AppendTimelineEntry("VOICE", "Command ignored while busy.", "The assistant was already working on another task.", Color.FromArgb(255, 138, 101));

            if (ShouldSpeakVoiceAcknowledgement("VOICE"))
            {
                await TrySpeakAsync("Still working on the last request.", _cancellationTokenSource.Token);
            }

            return;
        }

        // Low-confidence recognition: ask the user to confirm instead of acting.
        if (eventArgs.RequiresClarification)
        {
            await PromptForVoiceClarificationAsync(eventArgs);
            return;
        }

        // An answer to Jarvis's question is dictation, not a command: "Spotify" must not become "open app Spotify".
        var isAnswer = DateTimeOffset.UtcNow < _followUpAnswerDeadlineUtc;
        await ProcessPromptAsync(isAnswer ? eventArgs.RawText : eventArgs.Text, "VOICE", clearInput: false);
    }

    // The voice runtime detected the user speaking over Jarvis: stop the speech at once and note the time (used by the 4 second rule above).
    private void OnVoiceBargeInRequested(object? sender, VoiceBargeInEventArgs eventArgs)
    {
        _context.Speaker.Interrupt();
        _lastBargeInUtc = DateTimeOffset.UtcNow;

        if (!CanUseUi())
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() =>
            {
                if (CanUseUi())
                {
                    AddBargeInTimelineEntry(eventArgs.Reason);
                }
            }));
            return;
        }

        AddBargeInTimelineEntry(eventArgs.Reason);
    }

    // Logs that the user interrupted Jarvis, and why (hotkey, wake word...).
    private void AddBargeInTimelineEntry(string reason)
    {
        AppendRuntimeMessage("Barge-in detected. Voice output interrupted.");
        AppendTimelineEntry(
            "VOICE",
            "Barge-in detected.",
            $"Voice output was interrupted ({reason}).",
            Color.FromArgb(255, 191, 105));
    }

    // ----- Showing status on screen -----
    // Updates the voice labels (LISTEN HOT/READY, WAKE...), the mic button and the tray tooltip. Adds a feed line only when the text actually changed.
    private void UpdateVoiceUi(VoiceStatusSnapshot status, bool appendToFeed)
    {
        _trayIcon.Text = BuildTrayText(status.StatusText);
        _voiceStatusLabel.Text = status.StatusText;
        _listenIndicatorLabel.Text = status.IsListening ? "LISTEN HOT" : status.IsAvailable ? "LISTEN READY" : "MIC OFFLINE";
        _wakeIndicatorLabel.Text = status.IsArmed
            ? "WAKE DETECTED"
            : _currentOptions.WakeWordEnabled
                ? "WAKE GATE"
                : "OPEN MIC";

        _listenIndicatorLabel.ForeColor = status.IsAvailable
            ? status.IsListening ? Color.FromArgb(106, 220, 197) : Color.FromArgb(255, 191, 105)
            : Color.FromArgb(255, 138, 101);
        _wakeIndicatorLabel.ForeColor = status.IsArmed
            ? Color.FromArgb(255, 138, 101)
            : Color.FromArgb(132, 196, 255);

        _micToggleButton.Text = status.IsListening ? "Mute Mic" : "Start Mic";
        _micToggleButton.Enabled = status.IsAvailable || status.IsListening;

        if (!appendToFeed
            || _isInTrayMode
            || string.Equals(_lastVoiceStatusText, status.StatusText, StringComparison.Ordinal))
        {
            _lastVoiceStatusText = status.StatusText;
            RecomputeSurfaceState();
            return;
        }

        _lastVoiceStatusText = status.StatusText;
        AppendRuntimeMessage(status.StatusText);
        AppendTimelineEntry("VOICE", status.StatusText, "Voice runtime status changed.", Color.FromArgb(173, 216, 230));
        RecomputeSurfaceState();
    }

    // Stores the newest microphone reading and feeds the RMS/peak labels and the waveform.
    private void UpdateSignalUi(VoiceSignalSnapshot signal)
    {
        _latestSignal = signal;
        _rmsLabel.Text = $"RMS {signal.RmsLevel:P0}";
        _peakLabel.Text = $"PEAK {signal.PeakLevel:P0}";
        _waveformControl.PushSignal(signal);



        RecomputeSurfaceState();
    }

    // Tells the waveform whether Jarvis is speaking (it changes how the waveform looks).
    private void UpdateSpeakerUi(SpeakerStatusSnapshot status)
    {
        _waveformControl.SetSpeaking(status.IsSpeaking);
        RecomputeSurfaceState();
    }

    // Shows an assistant activity event: status line, activity card, timeline entry and surface state. Transient events (brief progress ticks) are not added to the chat.
    private void UpdateAssistantRuntimeUi(AssistantActivityEvent activity)
    {
        _assistantStatusLabel.Text = activity.Message;
        _lastAssistantKind = activity.Kind;
        _lastAssistantDetail = string.IsNullOrWhiteSpace(activity.Detail) ? activity.Message : activity.Detail;

        if (!string.IsNullOrWhiteSpace(activity.SessionId))
        {
            _activeSessionId = activity.SessionId;
            _ = RefreshSessionSnapshotAsync();
        }

        SyncAssistantSurfaceState(activity);

        if (!_isInTrayMode
            && !string.Equals(_lastAssistantStatusText, activity.Message, StringComparison.Ordinal))
        {
            _lastAssistantStatusText = activity.Message;

            if (!activity.IsTransient)
            {
                switch (activity.Kind)
                {
                    case AssistantActivityKind.Error:
                        AppendSystemMessage(activity.Message);
                        break;

                    default:
                        AppendRuntimeMessage(activity.Message);
                        break;
                }
            }
        }

        if (ShouldCreateActivityCard(activity))
        {
            AddActivityCard(
                activity.Kind.ToString(),
                activity.Message,
                string.IsNullOrWhiteSpace(activity.Detail) ? "No extra detail supplied." : activity.Detail,
                GetActivityAccent(activity.Kind));
        }

        AppendTimelineEntry(
            activity.Kind.ToString().ToUpperInvariant(),
            activity.Message,
            string.IsNullOrWhiteSpace(activity.Detail) ? "Runtime emitted an activity signal." : TrimForSingleLine(activity.Detail, 180),
            GetActivityAccent(activity.Kind));

        if (activity.Kind == AssistantActivityKind.ToolStart)
        {
            MaybeShowActionCue(activity);
        }

        RecomputeSurfaceState();
    }

    // Briefly flashes the on-screen action cue when the assistant starts a tool that visibly controls the desktop (a fixed list of tool names).
    private void MaybeShowActionCue(AssistantActivityEvent activity)
    {
        var toolName = activity.Message.Replace("Running ", string.Empty, StringComparison.OrdinalIgnoreCase).Trim().TrimEnd('.');

        if (!new[]
            {
                "open app",
                "open path",
                "browse",
                "search web",
                "run",
                "spotify",
                "code",
                "chrome",
                "explorer",
                "office",
                "discord",
                "slack",
                "screen"
            }.Contains(toolName, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var headline = $"JARVIS {toolName.ToUpperInvariant()}";
        var detail = string.IsNullOrWhiteSpace(activity.Detail) ? activity.Message : TrimForSingleLine(activity.Detail, 120);
        _actionCueOverlay.ShowCue(Screen.FromControl(this), headline, detail, GetActivityAccent(activity.Kind));
    }

    // Maps what the assistant is doing to the header state: running a tool is ACT, planning and other steps are THINK, an error clears the override.
    // Does nothing while the reply is being typed out or when no request is in progress.
    private void SyncAssistantSurfaceState(AssistantActivityEvent activity)
    {
        if (_isAssistantResponseStreaming || !_isSubmitting)
        {
            return;
        }

        switch (activity.Kind)
        {
            case AssistantActivityKind.ToolStart:
                SetAssistantStateOverride(SurfaceState.Acting, BuildSurfaceDetail(activity));
                break;

            case AssistantActivityKind.ToolComplete:
            case AssistantActivityKind.Status:
            case AssistantActivityKind.Classification:
            case AssistantActivityKind.Planning:
            case AssistantActivityKind.Background:
            case AssistantActivityKind.Summary:
            case AssistantActivityKind.Warning:
                SetAssistantStateOverride(SurfaceState.Thinking, BuildSurfaceDetail(activity));
                break;

            case AssistantActivityKind.Error:
                ClearAssistantStateOverride();
                break;
        }
    }

    // Which activity events deserve a card in the Activity list.
    private bool ShouldCreateActivityCard(AssistantActivityEvent activity)
    {
        if (activity.Kind is AssistantActivityKind.ToolStart
            or AssistantActivityKind.ToolComplete
            or AssistantActivityKind.Summary
            or AssistantActivityKind.Warning
            or AssistantActivityKind.Error
            or AssistantActivityKind.Background
            or AssistantActivityKind.Clarification)
        {
            return true;
        }

        return !activity.IsTransient;
    }

    // "Override" means a state forced by what the assistant is doing, taking priority over the microphone-based state in RecomputeSurfaceState.
    private void SetAssistantStateOverride(SurfaceState state, string detail)
    {
        _assistantStateOverride = state;
        _assistantStateOverrideDetail = detail;
        RecomputeSurfaceState();
    }

    private void ClearAssistantStateOverride()
    {
        _assistantStateOverride = null;
        _assistantStateOverrideDetail = string.Empty;
    }

    private static string BuildSurfaceDetail(AssistantActivityEvent activity)
    {
        if (!string.IsNullOrWhiteSpace(activity.Detail))
        {
            return activity.Detail.Trim();
        }

        return string.IsNullOrWhiteSpace(activity.Message)
            ? "Processing the current directive."
            : activity.Message.Trim();
    }

    // Splits text into words (cut after each space or punctuation mark) so the reply can be revealed piece by piece.
    private static IEnumerable<string> EnumerateResponseChunks(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            yield break;
        }

        var builder = new StringBuilder();

        foreach (var character in message)
        {
            builder.Append(character);

            if (char.IsWhiteSpace(character) || char.IsPunctuation(character))
            {
                yield return builder.ToString();
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    // Milliseconds to pause after each chunk: longer after sentence ends (56), shorter after commas (34), and a bit by word length otherwise.
    private static int GetResponseChunkDelay(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return 0;
        }

        if (string.IsNullOrWhiteSpace(chunk))
        {
            return chunk.Contains('\n') ? 24 : 0;
        }

        var trimmed = chunk.TrimEnd();
        var lastCharacter = trimmed.Length == 0 ? '\0' : trimmed[^1];

        return lastCharacter switch
        {
            '.' or '!' or '?' => 56,
            ',' or ';' or ':' => 34,
            _ => Math.Clamp(14 + (trimmed.Length * 2), 18, 38)
        };
    }

    // ----- Surface state (the big IDLE / LISTENING / THINKING label) -----
    // Picks the single state to show. Highest priority first: waiting for approval, voice confirmation pending, error,
    // Jarvis speaking, a forced assistant state, waiting for the user's answer, microphone listening, otherwise idle.
    private void RecomputeSurfaceState()
    {
        var speakerStatus = _context.Speaker.CurrentStatus;

        if (_pendingApproval is not null)
        {
            UpdateSurfaceState(SurfaceState.AwaitingApproval, TrimForSingleLine(_pendingApproval.Summary, 110));
            return;
        }

        if (_pendingVoiceClarification is not null)
        {
            UpdateSurfaceState(SurfaceState.Listening, TrimForSingleLine(_pendingVoiceClarification.Prompt, 110));
            return;
        }

        if (_lastAssistantKind == AssistantActivityKind.Error)
        {
            UpdateSurfaceState(SurfaceState.Error, TrimForSingleLine(_lastAssistantDetail, 110));
            return;
        }

        if (speakerStatus.IsSpeaking)
        {
            UpdateSurfaceState(SurfaceState.Speaking, string.IsNullOrWhiteSpace(speakerStatus.Message) ? "Speaking the latest response." : TrimForSingleLine(speakerStatus.Message, 110));
            return;
        }

        if (_assistantStateOverride is SurfaceState assistantStateOverride)
        {
            var detail = string.IsNullOrWhiteSpace(_assistantStateOverrideDetail)
                ? "Processing the current directive."
                : TrimForSingleLine(_assistantStateOverrideDetail, 110);
            UpdateSurfaceState(assistantStateOverride, detail);
            return;
        }

        if (_isAwaitingFollowUpInput)
        {
            UpdateSurfaceState(
                SurfaceState.Listening,
                string.IsNullOrWhiteSpace(_awaitingFollowUpDetail)
                    ? "Awaiting the next instruction."
                    : TrimForSingleLine(_awaitingFollowUpDetail, 110));
            return;
        }

        if (_currentVoiceRuntime.CurrentStatus.IsListening)
        {
            var detail = _latestSignal.IsSpeechDetected
                ? "Voice activity detected on the active microphone."
                : TrimForSingleLine(_currentVoiceRuntime.CurrentStatus.StatusText, 110);
            UpdateSurfaceState(SurfaceState.Listening, detail);
            return;
        }

        UpdateSurfaceState(SurfaceState.Idle, "Stand by for voice, operator, or background automation.");
    }

    // Applies a state to the header (label, color, waveform accent, stage chips) and, when it really changed, logs it and plays a sound cue.
    private void UpdateSurfaceState(SurfaceState state, string detail, bool playCue = true)
    {
        if (_surfaceState == state && string.Equals(_surfaceDetailLabel.Text, detail, StringComparison.Ordinal))
        {
            return;
        }

        var previousState = _surfaceState;
        _surfaceState = state;
        _surfaceStateLabel.Text = GetSurfaceStateLabel(state);
        _surfaceStateLabel.ForeColor = GetStateAccent(state);
        _surfaceDetailLabel.Text = detail;
        _waveformControl.AccentColor = GetStateAccent(state);
        UpdateStageLabels();
        UpdateSurfaceModeButtons();

        if (playCue && previousState != state)
        {
            AppendTimelineEntry(
                "STATE",
                $"{GetSurfaceStateLabel(previousState)} -> {GetSurfaceStateLabel(state)}",
                TrimForSingleLine(detail, 180),
                GetStateAccent(state));
            PlayStateCue(state);
        }
    }

    // Highlights the header stage chip (LISTEN / THINK / ACT / SPEAK) that matches the current state; the others are dimmed.
    private void UpdateStageLabels()
    {
        foreach (var pair in _stageLabels)
        {
            var isActive = pair.Key == _surfaceState;
            pair.Value.ForeColor = isActive ? GetStateAccent(_surfaceState) : Color.FromArgb(109, 130, 152);
            pair.Value.BackColor = isActive ? Color.FromArgb(24, 42, 62) : Color.FromArgb(12, 20, 34);
            pair.Value.BorderStyle = BorderStyle.FixedSingle;
        }
    }

    // Plays a short Windows system sound for attention-worthy states (approval, acting, speaking, error). Sound failures are ignored.
    private void PlayStateCue(SurfaceState state)
    {
        try
        {
            switch (state)
            {
                case SurfaceState.AwaitingApproval:
                    SystemSounds.Question.Play();
                    break;

                case SurfaceState.Acting:
                    SystemSounds.Exclamation.Play();
                    break;

                case SurfaceState.Speaking:
                    SystemSounds.Asterisk.Play();
                    break;

                case SurfaceState.Error:
                    SystemSounds.Hand.Play();
                    break;
            }
        }
        catch
        {
        }
    }

    // ----- Approvals, activity cards and the chat/timeline feeds -----
    // Remembers the pending approval and shows or hides the approval card (132 pixels tall when shown, 0 when hidden).
    private void UpdateApprovalUi(PendingApprovalAction? pendingApproval)
    {
        _pendingApproval = pendingApproval;
        _approvalPanel.Visible = pendingApproval is not null;
        _rightColumnLayout.RowStyles[1].Height = pendingApproval is null ? 0f : 132f;

        if (pendingApproval is null)
        {
            RecomputeSurfaceState();
            return;
        }

        _approvalSummaryLabel.Text = pendingApproval.Summary;
        _approvalPromptLabel.Text = TrimForSingleLine(pendingApproval.ApprovalPrompt.ReplaceLineEndings(" "), 220);
        RecomputeSurfaceState();
    }

    // Adds one card to the Activity list. Newest goes right after the pinned summary card; the list keeps at most 11 entries
    // and disposes the oldest ones so controls do not pile up.
    private void AddActivityCard(string title, string subtitle, string detail, Color accent)
    {
        var card = new GlassPanel
        {
            Width = 10,
            Height = 104,
            Margin = new Padding(0, 0, 0, 10),
            Padding = new Padding(14, 12, 14, 12),
            SurfaceColor = Color.FromArgb(16, 28, 44),
            SurfaceColor2 = Color.FromArgb(10, 20, 34),
            AccentColor = accent,
            BorderColor = Color.FromArgb(40, 72, 102)
        };

        var titleLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 20,
            ForeColor = accent,
            Font = new Font("Bahnschrift SemiBold", 10.5f, FontStyle.Bold, GraphicsUnit.Point),
            Text = TrimForSingleLine(title, 42)
        };

        var subtitleLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            ForeColor = Color.FromArgb(231, 239, 248),
            Font = new Font("Consolas", 9.85f, FontStyle.Regular, GraphicsUnit.Point),
            Text = TrimForSingleLine(subtitle, 72)
        };

        var detailLabel = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(165, 188, 210),
            Font = new Font("Consolas", 9.25f, FontStyle.Regular, GraphicsUnit.Point),
            Text = TrimForSingleLine(detail, 120)
        };

        card.Controls.Add(detailLabel);
        card.Controls.Add(subtitleLabel);
        card.Controls.Add(titleLabel);
        detailLabel.BringToFront();

        _activityFlow.Controls.Add(card);
        _activityFlow.Controls.SetChildIndex(_sessionSummaryCard, 0);
        UpdateActivityCardWidths();

        while (_activityFlow.Controls.Count > 11)
        {
            var lastIndex = _activityFlow.Controls.Count - 1;
            var control = _activityFlow.Controls[lastIndex];

            if (ReferenceEquals(control, _sessionSummaryCard))
            {
                break;
            }

            _activityFlow.Controls.RemoveAt(lastIndex);
            control.Dispose();
        }
    }

    // Makes every card as wide as the list (minimum 250 px), leaving room for the scroll bar.
    private void UpdateActivityCardWidths()
    {
        var width = Math.Max(250, _activityFlow.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 14);
        _sessionSummaryCard.Width = width;

        foreach (var control in _activityFlow.Controls.Cast<Control>())
        {
            control.Width = width;
        }
    }

    // Adds a line from the user (voice, typed, approval) to the chat with a color depending on where it came from.
    private void AppendOperatorMessage(string source, string message)
    {
        var accent = source.Equals("VOICE", StringComparison.OrdinalIgnoreCase)
            ? Color.FromArgb(173, 216, 230)
            : source.Contains("APPROVAL", StringComparison.OrdinalIgnoreCase) || source.Contains("DENIAL", StringComparison.OrdinalIgnoreCase)
                ? Color.FromArgb(255, 138, 101)
                : Color.FromArgb(255, 191, 105);

        AppendMessage(source, message, accent);
    }

    // The three Assistant message stream methods let a reply be added to the chat history piece by piece:
    // Begin writes the name and time header, Append adds text, Complete ends the message with a blank line.
    // The flag _isAssistantHistoryStreamActive keeps any other message from landing in the middle of a streaming reply.
    private void BeginAssistantMessageStream()
    {
        if (!CanUseUi() || _isAssistantHistoryStreamActive)
        {
            return;
        }

        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        _historyBox.SelectionStart = _historyBox.TextLength;
        _historyBox.SelectionLength = 0;
        _historyBox.SelectionColor = Color.FromArgb(106, 220, 197);
        _historyBox.SelectionFont = _historySourceFont;
        _historyBox.AppendText($"[{timestamp}] {_currentOptions.AssistantName.ToUpperInvariant()}{Environment.NewLine}");
        _historyBox.SelectionColor = Color.FromArgb(236, 243, 252);
        _historyBox.SelectionFont = _historyMessageFont;
        _historyBox.ScrollToCaret();
        _isAssistantHistoryStreamActive = true;
    }

    private void AppendAssistantMessageChunk(string chunk)
    {
        if (!CanUseUi() || string.IsNullOrEmpty(chunk))
        {
            return;
        }

        if (!_isAssistantHistoryStreamActive)
        {
            BeginAssistantMessageStream();
        }

        _historyBox.SelectionStart = _historyBox.TextLength;
        _historyBox.SelectionLength = 0;
        _historyBox.SelectionColor = Color.FromArgb(236, 243, 252);
        _historyBox.SelectionFont = _historyMessageFont;
        _historyBox.AppendText(chunk);
        _historyBox.ScrollToCaret();
    }

    private void CompleteAssistantMessageStream()
    {
        if (!CanUseUi() || !_isAssistantHistoryStreamActive)
        {
            return;
        }

        _historyBox.SelectionStart = _historyBox.TextLength;
        _historyBox.SelectionLength = 0;
        _historyBox.SelectionColor = _historyBox.ForeColor;
        _historyBox.SelectionFont = _historyMessageFont;
        _historyBox.AppendText($"{Environment.NewLine}{Environment.NewLine}");
        _historyBox.ScrollToCaret();
        _isAssistantHistoryStreamActive = false;
    }

    // Adds a red-orange "SYSTEM" line (problems and notices) to the chat history.
    private void AppendSystemMessage(string message)
    {
        AppendMessage("SYSTEM", message, Color.FromArgb(255, 138, 101));
    }

    // Adds a blue "RUNTIME" line (status information) to the chat history.
    private void AppendRuntimeMessage(string message)
    {
        AppendMessage("RUNTIME", message, Color.FromArgb(132, 196, 255));
    }

    // Adds a timestamped, colored entry to the chat history. Safe to call once the window exists; it is skipped while closing.
    private void AppendMessage(string source, string message, Color accent)
    {
        if (!CanUseUi())
        {
            return;
        }

        if (_isAssistantHistoryStreamActive)
        {
            CompleteAssistantMessageStream();
        }

        var timestamp = DateTime.Now.ToString("HH:mm:ss");

        _historyBox.SelectionStart = _historyBox.TextLength;
        _historyBox.SelectionLength = 0;
        _historyBox.SelectionColor = accent;
        _historyBox.SelectionFont = _historySourceFont;
        _historyBox.AppendText($"[{timestamp}] {source}{Environment.NewLine}");
        _historyBox.SelectionColor = Color.FromArgb(236, 243, 252);
        _historyBox.SelectionFont = _historyMessageFont;
        _historyBox.AppendText($"{message}{Environment.NewLine}{Environment.NewLine}");
        _historyBox.SelectionColor = _historyBox.ForeColor;
        _historyBox.ScrollToCaret();
    }

    // Adds an entry to the Timeline box: category and time, the detail, and a dimmer "why:" line.
    private void AppendTimelineEntry(string category, string detail, string justification, Color accent)
    {
        if (!CanUseUi())
        {
            return;
        }

        var timestamp = DateTime.Now.ToString("HH:mm:ss");

        _timelineBox.SelectionStart = _timelineBox.TextLength;
        _timelineBox.SelectionLength = 0;
        _timelineBox.SelectionColor = accent;
        _timelineBox.SelectionFont = _historySourceFont;
        _timelineBox.AppendText($"[{timestamp}] {category}{Environment.NewLine}");
        _timelineBox.SelectionColor = Color.FromArgb(236, 243, 252);
        _timelineBox.SelectionFont = _historyMessageFont;
        _timelineBox.AppendText($"{detail}{Environment.NewLine}");

        if (!string.IsNullOrWhiteSpace(justification))
        {
            _timelineBox.SelectionColor = Color.FromArgb(166, 188, 210);
            _timelineBox.SelectionFont = _historyMessageFont;
            _timelineBox.AppendText($"why: {justification}{Environment.NewLine}");
        }

        _timelineBox.AppendText(Environment.NewLine);
        _timelineBox.SelectionColor = _timelineBox.ForeColor;
        _timelineBox.ScrollToCaret();
    }

    // Key press in the input box: Enter sends, Shift+Enter inserts a new line.
    private async void InputBoxOnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter || e.Shift)
        {
            return;
        }

        e.SuppressKeyPress = true;
        await SubmitPromptAsync();
    }

    // Disables the buttons and input box and shows the wait cursor while a request is being processed.
    private void SetBusyState(bool isBusy)
    {
        _sendButton.Enabled = !isBusy;
        _helpButton.Enabled = !isBusy;
        _statusButton.Enabled = !isBusy;
        _notesButton.Enabled = !isBusy;
        _memoryButton.Enabled = !isBusy;
        _approveButton.Enabled = !isBusy;
        _denyButton.Enabled = !isBusy;
        _inputBox.Enabled = !isBusy;
        Cursor = isBusy ? Cursors.WaitCursor : Cursors.Default;
    }

    // Speaks a message without ever throwing (speech problems must not break a turn); cancellation is passed on.
    // In "half-duplex" mode the microphone is turned off while Jarvis talks (so it does not hear itself) and turned on again afterwards.
    private async Task TrySpeakAsync(string message, CancellationToken cancellationToken)
    {
        var shouldPauseMicrophone = string.Equals(
                _currentOptions.SpeechRecognitionMode,
                "half-duplex",
                StringComparison.OrdinalIgnoreCase)
            && _currentVoiceRuntime.CurrentStatus.IsListening;

        try
        {
            if (shouldPauseMicrophone)
            {
                await _currentVoiceRuntime.StopAsync(cancellationToken);
            }

            await _context.Speaker.SpeakAsync(message, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }
        finally
        {
            if (shouldPauseMicrophone
                && !_cancellationTokenSource.IsCancellationRequested)
            {
                try
                {
                    await _currentVoiceRuntime.StartAsync(_cancellationTokenSource.Token);
                }
                catch
                {
                }
            }
        }
    }

    // Short voice acknowledgements are spoken only when Jarvis is hidden (tray, minimized), because otherwise the user can see the screen.
    private bool ShouldSpeakVoiceAcknowledgement(string source)
    {
        return _currentOptions.VoiceEnabled
            && source.Equals("VOICE", StringComparison.OrdinalIgnoreCase)
            && IsAssistantInBackground();
    }

    private bool IsAssistantInBackground()
    {
        return _isInTrayMode
            || !Visible
            || WindowState == FormWindowState.Minimized;
    }

    private string BuildVoiceAcknowledgement(string input)
    {
        return BuildImmediateAcknowledgement(input);
    }

    // Text shown in the response box while a request is being processed, before the real answer arrives.
    private string BuildProcessingConsoleText(string source, string renderedInput, string input)
    {
        return string.Join(
            Environment.NewLine,
            $"[{source}] {renderedInput}",
            string.Empty,
            $"[ACKNOWLEDGED] {BuildImmediateAcknowledgement(input)}",
            $"[SITUATION] {_missionBriefText}",
            "[STATUS] THINKING | Analyzing the directive and preparing the response stream.");
    }

    // Picks a short canned acknowledgement ("Checking the forecast.") from the first words of the request. No model call is involved.
    private string BuildImmediateAcknowledgement(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "I'm listening.";
        }

        if (AssistantPresenceStyle.TryBuildAcknowledgement(input, _currentOptions, out var acknowledgement))
        {
            return acknowledgement;
        }

        var normalized = input.Trim();

        if (StartsWithDirective(normalized, "research", "search web"))
        {
            return "Pulling intelligence.";
        }

        if (StartsWithDirective(normalized, "screen", "analyze image", "analyze document"))
        {
            return "Scanning the visual feed.";
        }

        if (StartsWithDirective(normalized, "weather"))
        {
            return "Checking the forecast.";
        }

        if (StartsWithDirective(normalized, "remember", "note", "remind me", "reminders"))
        {
            return "Logging it.";
        }

        if (StartsWithDirective(normalized, "computer", "system", "network", "status"))
        {
            return "Checking the system.";
        }

        if (StartsWithDirective(normalized, "browse", "chrome"))
        {
            return "Routing navigation.";
        }

        if (StartsWithDirective(normalized, "spotify", "media"))
        {
            return "Routing audio control.";
        }

        if (StartsWithDirective(normalized, "code", "office", "explorer", "word", "excel", "powerpoint", "power point", "ppt", "outlook"))
        {
            return "Opening the workspace.";
        }

        if (StartsWithDirective(normalized, "discord", "slack", "open app", "launch", "start"))
        {
            return "Opening it.";
        }

        if (StartsWithDirective(normalized, "focus app", "window state", "move window", "resize window", "snap window", "send hotkey", "click element", "type into", "get element text", "scroll"))
        {
            return "Working the desktop.";
        }

        if (StartsWithDirective(normalized, "run", "shell"))
        {
            return "Running it.";
        }

        return "On it.";
    }

    // Builds the one-line strip: local time, active application, and what Jarvis is currently doing.
    private string BuildMissionBrief(DesktopContextSnapshot? snapshot)
    {
        var timestamp = snapshot?.LocalTime.ToLocalTime() ?? DateTimeOffset.Now;
        var activeApp = snapshot is null
            ? "Unavailable"
            : !string.IsNullOrWhiteSpace(snapshot.ActiveApplication)
                ? snapshot.ActiveApplication
                : !string.IsNullOrWhiteSpace(snapshot.ActiveWindow)
                    ? snapshot.ActiveWindow
                    : "Desktop";
        var objective = ResolveMissionObjective();

        return string.Join(
            "  |  ",
            $"LOCAL {timestamp:hh:mm tt}",
            $"ACTIVE {TrimForSingleLine(activeApp, 28)}",
            $"OBJECTIVE {TrimForSingleLine(objective, 68)}");
    }

    // What the "OBJECTIVE" part of the strip says, using the same priority as the main state.
    private string ResolveMissionObjective()
    {
        if (_pendingApproval is not null)
        {
            return _pendingApproval.Summary;
        }

        if (_pendingVoiceClarification is not null)
        {
            return _pendingVoiceClarification.Prompt;
        }

        if (_isAwaitingFollowUpInput)
        {
            return string.IsNullOrWhiteSpace(_awaitingFollowUpDetail)
                ? "Awaiting follow-up input."
                : _awaitingFollowUpDetail;
        }

        if (_lastAssistantKind == AssistantActivityKind.Error)
        {
            return string.IsNullOrWhiteSpace(_lastAssistantDetail)
                ? "Recovering from a runtime fault."
                : _lastAssistantDetail;
        }

        if (_context.Speaker.CurrentStatus.IsSpeaking)
        {
            return "Delivering the latest response.";
        }

        if (_isSubmitting || _isAssistantResponseStreaming)
        {
            return string.IsNullOrWhiteSpace(_currentMissionObjective)
                ? "Processing the current directive."
                : _currentMissionObjective;
        }

        return "Stand by for the next directive.";
    }

    // Short "Execute: ..." description of a new request.
    private static string DescribeMissionObjective(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "Awaiting the next directive.";
        }

        var trimmed = TrimForSingleLine(input, 84);
        return $"Execute: {trimmed}";
    }

    // What the objective should say once a turn finishes.
    private static string BuildPostTurnObjective(AssistantTurn turn, PendingApprovalAction? pendingApproval)
    {
        if (pendingApproval is not null)
        {
            return pendingApproval.Summary;
        }

        if (turn.IsFollowUpQuestion)
        {
            return turn.ResponseText;
        }

        if (turn.ShouldExit)
        {
            return "Shutdown sequence complete.";
        }

        return "Stand by for the next directive.";
    }

    // One-line summary of the presence settings after they change.
    private static string BuildPresenceUpdateSummary(JarvisOptions options)
    {
        var nickname = string.IsNullOrWhiteSpace(options.AssistantUserNickname)
            ? "none"
            : options.AssistantUserNickname.Trim();
        var pronunciationCount = AssistantPersonaConfiguration.ResolvePronunciationHints(options).Count;

        return string.Join(
            " | ",
            $"Preset {AssistantPersonaConfiguration.ResolvePresencePreset(options)}",
            $"Style {AssistantPersonaConfiguration.ResolveResponseStyle(options)}",
            $"Small talk {(options.AssistantSmallTalkEnabled ? "enabled" : "minimal")}",
            $"Nickname {nickname}",
            $"Pronunciation hints {pronunciationCount}");
    }

    // True when the input is exactly the directive word or starts with it followed by a space (so "run" matches "run ls" but not "runway").
    private static bool StartsWithDirective(string input, params string[] directives)
    {
        foreach (var directive in directives)
        {
            if (input.Equals(directive, StringComparison.OrdinalIgnoreCase)
                || input.StartsWith(directive + " ", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // Chooses the text shown for a turn; when approval is pending it adds the approval details (see below).
    private string BuildDisplayResponse(AssistantTurn turn, PendingApprovalAction? pendingApproval)
    {
        if (pendingApproval is null)
        {
            return turn.ResponseText;
        }

        return string.Join(
            Environment.NewLine,
            "Approval required before Jarvis continues.",
            pendingApproval.Summary,
            "Use the approval card to approve or deny the action directly in the UI.");
    }

    // ----- Text builders for the summary boxes -----
    // Builds the "Plan and result" text after a turn: intent, session, tool results with their verification status, follow-up question, citations.
    private string BuildTurnSummary(AssistantTurn turn, PendingApprovalAction? pendingApproval, string renderedResponse)
    {
        var lines = new List<string>
        {
            $"Intent: {turn.Intent}",
            string.IsNullOrWhiteSpace(turn.SessionId) ? "Session: direct route" : $"Session: {ShortId(turn.SessionId)}",
            $"Follow-up required: {(turn.IsFollowUpQuestion ? "yes" : "no")}"
        };

        if (pendingApproval is not null)
        {
            lines.Add($"Approval gate: {pendingApproval.Summary}");
        }

        if (turn.ToolResults is { Length: > 0 })
        {
            lines.Add(string.Empty);
            lines.Add("Tool results:");
            lines.AddRange(turn.ToolResults.Take(4).Select(tool =>
                $"- {tool.ToolName}: {FormatToolOutcome(tool)} | {TrimForSingleLine(BuildToolSummary(tool), 72)}"));
            lines.Add($"Verification: {BuildVerificationOverview(turn.ToolResults)}");
        }

        if (turn.IsFollowUpQuestion)
        {
            lines.Add(string.Empty);
            lines.Add("Clarification:");
            lines.Add($"Question: {TrimForSingleLine(renderedResponse, 110)}");
            lines.Add("Reply guidance: answer the missing detail directly and Jarvis will continue the same workflow.");
        }

        if (turn.Citations is { Length: > 0 })
        {
            lines.Add(string.Empty);
            lines.Add($"Citations: {turn.Citations.Length}");
        }

        lines.Add(string.Empty);
        lines.Add($"Latest response: {TrimForSingleLine(renderedResponse, 120)}");
        return string.Join(Environment.NewLine, lines);
    }

    // Same kind of summary, built from a saved session (used by the periodic refresh).
    private static string BuildSessionSummary(PersistedAgentSession session)
    {
        var lines = new List<string>
        {
            $"Session: {ShortId(session.SessionId)}  |  {session.Status}  |  {session.Intent}",
            $"Updated: {session.UpdatedAtUtc.ToLocalTime():MM-dd h:mm tt}",
            $"Request: {TrimForSingleLine(session.OriginalRequest, 110)}"
        };

        if (!string.IsNullOrWhiteSpace(session.LatestInput)
            && !string.Equals(session.LatestInput, session.OriginalRequest, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"Latest input: {TrimForSingleLine(session.LatestInput, 110)}");
        }

        if (!string.IsNullOrWhiteSpace(session.FollowUpQuestion))
        {
            lines.Add(string.Empty);
            lines.Add("Clarification:");
            lines.Add($"Question: {TrimForSingleLine(session.FollowUpQuestion, 104)}");
            lines.Add("Reply guidance: answer the missing detail directly to continue the same session.");
        }

        if (session.Steps.Length > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Plan:");
            lines.AddRange(session.Steps.Take(4).Select(step =>
                $"- [{step.Status}] {step.Title}: {TrimForSingleLine(step.Detail, 72)}"));
        }

        if (session.ToolResults.Length > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Latest tools:");
            lines.AddRange(session.ToolResults.Take(4).Select(tool =>
                $"- {tool.ToolName}: {FormatToolOutcome(tool)} | {TrimForSingleLine(BuildToolSummary(tool), 68)}"));
            lines.Add($"Verification: {BuildVerificationOverview(session.ToolResults)}");
        }

        if (!string.IsNullOrWhiteSpace(session.LatestResponse))
        {
            lines.Add(string.Empty);
            lines.Add($"Latest response: {TrimForSingleLine(session.LatestResponse, 104)}");
        }

        if (!string.IsNullOrWhiteSpace(session.FollowUpQuestion))
        {
            lines.Add(string.Empty);
            lines.Add($"Follow-up: {TrimForSingleLine(session.FollowUpQuestion, 96)}");
        }

        if (session.Citations.Length > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"Citations: {session.Citations.Length}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    // A string that changes when the question or the session changes; the refresh loop compares it to avoid logging the same follow-up repeatedly.
    private static string BuildClarificationSnapshotKey(PersistedAgentSession session) =>
        string.Join(
            "|",
            session.SessionId,
            session.UpdatedAtUtc.UtcTicks.ToString(),
            session.FollowUpQuestion);

    // Response box text while Jarvis waits for the user's answer to a follow-up question.
    private static string BuildClarificationConsoleText(PersistedAgentSession session)
    {
        var lines = new List<string>
        {
            "[FOLLOW-UP REQUIRED]",
            $"Question: {session.FollowUpQuestion}",
            $"Original request: {TrimForSingleLine(session.OriginalRequest, 160)}",
            "Reply guidance: answer the missing detail directly and Jarvis will continue the same workflow."
        };

        if (!string.IsNullOrWhiteSpace(session.LatestInput)
            && !string.Equals(session.LatestInput, session.OriginalRequest, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"Latest input: {TrimForSingleLine(session.LatestInput, 160)}");
        }

        if (session.ToolResults.Length > 0)
        {
            lines.Add($"Current evidence: {BuildVerificationOverview(session.ToolResults)}");
            lines.Add("Recent tools:");
            lines.AddRange(session.ToolResults.Take(3).Select(tool =>
                $"- {tool.ToolName}: {FormatToolOutcome(tool)} | {TrimForSingleLine(BuildToolSummary(tool), 88)}"));
        }

        if (session.Steps.Length > 0)
        {
            var latestStep = session.Steps[^1];
            lines.Add($"Latest plan step: {latestStep.Title} | {TrimForSingleLine(latestStep.Detail, 120)}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    // The "why:" line for the timeline entry about a follow-up question.
    private static string BuildClarificationWhy(PersistedAgentSession session)
    {
        var parts = new List<string>
        {
            $"Original request: {TrimForSingleLine(session.OriginalRequest, 84)}."
        };

        if (session.ToolResults.Length > 0)
        {
            parts.Add($"Current evidence: {BuildVerificationOverview(session.ToolResults)}.");
        }

        if (session.Steps.Length > 0)
        {
            var latestStep = session.Steps[^1];
            parts.Add($"Latest step: {TrimForSingleLine($"{latestStep.Title} - {latestStep.Detail}", 96)}.");
        }

        parts.Add("Reply with the missing detail directly to continue the same session.");
        return string.Join(" ", parts);
    }

    // ----- Voice clarification and learned corrections -----
    // When a voice command was recognised with low confidence, Jarvis asks "did you say ...?" and parks the command in
    // _pendingVoiceClarification. The next input is checked here, in this order: yes (run it), no (cancel and remember the
    // bad transcript), an explicit correction, a retry that looks like a correction (remember it), or anything else
    // (forget the question and treat it as a fresh command). Returns true if it consumed the input.
    private async Task<bool> TryResolvePendingVoiceClarificationAsync(string input, string source, bool clearInput)
    {
        var pending = _pendingVoiceClarification;

        if (pending is null)
        {
            return false;
        }

        if (clearInput)
        {
            _inputBox.Clear();
        }

        if (VoiceRecognitionText.LooksLikeConfirmationApproval(input))
        {
            _pendingVoiceClarification = null;
            AppendTimelineEntry(
                "VOICE",
                "Low-confidence directive confirmed.",
                BuildVoiceClarificationWhy(pending.Text, pending.RawText, pending.Confidence),
                Color.FromArgb(255, 210, 132));

            var confirmationSource = source.Equals("VOICE", StringComparison.OrdinalIgnoreCase) ? "VOICE" : "YOU";
            await ProcessPromptAsync(
                pending.Text,
                confirmationSource,
                clearInput: false,
                displayedInput: $"Confirmed voice directive: {pending.Text}");
            return true;
        }

        if (VoiceRecognitionText.LooksLikeConfirmationDenial(input))
        {
            _pendingVoiceClarification = null;
            await RecordRejectedVoiceTranscriptAsync(pending.RawText, pending.Confidence);
            var message = "Voice directive canceled. Say it again when ready.";
            _liveResponseBox.Text = message;
            AppendRuntimeMessage(message);
            AppendTimelineEntry(
                "VOICE",
                "Low-confidence directive canceled.",
                BuildVoiceClarificationWhy(pending.Text, pending.RawText, pending.Confidence),
                Color.FromArgb(255, 138, 101));
            AddActivityCard("Voice", "Recognition canceled", pending.RawText, Color.FromArgb(255, 138, 101));
            RecomputeSurfaceState();

            if (_currentOptions.VoiceEnabled && source.Equals("VOICE", StringComparison.OrdinalIgnoreCase))
            {
                await TrySpeakAsync("Okay. Please say it again.", _cancellationTokenSource.Token);
            }

            return true;
        }

        if (VoiceCorrectionLearning.TryParseCorrectionDirective(input, out var explicitCorrection))
        {
            _pendingVoiceClarification = null;
            await LearnVoiceCorrectionAsync(pending.RawText, explicitCorrection.CorrectedText, pending.Confidence, source);
            AppendTimelineEntry(
                "VOICE",
                "Voice correction learned.",
                $"Stored \"{TrimForSingleLine(pending.RawText, 64)}\" -> \"{TrimForSingleLine(explicitCorrection.CorrectedText, 64)}\" for future recognition.",
                Color.FromArgb(106, 220, 197));
            await ProcessPromptAsync(
                explicitCorrection.CorrectedText,
                source.Equals("VOICE", StringComparison.OrdinalIgnoreCase) ? "VOICE" : "YOU",
                clearInput: false,
                displayedInput: $"Corrected voice directive: {explicitCorrection.CorrectedText}");
            return true;
        }

        if (source.Equals("VOICE", StringComparison.OrdinalIgnoreCase)
            && VoiceCorrectionLearning.CanLearnCorrection(pending.RawText, input))
        {
            _pendingVoiceClarification = null;
            await LearnVoiceCorrectionAsync(pending.RawText, input, pending.Confidence, source);
            AppendTimelineEntry(
                "VOICE",
                "Voice correction learned from the retry.",
                $"Stored \"{TrimForSingleLine(pending.RawText, 64)}\" -> \"{TrimForSingleLine(input, 64)}\" for future recognition.",
                Color.FromArgb(106, 220, 197));
            await ProcessPromptAsync(
                input,
                "VOICE",
                clearInput: false,
                displayedInput: $"Corrected voice directive: {input}");
            return true;
        }

        _pendingVoiceClarification = null;
        AppendTimelineEntry(
            "VOICE",
            "Voice clarification dismissed.",
            "Received a new directive instead of a yes or no confirmation, so Jarvis will route the latest input.",
            Color.FromArgb(255, 191, 105));
        return false;
    }

    // Shows and speaks the "did you mean" question and stores the command until the user answers.
    private async Task PromptForVoiceClarificationAsync(VoiceCommandRecognizedEventArgs eventArgs)
    {
        var prompt = string.IsNullOrWhiteSpace(eventArgs.ClarificationPrompt)
            ? VoiceRecognitionConfidence.BuildClarificationPrompt(eventArgs.RawText, eventArgs.Confidence)
            : eventArgs.ClarificationPrompt;

        _pendingVoiceClarification = new PendingVoiceClarification(eventArgs.Text, eventArgs.RawText, prompt, eventArgs.Confidence);
        _lastAssistantKind = AssistantActivityKind.Clarification;
        _lastAssistantDetail = prompt;
        _promptEchoLabel.Text = $"VOICE CHECK  |  {TrimForSingleLine(eventArgs.RawText, 132)}";
        _liveResponseBox.Text = prompt;
        AppendRuntimeMessage(prompt);
        AppendTimelineEntry(
            "VOICE",
            "Recognition needs confirmation.",
            BuildVoiceClarificationWhy(eventArgs.Text, eventArgs.RawText, eventArgs.Confidence),
            Color.FromArgb(255, 210, 132));
        AddActivityCard(
            "Voice",
            "Low-confidence recognition",
            TrimForSingleLine(eventArgs.RawText, 108),
            Color.FromArgb(255, 210, 132));
        RecomputeSurfaceState();

        if (_currentOptions.VoiceEnabled)
        {
            await TrySpeakAsync(prompt, _cancellationTokenSource.Token);
        }
    }

    // Runs a command that was queued while Jarvis was busy; low-confidence ones still ask for confirmation first.
    private Task RouteQueuedVoiceDirectiveAsync(QueuedVoiceDirective directive)
    {
        var eventArgs = directive.ToEventArgs();
        return eventArgs.RequiresClarification
            ? PromptForVoiceClarificationAsync(eventArgs)
            : ProcessPromptAsync(eventArgs.Text, "VOICE", clearInput: false);
    }

    // Handles spoken or typed commands about corrections themselves: "review corrections" shows what was learned, and
    // "X means Y" style directives store a correction. Returns true if the input was one of these.
    private async Task<bool> TryHandleVoiceCorrectionDirectiveAsync(string input, string source, bool clearInput)
    {
        if (VoiceCorrectionLearning.IsReviewCommand(input))
        {
            if (clearInput)
            {
                _inputBox.Clear();
            }

            var review = VoiceCorrectionLearning.BuildReview(_currentOptions);
            _promptEchoLabel.Text = "VOICE CORRECTIONS";
            _liveResponseBox.Text = review;
            AppendRuntimeMessage(review);
            AppendTimelineEntry(
                "VOICE",
                "Displayed learned voice corrections.",
                "Reviewed the stored correction pairs and rejected transcripts.",
                Color.FromArgb(132, 196, 255));
            return true;
        }

        if (!VoiceCorrectionLearning.TryParseCorrectionDirective(input, out var correction))
        {
            return false;
        }

        if (clearInput)
        {
            _inputBox.Clear();
        }

        await LearnVoiceCorrectionAsync(correction.HeardText, correction.CorrectedText, null, source);

        var message = $"Learned voice correction: \"{correction.HeardText}\" -> \"{correction.CorrectedText}\".";
        _promptEchoLabel.Text = "VOICE CORRECTION SAVED";
        _liveResponseBox.Text = message;
        AppendRuntimeMessage(message);
        AppendTimelineEntry(
            "VOICE",
            "Voice correction stored.",
            $"Future recognition will bias \"{TrimForSingleLine(correction.HeardText, 56)}\" toward \"{TrimForSingleLine(correction.CorrectedText, 56)}\".",
            Color.FromArgb(106, 220, 197));
        AddActivityCard("Voice", "Correction learned", correction.CorrectedText, Color.FromArgb(106, 220, 197));
        RecomputeSurfaceState();
        return true;
    }

    // Stores a (heard -> meant) pair in the settings and reloads the voice runtime so future recognition favors it.
    private async Task LearnVoiceCorrectionAsync(string heardText, string correctedText, double? confidence, string source)
    {
        var updatedOptions = VoiceCorrectionLearning.RecordCorrection(_currentOptions, heardText, correctedText, confidence);

        if (ReferenceEquals(updatedOptions, _currentOptions)
            || (updatedOptions.SpeechRecognitionCorrections.SequenceEqual(_currentOptions.SpeechRecognitionCorrections)
                && updatedOptions.SpeechRecognitionRejectedPhrases.SequenceEqual(_currentOptions.SpeechRecognitionRejectedPhrases)))
        {
            return;
        }

        _currentOptions = updatedOptions;
        await PersistCurrentOptionsAsync();
        await ReloadVoiceRuntimeAsync(_currentOptions);

        if (_currentOptions.VoiceEnabled && source.Equals("VOICE", StringComparison.OrdinalIgnoreCase))
        {
            await TrySpeakAsync("Learned it. I will bias future transcripts toward that wording.", _cancellationTokenSource.Token);
        }
    }

    // Remembers a transcript the user rejected so it can be avoided later.
    private async Task RecordRejectedVoiceTranscriptAsync(string heardText, double? confidence)
    {
        var updatedOptions = VoiceCorrectionLearning.RecordRejectedPhrase(_currentOptions, heardText, confidence);

        if (ReferenceEquals(updatedOptions, _currentOptions)
            || updatedOptions.SpeechRecognitionRejectedPhrases.SequenceEqual(_currentOptions.SpeechRecognitionRejectedPhrases))
        {
            return;
        }

        _currentOptions = updatedOptions;
        await PersistCurrentOptionsAsync();
    }

    // Explains in plain words what was heard, how confident recognition was, and what Jarvis would have done.
    private static string BuildVoiceClarificationWhy(string text, string rawText, double? confidence)
    {
        var confidenceText = confidence is double value ? $"{value:P0}" : "unknown";
        var heardText = string.IsNullOrWhiteSpace(rawText) ? text : rawText;

        if (string.Equals(heardText, text, StringComparison.OrdinalIgnoreCase))
        {
            return $"Jarvis heard \"{TrimForSingleLine(heardText, 96)}\" with {confidenceText} confidence and is waiting for confirmation or a correction before acting.";
        }

        return $"Jarvis heard \"{TrimForSingleLine(heardText, 72)}\" with {confidenceText} confidence. The routed directive would be \"{TrimForSingleLine(text, 72)}\" unless you confirm or correct it.";
    }

    // ----- Small formatting helpers -----
    // Color for each kind of assistant activity in the timeline and activity cards.
    private static Color GetActivityAccent(AssistantActivityKind kind) =>
        kind switch
        {
            AssistantActivityKind.Error => Color.FromArgb(255, 138, 101),
            AssistantActivityKind.Warning => Color.FromArgb(255, 191, 105),
            AssistantActivityKind.ToolStart => Color.FromArgb(132, 196, 255),
            AssistantActivityKind.ToolComplete => Color.FromArgb(106, 220, 197),
            AssistantActivityKind.Summary => Color.FromArgb(106, 220, 197),
            AssistantActivityKind.Background => Color.FromArgb(186, 150, 255),
            AssistantActivityKind.Clarification => Color.FromArgb(255, 210, 132),
            _ => Color.FromArgb(173, 216, 230)
        };

    // One word describing a tool's result: its verification status if it has one, otherwise "observed" or "failed".
    private static string FormatToolOutcome(AssistantToolExecution tool)
    {
        var status = string.IsNullOrWhiteSpace(tool.VerificationStatus)
            ? tool.Succeeded ? "observed" : "failed"
            : tool.VerificationStatus.Replace('_', ' ').Trim().ToLowerInvariant();
        return status;
    }

    // Best available one-line description of a tool result: verification summary, else summary, else verification text.
    private static string BuildToolSummary(AssistantToolExecution tool)
    {
        if (!string.IsNullOrWhiteSpace(tool.VerificationSummary))
        {
            return tool.VerificationSummary;
        }

        if (!string.IsNullOrWhiteSpace(tool.Summary))
        {
            return tool.Summary;
        }

        return tool.VerificationText;
    }

    // Counts tool results by outcome, for example "2 verified | 1 failed", in a fixed order with unknown statuses last.
    private static string BuildVerificationOverview(IReadOnlyList<AssistantToolExecution> toolResults)
    {
        if (toolResults.Count == 0)
        {
            return "no tool evidence";
        }

        var orderedStatuses = new[]
        {
            "verified",
            "observed",
            "awaiting approval",
            "unconfirmed",
            "failed"
        };
        var counts = toolResults
            .GroupBy(tool => FormatToolOutcome(tool), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var parts = new List<string>();

        foreach (var status in orderedStatuses)
        {
            if (counts.TryGetValue(status, out var count) && count > 0)
            {
                parts.Add($"{count} {status}");
            }
        }

        foreach (var extra in counts.Keys
                     .Where(key => !orderedStatuses.Contains(key, StringComparer.OrdinalIgnoreCase))
                     .OrderBy(key => key, StringComparer.OrdinalIgnoreCase))
        {
            parts.Add($"{counts[extra]} {extra}");
        }

        return parts.Count == 0 ? "no tool evidence" : string.Join(" | ", parts);
    }

    // Header text for each state.
    private static string GetSurfaceStateLabel(SurfaceState state) =>
        state switch
        {
            SurfaceState.Listening => "LISTENING",
            SurfaceState.Thinking => "THINKING",
            SurfaceState.Acting => "ACTING",
            SurfaceState.Speaking => "SPEAKING",
            SurfaceState.AwaitingApproval => "AWAITING APPROVAL",
            SurfaceState.Error => "RUNTIME ALERT",
            _ => "IDLE"
        };

    // Header color for each state.
    private static Color GetStateAccent(SurfaceState state) =>
        state switch
        {
            SurfaceState.Listening => Color.FromArgb(106, 220, 197),
            SurfaceState.Thinking => Color.FromArgb(132, 196, 255),
            SurfaceState.Acting => Color.FromArgb(255, 191, 105),
            SurfaceState.Speaking => Color.FromArgb(255, 138, 101),
            SurfaceState.AwaitingApproval => Color.FromArgb(255, 112, 102),
            SurfaceState.Error => Color.FromArgb(255, 123, 114),
            _ => Color.FromArgb(170, 191, 214)
        };

    // Flattens line breaks and cuts text to maxLength characters, adding "..." so it fits one line.
    private static string TrimForSingleLine(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var flattened = value.ReplaceLineEndings(" ").Trim();
        return flattened.Length <= maxLength ? flattened : flattened[..maxLength] + "...";
    }

    // First 8 characters of a session id, or "n/a" when empty.
    private static string ShortId(string sessionId)
    {
        return string.IsNullOrWhiteSpace(sessionId)
            ? "n/a"
            : sessionId.Length <= 8
                ? sessionId
                : sessionId[..8];
    }
}
