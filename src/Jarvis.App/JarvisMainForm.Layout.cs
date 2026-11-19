// JarvisMainForm.Layout.cs: how the window looks. Shared fonts and colors, one Build...Card method per panel,
// the five window modes (Operator, Voice, Overlay, Edge, Dashboard) and factory helpers for styled controls.
// InitializeSurface (JarvisMainForm.cs) calls it once; ApplySurfaceMode also runs on every mode switch.
using Jarvis.Core;
using Jarvis.App;
using System.Windows.Forms;
using System.Drawing;

namespace Jarvis.App;

internal sealed partial class JarvisMainForm
{
    // Bahnschrift for the interface, Consolas (monospace) for Jarvis's own words.
    private static readonly Font FontTitle = new("Bahnschrift SemiBold", 26f, FontStyle.Bold, GraphicsUnit.Point);
    private static readonly Font FontState = new("Bahnschrift SemiBold", 17f, FontStyle.Bold, GraphicsUnit.Point);
    private static readonly Font FontHeading = new("Bahnschrift SemiBold", 11f, FontStyle.Bold, GraphicsUnit.Point);
    private static readonly Font FontLabel = new("Bahnschrift SemiBold", 10f, FontStyle.Bold, GraphicsUnit.Point);
    private static readonly Font FontBody = new("Bahnschrift", 10f, FontStyle.Regular, GraphicsUnit.Point);
    private static readonly Font FontMonoLarge = new("Consolas", 11.5f, FontStyle.Regular, GraphicsUnit.Point);
    private static readonly Font FontMono = new("Consolas", 10f, FontStyle.Regular, GraphicsUnit.Point);
    private static readonly Font FontMonoBold = new("Consolas", 10f, FontStyle.Bold, GraphicsUnit.Point);
    private static readonly Font FontMonoSmall = new("Consolas", 9.5f, FontStyle.Regular, GraphicsUnit.Point);

    private static readonly Color Ink = Color.FromArgb(237, 244, 252);
    private static readonly Color InkSoft = Color.FromArgb(183, 204, 223);
    private static readonly Color InkMuted = Color.FromArgb(150, 172, 194);
    private static readonly Color Well = Color.FromArgb(10, 18, 30);
    private static readonly Color Raised = Color.FromArgb(16, 34, 52);
    private static readonly Color RaisedActive = Color.FromArgb(24, 44, 64);
    private static readonly Color Border = Color.FromArgb(40, 72, 102);
    private static readonly Color Teal = Color.FromArgb(106, 220, 197);
    private static readonly Color Amber = Color.FromArgb(255, 191, 105);
    private static readonly Color Coral = Color.FromArgb(255, 138, 101);
    private static readonly Color Sky = Color.FromArgb(132, 196, 255);

    // Controls that other slices need to reach later (show/hide, resize, restyle).
    private Panel _tuningPanel = null!;
    private GlassPanel _headerPanel = null!;
    private TransparentTableLayoutPanel _headerLayout = null!;
    private TransparentFlowLayoutPanel _chipFlow = null!;
    private TransparentFlowLayoutPanel _appActionFlow = null!;
    private Button _fullViewButton = null!;

    // Builds the top bar: assistant name, current state (IDLE, LISTENING...), detail line, mission strip, stage chips
    // on the left, and the window-mode buttons plus Help/Status/Notes/Memory/Presence buttons on the right.
    private GlassPanel BuildHeaderPanel()
    {
        var headerPanel = _headerPanel = new GlassPanel
        {
            Dock = DockStyle.Top,
            Height = 224,
            Margin = Padding.Empty,
            Padding = new Padding(24, 16, 24, 16),
            SurfaceColor = Color.FromArgb(14, 24, 39),
            SurfaceColor2 = Color.FromArgb(7, 16, 28),
            BorderColor = Border,
            AccentColor = Teal
        };

        var layout = _headerLayout = new TransparentTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1
        };
        // Left side takes 62% of the width, the mode buttons 38%.
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38f));

        var leftLayout = new TransparentTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Margin = Padding.Empty
        };
        for (var i = 0; i < 5; i++)
        {
            leftLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
        // One full-width column, so the single-line detail label can stretch to it.
        leftLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        var titleLabel = new Label
        {
            AutoSize = true,
            ForeColor = Ink,
            Font = FontTitle,
            Margin = new Padding(0, 0, 0, 2),
            Text = _currentOptions.AssistantName.ToUpperInvariant()
        };

        _surfaceStateLabel = new Label
        {
            AutoSize = true,
            ForeColor = Amber,
            Font = FontState,
            Text = "IDLE"
        };

        _surfaceDetailLabel = new Label
        {
            // Anchored left+right stretches to the cell; Dock.Fill left it at the 100 px default here.
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Width = 900,
            Height = 22,
            AutoEllipsis = true,
            ForeColor = InkSoft,
            Font = FontMono,
            Margin = new Padding(0, 2, 0, 0),
            Text = "Stand by."
        };

        _missionBriefLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(163, 201, 233),
            Font = FontMonoSmall,
            MaximumSize = new Size(820, 0),
            Margin = new Padding(0, 4, 0, 0),
            Text = _missionBriefText
        };

        // Capability chips and the listen/think/act/speak stages share one row so the header stays short.
        var chipFlow = _chipFlow = new TransparentFlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0, 10, 0, 0)
        };
        AddStageLabel(chipFlow, SurfaceState.Listening, "LISTEN");
        AddStageLabel(chipFlow, SurfaceState.Thinking, "THINK");
        AddStageLabel(chipFlow, SurfaceState.Acting, "ACT");
        AddStageLabel(chipFlow, SurfaceState.Speaking, "SPEAK");
        chipFlow.Controls.Add(CreateInfoChip(_currentOptions.VoiceEnabled ? "VOICE ON" : "VOICE OFF", Amber));
        chipFlow.Controls.Add(CreateInfoChip(_currentOptions.SpeechRecognitionEnabled ? "LIVE MIC" : "MIC OFF", Teal));
        chipFlow.Controls.Add(CreateInfoChip(_currentOptions.WakeWordEnabled ? "WAKE GATE" : "OPEN MIC", Sky));
        chipFlow.Controls.Add(CreateInfoChip(_currentOptions.ShellExecutionEnabled ? "TOOLS LIVE" : "SHELL LOCKED", Coral));

        leftLayout.Controls.Add(titleLabel, 0, 0);
        leftLayout.Controls.Add(_surfaceStateLabel, 0, 1);
        leftLayout.Controls.Add(_surfaceDetailLabel, 0, 2);
        leftLayout.Controls.Add(_missionBriefLabel, 0, 3);
        leftLayout.Controls.Add(chipFlow, 0, 4);

        var rightLayout = new TransparentTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty
        };
        rightLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rightLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rightLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rightLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        // Right half of the header: window mode picker and quick action buttons.
        var modeHeader = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(200, 216, 232),
            Font = FontHeading,
            Text = "Window mode"
        };

        _modeStatusLabel = new Label
        {
            AutoSize = true,
            ForeColor = InkMuted,
            Font = FontBody,
            MaximumSize = new Size(480, 0),
            Text = "Operator console on the primary display."
        };

        var modeFlow = new TransparentFlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0, 8, 0, 0)
        };
        AddModeButton(modeFlow, SurfaceMode.OperatorConsole, "Operator");
        AddModeButton(modeFlow, SurfaceMode.VoiceOnly, "Voice");
        AddModeButton(modeFlow, SurfaceMode.OverlayHud, "Overlay");
        AddModeButton(modeFlow, SurfaceMode.EdgeDock, "Edge");
        AddModeButton(modeFlow, SurfaceMode.Dashboard, "Dashboard");

        var appActionFlow = _appActionFlow = CreateRowFlow();
        appActionFlow.Margin = new Padding(0, 2, 0, 0);
        _helpButton = CreateActionButton("Help");
        _statusButton = CreateActionButton("Status");
        _notesButton = CreateActionButton("Notes");
        _memoryButton = CreateActionButton("Memory");
        _presenceButton = CreateActionButton("Presence");
        appActionFlow.Controls.Add(_helpButton);
        appActionFlow.Controls.Add(_statusButton);
        appActionFlow.Controls.Add(_notesButton);
        appActionFlow.Controls.Add(_memoryButton);
        appActionFlow.Controls.Add(_presenceButton);

        rightLayout.Controls.Add(modeHeader, 0, 0);
        rightLayout.Controls.Add(_modeStatusLabel, 0, 1);
        rightLayout.Controls.Add(modeFlow, 0, 2);
        rightLayout.Controls.Add(appActionFlow, 0, 3);

        layout.Controls.Add(leftLayout, 0, 0);
        layout.Controls.Add(rightLayout, 1, 0);
        headerPanel.Controls.Add(layout);
        return headerPanel;
    }

    // Builds the large "Response" card on the left: the latest reply text plus two status lines for the assistant and the voice system.
    private GlassPanel BuildLiveConsoleCard()
    {
        var card = CreateSurfaceCard();

        var titleLabel = CreateSectionTitle("Response");
        titleLabel.Dock = DockStyle.Fill;

        // Compact modes hide the header's mode buttons, so this toggles between the compact voice window and the full console.
        // Always visible: toggling Visible during a border-style change left it unpainted.
        _fullViewButton = CreateActionButton("Compact");
        _fullViewButton.Dock = DockStyle.Right;
        _fullViewButton.AutoSize = false;
        _fullViewButton.Width = 124;
        _fullViewButton.Margin = Padding.Empty;
        _fullViewButton.Click += (_, _) => ApplySurfaceMode(
            _surfaceMode is SurfaceMode.OperatorConsole or SurfaceMode.Dashboard
                ? SurfaceMode.VoiceOnly
                : SurfaceMode.OperatorConsole);

        var titleRow = new TransparentPanel
        {
            Dock = DockStyle.Top,
            Height = 30
        };
        titleRow.Controls.Add(titleLabel);
        titleRow.Controls.Add(_fullViewButton);
        titleLabel.BringToFront();

        _promptEchoLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            ForeColor = Amber,
            Font = FontMonoBold,
            AutoEllipsis = true,
            Text = "LATEST DIRECTIVE  |  Awaiting input."
        };

        var statusStack = new TransparentPanel
        {
            Dock = DockStyle.Bottom,
            Height = 48
        };

        _assistantStatusLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            ForeColor = Color.FromArgb(163, 201, 233),
            Font = FontMonoSmall,
            AutoEllipsis = true,
            Text = "Assistant runtime idle."
        };

        _voiceStatusLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            ForeColor = InkMuted,
            Font = FontMonoSmall,
            AutoEllipsis = true,
            Text = "Speech recognition initializing."
        };

        statusStack.Controls.Add(_assistantStatusLabel);
        statusStack.Controls.Add(_voiceStatusLabel);

        _liveResponseBox = CreateTelemetryBox();
        _liveResponseBox.Dock = DockStyle.Fill;
        _liveResponseBox.Font = FontMonoLarge;
        _liveResponseBox.Text = "Response renderer ready.";

        card.Controls.Add(_liveResponseBox);
        card.Controls.Add(statusStack);
        card.Controls.Add(_promptEchoLabel);
        card.Controls.Add(titleRow);
        titleRow.BringToFront();
        _promptEchoLabel.BringToFront();
        // Docked controls are laid out from the highest index down, so the Fill control goes to index 0
        // or it grabs the whole card and the title paints over its top.
        _liveResponseBox.BringToFront();
        return card;
    }

    // Builds the "Conversation" card: the scrolling chat history.
    private Panel BuildHistoryCard()
    {
        var card = CreateSurfaceCard();
        var titleLabel = CreateSectionTitle("Conversation");
        titleLabel.Dock = DockStyle.Top;

        _historyBox = CreateTelemetryBox();
        _historyBox.Dock = DockStyle.Fill;
        _historyBox.Font = _historyMessageFont;

        card.Controls.Add(_historyBox);
        card.Controls.Add(titleLabel);
        _historyBox.BringToFront();
        return card;
    }

    // Builds the "Microphone" card: waveform, level readouts, input device picker, mute button
    // and a collapsible tuning area (calibrate, validate, wake word and engine pickers, volume, whisper mode).
    private GlassPanel BuildSignalCard()
    {
        // This card sizes itself (the right column's first row is AutoSize), so what it shows decides
        // how much room Activity and Timeline get below it. Tuning stays folded unless asked for.
        var card = CreateSurfaceCard();
        card.AutoSize = true;
        card.AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var titleLabel = CreateSectionTitle("Microphone");
        titleLabel.Dock = DockStyle.Top;

        _waveformControl = new SignalWaveformControl
        {
            Dock = DockStyle.Top,
            Height = 72,
            Margin = new Padding(0, 8, 0, 8)
        };

        var metricsFlow = CreateRowFlow();
        _rmsLabel = CreateMetricLabel("RMS 0%");
        _peakLabel = CreateMetricLabel("PEAK 0%");
        _listenIndicatorLabel = CreateMetricLabel("LISTEN STANDBY");
        _wakeIndicatorLabel = CreateMetricLabel(_currentOptions.WakeWordEnabled ? "WAKE GATE" : "OPEN MIC");
        metricsFlow.Controls.Add(_rmsLabel);
        metricsFlow.Controls.Add(_peakLabel);
        metricsFlow.Controls.Add(_listenIndicatorLabel);
        metricsFlow.Controls.Add(_wakeIndicatorLabel);

        // Input device row: choose the microphone and rescan.
        var deviceFlow = CreateRowFlow();
        _deviceComboBox = CreateComboBox(230);
        _deviceRefreshButton = CreateActionButton("Refresh");
        deviceFlow.Controls.Add(CreateFieldLabel("Input"));
        deviceFlow.Controls.Add(_deviceComboBox);
        deviceFlow.Controls.Add(_deviceRefreshButton);

        // Mute button and Tuning toggle.
        var actionFlow = CreateRowFlow();
        _micToggleButton = CreateActionButton("Mute Mic");
        var tuningToggle = CreateActionButton("Tuning");
        actionFlow.Controls.Add(_micToggleButton);
        actionFlow.Controls.Add(tuningToggle);

        // Tuning area, hidden until the Tuning button is pressed.
        var tuningFlow = CreateRowFlow();
        tuningFlow.Margin = new Padding(0, 6, 0, 0);
        _calibrateMicButton = CreateActionButton("Calibrate");
        _validateMicButton = CreateActionButton("Validate");
        tuningFlow.Controls.Add(_calibrateMicButton);
        tuningFlow.Controls.Add(_validateMicButton);
        _beamformingProfileComboBox = CreateComboBox(150);
        _hardwareDspProfileComboBox = CreateComboBox(140);
        _wakeWordAssetComboBox = CreateComboBox(170);
        _wakeEngineComboBox = CreateComboBox(170);
        _calibrationStatusLabel = CreateStatusLabel("No calibration saved.");
        _validationStatusLabel = CreateStatusLabel("No validation saved.");
        tuningFlow.Controls.Add(CreateFieldLabel("Beam"));
        tuningFlow.Controls.Add(_beamformingProfileComboBox);
        tuningFlow.Controls.Add(CreateFieldLabel("DSP"));
        tuningFlow.Controls.Add(_hardwareDspProfileComboBox);
        tuningFlow.Controls.Add(CreateFieldLabel("Wake"));
        tuningFlow.Controls.Add(_wakeWordAssetComboBox);
        tuningFlow.Controls.Add(CreateFieldLabel("Engine"));
        tuningFlow.Controls.Add(_wakeEngineComboBox);
        tuningFlow.Controls.Add(_calibrationStatusLabel);
        tuningFlow.Controls.Add(_validationStatusLabel);

        // Speaker row: volume slider (10% to 200%) and the quiet "Whisper" voice mode checkbox.
        var speakerFlow = CreateRowFlow();
        var volumeTrackBar = new TrackBar
        {
            Width = 140,
            Height = 28,
            Minimum = 10,
            Maximum = 200,
            Value = Math.Clamp((int)Math.Round(_currentOptions.SpeakerVolumeMultiplier * 100), 10, 200),
            TickStyle = TickStyle.None,
            Margin = new Padding(0, 6, 8, 0)
        };
        // Saves the new volume when the slider moves; errors are shown in the chat.
        volumeTrackBar.ValueChanged += async (_, _) =>
        {
            try
            {
                await UpdateSpeakerVolumeAsync(volumeTrackBar.Value / 100.0);
            }
            catch (Exception exception)
            {
                AppendSystemMessage($"Speaker volume update failed: {exception.Message}");
            }
        };
        var whisperCheckBox = new CheckBox
        {
            AutoSize = true,
            ForeColor = Ink,
            Font = FontBody,
            Text = "Whisper",
            Checked = _currentOptions.WhisperModeEnabled,
            Margin = new Padding(0, 8, 0, 0)
        };
        whisperCheckBox.CheckedChanged += async (_, _) =>
        {
            try
            {
                await UpdateWhisperModeAsync(whisperCheckBox.Checked);
            }
            catch (Exception exception)
            {
                AppendSystemMessage($"Whisper mode update failed: {exception.Message}");
            }
        };
        speakerFlow.Controls.Add(CreateFieldLabel("Volume"));
        speakerFlow.Controls.Add(volumeTrackBar);
        speakerFlow.Controls.Add(whisperCheckBox);

        // The folded-away tuning area. Visible is toggled by the Tuning button below.
        _tuningPanel = new TransparentPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Visible = false
        };
        // Docked controls stack from the last-added outward, so add bottom-most first.
        _tuningPanel.Controls.Add(speakerFlow);
        _tuningPanel.Controls.Add(tuningFlow);
        tuningToggle.Click += (_, _) =>
        {
            _tuningPanel.Visible = !_tuningPanel.Visible;
            tuningToggle.Text = _tuningPanel.Visible ? "Hide tuning" : "Tuning";
        };

        card.Controls.Add(_tuningPanel);
        card.Controls.Add(actionFlow);
        card.Controls.Add(deviceFlow);
        card.Controls.Add(metricsFlow);
        card.Controls.Add(_waveformControl);
        card.Controls.Add(titleLabel);
        return card;
    }

    // Builds the Approval card, hidden until Jarvis wants permission for a major action. Approve and Deny are wired in HookUiEvents.
    private Panel BuildApprovalCard()
    {
        var card = CreateSurfaceCard();
        card.Visible = false;

        var titleLabel = CreateSectionTitle("Approval needed");
        titleLabel.Dock = DockStyle.Top;
        titleLabel.ForeColor = Coral;

        _approvalSummaryLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 30,
            ForeColor = Color.FromArgb(255, 215, 200),
            Font = FontHeading,
            AutoEllipsis = true,
            Text = "A major action is waiting for approval."
        };

        _approvalPromptLabel = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = InkSoft,
            Font = FontMono,
            Text = "Review the planned change.",
            AutoEllipsis = true
        };

        var buttonFlow = new TransparentFlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 42
        };
        _approveButton = CreateAccentButton("Approve", Teal);
        _denyButton = CreateAccentButton("Deny", Coral);
        buttonFlow.Controls.Add(_approveButton);
        buttonFlow.Controls.Add(_denyButton);

        card.Controls.Add(_approvalPromptLabel);
        card.Controls.Add(buttonFlow);
        card.Controls.Add(_approvalSummaryLabel);
        card.Controls.Add(titleLabel);
        _approvalPromptLabel.BringToFront();
        return card;
    }

    // Builds the "Activity" card: a plan-and-result summary box with a background-queue line beneath it.
    private GlassPanel BuildActivitiesCard()
    {
        var card = CreateSurfaceCard();
        var titleLabel = CreateSectionTitle("Activity");
        titleLabel.Dock = DockStyle.Top;

        _activityFlow = new BufferedFlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 8, 4, 0)
        };
        _activityFlow.Resize += (_, _) => UpdateActivityCardWidths();

        _sessionSummaryCard = new GlassPanel
        {
            Width = 10,
            Height = 118,
            Margin = new Padding(0, 0, 0, 10),
            Padding = new Padding(14),
            SurfaceColor = Color.FromArgb(18, 31, 50),
            SurfaceColor2 = Color.FromArgb(11, 22, 38),
            AccentColor = Sky,
            BorderColor = Border
        };

        var sessionTitle = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            ForeColor = Color.FromArgb(208, 226, 244),
            Font = FontHeading,
            Text = "Plan and result"
        };

        _backgroundLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 36,
            ForeColor = InkMuted,
            Font = FontMonoSmall,
            Text = "Background queue clear.",
            AutoEllipsis = true
        };

        _sessionBox = CreateTelemetryBox();
        _sessionBox.Dock = DockStyle.Fill;
        _sessionBox.Font = FontMonoSmall;
        _sessionBox.Text = _lastSessionSummary;

        _sessionSummaryCard.Controls.Add(_sessionBox);
        _sessionSummaryCard.Controls.Add(_backgroundLabel);
        _sessionSummaryCard.Controls.Add(sessionTitle);
        _sessionBox.BringToFront();

        _activityFlow.Controls.Add(_sessionSummaryCard);

        card.Controls.Add(_activityFlow);
        card.Controls.Add(titleLabel);
        _activityFlow.BringToFront();
        return card;
    }

    // Builds the "Timeline" card: a log of what happened (surface changes, commands, errors).
    private GlassPanel BuildTimelineCard()
    {
        var card = CreateSurfaceCard();
        var titleLabel = CreateSectionTitle("Timeline");
        titleLabel.Dock = DockStyle.Top;

        _timelineBox = CreateTelemetryBox();
        _timelineBox.Dock = DockStyle.Fill;
        _timelineBox.Font = FontMonoSmall;

        card.Controls.Add(_timelineBox);
        card.Controls.Add(titleLabel);
        _timelineBox.BringToFront();
        return card;
    }

    // Builds the bottom input area: a text box for typed requests and the Send button.
    private TransparentPanel BuildComposerPanel()
    {
        var wrapper = new TransparentPanel
        {
            Dock = DockStyle.Bottom,
            Height = 150,
            Padding = new Padding(22, 8, 22, 20)
        };

        var card = CreateSurfaceCard();
        card.Dock = DockStyle.Fill;
        card.Padding = new Padding(18, 12, 18, 14);

        var promptLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            ForeColor = InkMuted,
            Font = FontLabel,
            Text = "Type a request, or just talk."
        };

        _inputBox = new TextBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            Multiline = true,
            AcceptsReturn = true,
            BackColor = Well,
            ForeColor = Ink,
            Font = new Font("Bahnschrift", 12f, FontStyle.Regular, GraphicsUnit.Point),
            Margin = new Padding(0, 0, 12, 0)
        };

        _sendButton = CreateAccentButton("Send", Teal);
        _sendButton.AutoSize = false;
        _sendButton.Dock = DockStyle.Fill;
        _sendButton.Margin = Padding.Empty;

        var editorLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        editorLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        editorLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130f));
        editorLayout.Controls.Add(_inputBox, 0, 0);
        editorLayout.Controls.Add(_sendButton, 1, 0);

        var editorHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Well,
            Padding = new Padding(12, 10, 10, 10)
        };

        editorHost.Controls.Add(editorLayout);
        card.Controls.Add(editorHost);
        card.Controls.Add(promptLabel);
        editorHost.BringToFront();
        wrapper.Controls.Add(card);
        return wrapper;
    }

    // Adds one window-mode button; clicking it switches the whole window layout.
    private void AddModeButton(FlowLayoutPanel flow, SurfaceMode mode, string label)
    {
        var button = new Button
        {
            AutoSize = true,
            MinimumSize = new Size(92, 34),
            Height = 34,
            Padding = new Padding(12, 0, 12, 0),
            Margin = new Padding(0, 0, 8, 8),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(14, 28, 42),
            ForeColor = Ink,
            Font = FontLabel,
            Text = label,
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.BorderSize = 1;
        button.Click += (_, _) => ApplySurfaceMode(mode);
        _modeButtons[mode] = button;
        flow.Controls.Add(button);
    }

    // Switches between the five window layouts. Each case sets the border, taskbar, always-on-top, transparency,
    // which panels are visible and the window size. Operator mode remembers its position so it can be restored.
    // Dashboard needs a second monitor; with only one display it falls back to Operator.
    private void ApplySurfaceMode(SurfaceMode mode)
    {
        if (_surfaceMode == mode && !_isInTrayMode)
        {
            return;
        }

        if (_surfaceMode == SurfaceMode.OperatorConsole)
        {
            _operatorBounds = Bounds;
        }

        if (mode == SurfaceMode.Dashboard && Screen.AllScreens.Length < 2)
        {
            AppendSystemMessage("Second-screen dashboard requested, but only one display is available.");
            AppendTimelineEntry("DISPLAY", "Second-screen dashboard unavailable.", "A second monitor was not detected.", Coral);
            mode = SurfaceMode.OperatorConsole;
        }

        _surfaceMode = mode;
        _contentLayout.SuspendLayout();
        _mainColumnLayout.SuspendLayout();

        // Compact modes use a smaller minimum size than the console (1080x760).
        var compact = mode is SurfaceMode.VoiceOnly or SurfaceMode.OverlayHud or SurfaceMode.EdgeDock;
        MinimumSize = compact ? new Size(320, 300) : new Size(1080, 760);
        SetCompactChrome(compact);

        var targetScreen = mode == SurfaceMode.Dashboard && Screen.AllScreens.Length > 1
            ? Screen.AllScreens[1]
            : Screen.FromControl(this);
        var workingArea = targetScreen.WorkingArea;

        // Operator: full console, resizable, 63/37 column split, the position the user last left it in.
        switch (mode)
        {
            case SurfaceMode.OperatorConsole:
                FormBorderStyle = FormBorderStyle.Sizable;
                ShowInTaskbar = true;
                TopMost = false;
                Opacity = 1.0;
                _contentLayout.ColumnStyles[0].Width = 63f;
                _contentLayout.ColumnStyles[1].Width = 37f;
                _mainColumnLayout.RowStyles[0].SizeType = SizeType.Absolute;
                _mainColumnLayout.RowStyles[0].Height = 278f;
                _mainColumnLayout.RowStyles[1].SizeType = SizeType.Percent;
                _mainColumnLayout.RowStyles[1].Height = 100f;
                _historyCard.Visible = true;
                _telemetryColumn.Visible = true;
                _composerPanel.Visible = true;
                Bounds = _operatorBounds;
                break;

            // Voice: small centered fixed window showing only the response card.
            case SurfaceMode.VoiceOnly:
                FormBorderStyle = FormBorderStyle.FixedSingle;
                ShowInTaskbar = true;
                TopMost = false;
                Opacity = 1.0;
                _contentLayout.ColumnStyles[0].Width = 100f;
                _contentLayout.ColumnStyles[1].Width = 0f;
                _historyCard.Visible = false;
                _telemetryColumn.Visible = false;
                _composerPanel.Visible = false;
                _mainColumnLayout.RowStyles[0].SizeType = SizeType.Percent;
                _mainColumnLayout.RowStyles[0].Height = 100f;
                _mainColumnLayout.RowStyles[1].SizeType = SizeType.Absolute;
                _mainColumnLayout.RowStyles[1].Height = 0f;
                Bounds = CenteredBounds(workingArea, 760, 420);
                break;

            // Overlay: borderless, always on top, slightly see-through panel in the top-right corner.
            case SurfaceMode.OverlayHud:
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = true;
                TopMost = true;
                Opacity = 0.94;
                _contentLayout.ColumnStyles[0].Width = 100f;
                _contentLayout.ColumnStyles[1].Width = 0f;
                _historyCard.Visible = false;
                _telemetryColumn.Visible = false;
                _composerPanel.Visible = false;
                _mainColumnLayout.RowStyles[0].SizeType = SizeType.Percent;
                _mainColumnLayout.RowStyles[0].Height = 100f;
                _mainColumnLayout.RowStyles[1].SizeType = SizeType.Absolute;
                _mainColumnLayout.RowStyles[1].Height = 0f;
                Bounds = new Rectangle(workingArea.Right - 600, workingArea.Top + 32, 560, 430);
                break;

            // Edge dock: thin borderless always-on-top strip along the right edge of the screen.
            case SurfaceMode.EdgeDock:
                FormBorderStyle = FormBorderStyle.None;
                // Stays listed in the taskbar like the overlay: hiding it froze the UI thread in GetWindowText on the next
                // status refresh.
                ShowInTaskbar = true;
                TopMost = true;
                Opacity = 0.92;
                _contentLayout.ColumnStyles[0].Width = 100f;
                _contentLayout.ColumnStyles[1].Width = 0f;
                _historyCard.Visible = false;
                _telemetryColumn.Visible = false;
                _composerPanel.Visible = false;
                _mainColumnLayout.RowStyles[0].SizeType = SizeType.Percent;
                _mainColumnLayout.RowStyles[0].Height = 100f;
                _mainColumnLayout.RowStyles[1].SizeType = SizeType.Absolute;
                _mainColumnLayout.RowStyles[1].Height = 0f;
                Bounds = new Rectangle(workingArea.Right - 360, workingArea.Top + 18, 340, Math.Max(360, workingArea.Height - 36));
                break;

            // Dashboard: borderless, fills the second display, shows history and telemetry but no input box.
            case SurfaceMode.Dashboard:
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = true;
                TopMost = false;
                Opacity = 0.98;
                _contentLayout.ColumnStyles[0].Width = 58f;
                _contentLayout.ColumnStyles[1].Width = 42f;
                _historyCard.Visible = true;
                _telemetryColumn.Visible = true;
                _composerPanel.Visible = false;
                _mainColumnLayout.RowStyles[0].SizeType = SizeType.Absolute;
                _mainColumnLayout.RowStyles[0].Height = 250f;
                _mainColumnLayout.RowStyles[1].SizeType = SizeType.Percent;
                _mainColumnLayout.RowStyles[1].Height = 100f;
                Bounds = workingArea;
                break;
        }

        // Layout was suspended above so the many size changes cause one redraw here.
        _mainColumnLayout.ResumeLayout(true);
        _contentLayout.ResumeLayout(true);
        UpdateSurfaceModeButtons();
        UpdateModeStatusLabel();
    }

    // Makes the header shorter and hides extras in compact modes.
    private void SetCompactChrome(bool compact)
    {
        _headerPanel.Height = compact ? 148 : 224;
        _missionBriefLabel.Visible = !compact;
        _chipFlow.Visible = !compact;
        _appActionFlow.Visible = !compact;
        _headerLayout.ColumnStyles[0].Width = compact ? 100f : 62f;
        _headerLayout.ColumnStyles[1].Width = compact ? 0f : 38f;
        _fullViewButton.Text = compact ? "Full view" : "Compact";
    }

    // Highlights the active mode button in the current state color.
    private void UpdateSurfaceModeButtons()
    {
        foreach (var pair in _modeButtons)
        {
            var active = pair.Key == _surfaceMode;
            pair.Value.BackColor = active ? RaisedActive : Color.FromArgb(14, 28, 42);
            pair.Value.ForeColor = active ? GetStateAccent(_surfaceState) : Ink;
            pair.Value.FlatAppearance.BorderColor = active ? GetStateAccent(_surfaceState) : Border;
        }
    }

    // One-line description of the current mode shown under "Window mode".
    private void UpdateModeStatusLabel()
    {
        _modeStatusLabel.Text = _surfaceMode switch
        {
            SurfaceMode.VoiceOnly => "Compact voice window. Conversation, timeline and the text box are hidden.",
            SurfaceMode.OverlayHud => "Floating panel that stays on top of other windows.",
            SurfaceMode.EdgeDock => "Thin rail at the edge of the screen for passive monitoring.",
            SurfaceMode.Dashboard => "Full-screen view on the second display.",
            _ => "Full console on this display: response, conversation, microphone, activity and timeline."
        };
    }

    // Adds a LISTEN/THINK/ACT/SPEAK label to the header and remembers it so the current stage can be lit up.
    private void AddStageLabel(FlowLayoutPanel flow, SurfaceState state, string label)
    {
        var stageLabel = new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 0, 8, 0),
            Padding = new Padding(12, 6, 12, 6),
            BackColor = Color.FromArgb(12, 20, 34),
            ForeColor = Color.FromArgb(109, 130, 152),
            Font = FontMonoBold,
            Text = label
        };

        _stageLabels[state] = stageLabel;
        flow.Controls.Add(stageLabel);
    }

    // Shared layout for rows of controls: wraps to the next line when narrow and grows its card to fit.
    private static TransparentFlowLayoutPanel CreateRowFlow()
    {
        // Wraps instead of clipping when the column is narrow; the card grows to fit.
        return new TransparentFlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = Padding.Empty
        };
    }

    private static ComboBox CreateComboBox(int width)
    {
        return new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = width,
            FlatStyle = FlatStyle.Flat,
            BackColor = Well,
            ForeColor = Ink,
            Font = FontBody,
            Margin = new Padding(0, 4, 10, 4)
        };
    }

    private static Label CreateFieldLabel(string text)
    {
        return new Label
        {
            AutoSize = true,
            ForeColor = InkSoft,
            Font = FontLabel,
            Margin = new Padding(0, 9, 8, 0),
            Text = text
        };
    }

    private static Label CreateStatusLabel(string text)
    {
        return new Label
        {
            AutoSize = true,
            ForeColor = InkMuted,
            Font = FontMonoSmall,
            Margin = new Padding(0, 8, 12, 0),
            Text = text
        };
    }

    // Read-only dark text box used for the response, history, session summary and timeline.
    private static RichTextBox CreateTelemetryBox()
    {
        return new RichTextBox
        {
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Well,
            ForeColor = Color.FromArgb(235, 243, 252),
            ScrollBars = RichTextBoxScrollBars.Vertical,
            DetectUrls = false
        };
    }

    // Standard dark glass card the other panels are built from.
    private static GlassPanel CreateSurfaceCard()
    {
        return new GlassPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 12),
            Padding = new Padding(18),
            SurfaceColor = Color.FromArgb(14, 24, 40),
            SurfaceColor2 = Color.FromArgb(9, 17, 29),
            BorderColor = Border,
            AccentColor = Teal
        };
    }

    private static Label CreateSectionTitle(string text)
    {
        return new Label
        {
            Height = 30,
            ForeColor = Color.FromArgb(214, 229, 242),
            Font = FontHeading,
            Text = text
        };
    }

    private static Label CreateMetricLabel(string text)
    {
        return new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 0, 10, 6),
            Padding = new Padding(10, 4, 10, 4),
            BackColor = Color.FromArgb(12, 22, 36),
            ForeColor = Color.FromArgb(173, 216, 230),
            Font = FontMonoBold,
            Text = text
        };
    }

    private static Label CreateInfoChip(string text, Color accent)
    {
        return new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 0, 8, 0),
            Padding = new Padding(10, 6, 10, 6),
            BackColor = Color.FromArgb(13, 24, 36),
            ForeColor = accent,
            Font = FontMonoBold,
            Text = text
        };
    }

    private static Button CreateActionButton(string text)
    {
        var button = new Button
        {
            AutoSize = true,
            Height = 32,
            Margin = new Padding(0, 4, 8, 4),
            Padding = new Padding(12, 0, 12, 0),
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Raised,
            ForeColor = Ink,
            Font = FontLabel,
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(34, 88, 118);
        button.FlatAppearance.BorderSize = 1;
        return button;
    }

    private static Button CreateAccentButton(string text, Color accent)
    {
        var button = new Button
        {
            AutoSize = true,
            Height = 34,
            Margin = new Padding(0, 4, 10, 0),
            Padding = new Padding(16, 0, 16, 0),
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = accent,
            ForeColor = Color.FromArgb(8, 15, 24),
            Font = FontHeading,
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    // Window rectangle of the given size centered in the screen's usable area, with a 20 pixel margin so it never touches the edges.
    private static Rectangle CenteredBounds(Rectangle workingArea, int width, int height)
    {
        var finalWidth = Math.Min(width, workingArea.Width - 40);
        var finalHeight = Math.Min(height, workingArea.Height - 40);
        return new Rectangle(
            workingArea.Left + ((workingArea.Width - finalWidth) / 2),
            workingArea.Top + ((workingArea.Height - finalHeight) / 2),
            finalWidth,
            finalHeight);
    }
}
