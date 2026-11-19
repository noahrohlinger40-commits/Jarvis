// JarvisMainForm.cs: the main window, a partial class split across JarvisMainForm*.cs. This file holds the fields,
// constructor, window events (hotkey, tray, close, paint) and first-time setup. Layout.cs builds the controls,
// Runtime.cs handles prompts, voice turns, approvals and settings, Tutor.cs is the lesson tutor callback. Created by Program.cs.
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Jarvis.Core;

namespace Jarvis.App;

// The main Jarvis window ("operations console"): shows the conversation, live status, microphone signal and
// pending approvals, and sits in the system tray when closed or minimized (if that option is on).
internal sealed partial class JarvisMainForm : Form
{
    // Small overlay window that flashes a cue when Jarvis performs a desktop action.
    private readonly ActionCueOverlayForm _actionCueOverlay = new();
    // Wake word models and engines found on disk, used to fill the two drop-downs in the voice settings.
    private IReadOnlyList<WakeWordAssetDescriptor> _availableWakeWordAssets = Array.Empty<WakeWordAssetDescriptor>();
    private IReadOnlyList<WakeWordEngineDescriptor> _availableWakeWordEngines = Array.Empty<WakeWordEngineDescriptor>();
    // Cancelled when the window is disposed, so background work started from the form stops with it.
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    // Everything built at startup (settings, assistant, speaker, voice); see JarvisDesktopContext.
    private readonly JarvisDesktopContext _context;
    // Fonts for chat lines in the history box, kept as fields so they can be disposed with the form.
    private readonly Font _historyMessageFont;
    private readonly Font _historySourceFont;
    private readonly MicrophoneCalibrationService _microphoneCalibrationService = new();
    private readonly Dictionary<SurfaceMode, Button> _modeButtons = new();
    // Serializes saving settings so two quick changes cannot write the file at the same time.
    private readonly SemaphoreSlim _optionsPersistenceGate = new(1, 1);
    // Timer that re-reads assistant state and refreshes the status labels every 1.4 s (see the constructor).
    private readonly System.Windows.Forms.Timer _refreshTimer;
    // One label per stage of the Idle / Listening / Thinking... status strip, so the active one can be highlighted.
    private readonly Dictionary<SurfaceState, Label> _stageLabels = new();
    // The system tray icon.
    private readonly NotifyIcon _trayIcon;
    // Drives the signal waveform animation at about 30 frames per second.
    private readonly System.Windows.Forms.Timer _waveformTimer;

    // Id of the conversation session currently shown.
    private string _activeSessionId = string.Empty;
    private BufferedFlowLayoutPanel _activityFlow = null!;
    // True once the user really chose to exit; stops the close button from just hiding the window in the tray.
    private bool _allowExit;
    // State forced by what the assistant is doing right now; it takes priority over the microphone-based state (see RecomputeSurfaceState).
    private SurfaceState? _assistantStateOverride;
    private string _assistantStateOverrideDetail = string.Empty;
    private Panel _approvalPanel = null!;
    private Label _approvalPromptLabel = null!;
    private Label _approvalSummaryLabel = null!;
    private Button _approveButton = null!;
    private Label _assistantStatusLabel = null!;
    private Label _backgroundLabel = null!;
    private ComboBox _beamformingProfileComboBox = null!;
    private Button _calibrateMicButton = null!;
    private Label _calibrationStatusLabel = null!;
    private TransparentPanel _composerPanel = null!;
    private TransparentTableLayoutPanel _contentLayout = null!;
    // The settings in use now; replaced whenever the user changes a setting.
    private JarvisOptions _currentOptions;
    // The active microphone/speech-recognition engine; replaced when audio settings change.
    private IVoiceRuntime _currentVoiceRuntime;
    private Button _denyButton = null!;
    private ComboBox _deviceComboBox = null!;
    private Button _deviceRefreshButton = null!;
    private Button _helpButton = null!;
    private RichTextBox _historyBox = null!;
    private Panel _historyCard = null!;
    private ComboBox _hardwareDspProfileComboBox = null!;
    private TextBox _inputBox = null!;
    // True while the window is hidden in the tray.
    private bool _isInTrayMode;
    // True while a periodic refresh is running, so a slow one is not overlapped by the next timer tick.
    private bool _isRefreshingUiState;
    // True while a typed or spoken prompt is being processed, so a second one is not started.
    private bool _isSubmitting;
    private string _lastAssistantDetail = "Stand by.";
    private AssistantActivityKind _lastAssistantKind = AssistantActivityKind.Status;
    private string _lastAssistantStatusText = string.Empty;
    private string _lastClarificationSnapshotKey = string.Empty;
    // When the user last cut Jarvis off by speaking over it (barge-in).
    private DateTimeOffset _lastBargeInUtc = DateTimeOffset.MinValue;
    private string _lastSessionSummary = "No active session.";
    private string _lastVoiceStatusText = string.Empty;
    // Most recent microphone level reading, drawn by the waveform control.
    private VoiceSignalSnapshot _latestSignal;
    private Label _listenIndicatorLabel = null!;
    private RichTextBox _liveResponseBox = null!;
    // Cancels the reply that is currently being shown word by word in the live response box.
    private CancellationTokenSource? _liveResponseCts;
    // Streaming flags: set while an assistant reply is being typed into the history box or live box.
    private bool _isAssistantHistoryStreamActive;
    private bool _isAssistantResponseStreaming;
    // Jarvis asked a question and is waiting for the answer, which arrives without a wake word until the deadline below.
    private bool _isAwaitingFollowUpInput;
    private DateTimeOffset _followUpAnswerDeadlineUtc = DateTimeOffset.MinValue;
    private TransparentTableLayoutPanel _mainColumnLayout = null!;
    private Button _micToggleButton = null!;
    private Label _modeStatusLabel = null!;
    private Button _memoryButton = null!;
    private Label _missionBriefLabel = null!;
    // Text of the mission strip at the top of the window.
    private string _missionBriefText = "LOCAL --:--  |  ACTIVE standby  |  OBJECTIVE Stand by for the next directive.";
    private string _currentMissionObjective = "Stand by for the next directive.";
    private Button _notesButton = null!;
    // Default window position and size (x, y, width, height) for the normal operator console view.
    private Rectangle _operatorBounds = new(120, 50, 1600, 1040);
    // A voice command that was heard with low confidence and is waiting for the user to confirm or repeat it.
    private PendingVoiceClarification? _pendingVoiceClarification;
    private Label _peakLabel = null!;
    // An action waiting for the user to press Approve or Deny.
    private PendingApprovalAction? _pendingApproval;
    private Button _presenceButton = null!;
    private Label _promptEchoLabel = null!;
    private Button _pushToTalkButton = null!;
    // A voice command that arrived while Jarvis was busy; it is run after the current one finishes.
    private QueuedVoiceDirective? _queuedVoiceDirective;
    private TransparentTableLayoutPanel _rightColumnLayout = null!;
    private Label _rmsLabel = null!;
    private Button _sendButton = null!;
    private RichTextBox _sessionBox = null!;
    private GlassPanel _sessionSummaryCard = null!;
    private Button _statusButton = null!;
    private string _awaitingFollowUpDetail = string.Empty;
    private Label _surfaceDetailLabel = null!;
    private SurfaceMode _surfaceMode = SurfaceMode.OperatorConsole;
    private SurfaceState _surfaceState = SurfaceState.Idle;
    private Label _surfaceStateLabel = null!;
    private Panel _telemetryColumn = null!;
    private RichTextBox _timelineBox = null!;
    private Button _validateMicButton = null!;
    private Label _validationStatusLabel = null!;
    private Label _voiceStatusLabel = null!;
    private ComboBox _wakeEngineComboBox = null!;
    private Label _wakeIndicatorLabel = null!;
    private ComboBox _wakeWordAssetComboBox = null!;
    private SignalWaveformControl _waveformControl = null!;

    // A low-confidence voice command held until the user confirms it: cleaned text, raw transcript, the question asked, and recognition confidence.
    private sealed record PendingVoiceClarification(string Text, string RawText, string Prompt, double? Confidence);

    // A voice command saved for later, with the same details as the event the voice runtime raised.
    private sealed record QueuedVoiceDirective(
        string Text,
        string RawText,
        double? Confidence,
        bool RequiresClarification,
        string ClarificationPrompt)
    {
        // Copies the voice runtime's event data into a queue entry.
        public static QueuedVoiceDirective From(VoiceCommandRecognizedEventArgs args) =>
            new(args.Text, args.RawText, args.Confidence, args.RequiresClarification, args.ClarificationPrompt);

        // Rebuilds the original event data so the queued command can be handled as if it just arrived.
        public VoiceCommandRecognizedEventArgs ToEventArgs() =>
            new(Text, Confidence, RequiresClarification, ClarificationPrompt, RawText);
    }

    // Builds the window: creates timers, builds the controls (Layout slice), hooks events, then fills in the first status.
    // Nothing talks to the model here; that starts in OnShown.
    public JarvisMainForm(JarvisDesktopContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _currentOptions = context.Options;
        _currentVoiceRuntime = context.VoiceRuntime;
        _latestSignal = _currentVoiceRuntime.CurrentSignal;
        _historySourceFont = new Font("Bahnschrift SemiBold", 9.75f, FontStyle.Bold, GraphicsUnit.Point);
        _historyMessageFont = new Font("Consolas", 10.15f, FontStyle.Regular, GraphicsUnit.Point);

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = BuildTrayText("Initializing"),
            Visible = _currentOptions.KeepRunningInTrayOnClose
        };

        // Status refresh timer, started in OnShown.
        _refreshTimer = new System.Windows.Forms.Timer
        {
            Interval = 1400
        };
        _refreshTimer.Tick += OnRefreshTimerTick;

        // Waveform animation timer; each tick advances one frame.
        _waveformTimer = new System.Windows.Forms.Timer
        {
            Interval = 33
        };
        _waveformTimer.Tick += (_, _) => _waveformControl.AdvanceAnimation();

        // Order matters: the window and controls must exist before events are hooked and before any UI update runs.
        InitializeWindow();
        InitializeSurface();
        InitializeTrayIcon();
        HookUiEvents();
        HookRuntimeEvents();

        // Fill the drop-downs and show the first status of every part of the voice system.
        RefreshAudioDevices();
        RefreshWakeWordAssets();
        RefreshWakeEngines();
        RefreshBeamformingProfiles();
        RefreshHardwareDspProfiles();
        UpdateCalibrationUi();
        UpdateVoiceUi(_currentVoiceRuntime.CurrentStatus, appendToFeed: false);
        UpdateSignalUi(_currentVoiceRuntime.CurrentSignal);
        UpdateSpeakerUi(_context.Speaker.CurrentStatus);
        UpdateSurfaceState(SurfaceState.Idle, "Stand by for voice, operator, or background automation.", playCue: false);
        UpdateModeStatusLabel();
        ApplySurfaceMode(_surfaceMode);
    }

    // Fires once the window is first visible: starts the timers and kicks off the voice runtime without waiting for it.
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _refreshTimer.Start();
        _waveformTimer.Start();
        _ = InitializeRuntimeAsync();
    }

    // Global hotkey (user32.dll RegisterHotKey), works from any app. While Jarvis talks it only stops the reply,
    // because whoever cuts Jarvis off usually keeps talking to the room and that speech would run as a command;
    // otherwise it listens for a command. It is the reliable way to cut off a long page reading, since Jarvis
    // can't always hear "Jarvis, stop" over its own voice. Registered per window handle (tray mode recreates it).
    private const int WmHotkey = 0x0312;
    private const int VoiceHotkeyId = 1;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // Fires when the real window handle exists (and again if the handle is recreated, e.g. after tray mode).
    // Registers the voice hotkey with Windows.
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // "Control+Space" reads as Keys.Control | Keys.Space.
        if (!_currentOptions.SpeechRecognitionHotkeyEnabled
            || !Enum.TryParse<Keys>(_currentOptions.SpeechRecognitionHotkey.Replace("Ctrl", "Control").Replace('+', ','), ignoreCase: true, out var keys))
        {
            return;
        }

        // Windows modifier bit flags; modNoRepeat stops a held key from firing repeatedly.
        const uint modAlt = 0x1, modControl = 0x2, modShift = 0x4, modNoRepeat = 0x4000;
        var modifiers = modNoRepeat
            | (keys.HasFlag(Keys.Alt) ? modAlt : 0)
            | (keys.HasFlag(Keys.Control) ? modControl : 0)
            | (keys.HasFlag(Keys.Shift) ? modShift : 0);

        if (!RegisterHotKey(Handle, VoiceHotkeyId, modifiers, (uint)(keys & Keys.KeyCode)))
        {
            // Registration fails when another app already owns the key; tell the user in the chat. BeginInvoke because the form is still starting.
            BeginInvoke(() => AppendSystemMessage(
                $"The voice hotkey {_currentOptions.SpeechRecognitionHotkey} is taken by another app. Change speechRecognitionHotkey in jarvis.settings.json."));
        }
    }

    // Fires when the window handle is destroyed; the hotkey is tied to the handle, so release it.
    protected override void OnHandleDestroyed(EventArgs e)
    {
        UnregisterHotKey(Handle, VoiceHotkeyId);
        base.OnHandleDestroyed(e);
    }

    // Receives raw Windows messages; the only one handled here is the hotkey press from RegisterHotKey.
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && m.WParam == VoiceHotkeyId)
        {
            if (_context.Speaker.CurrentStatus.IsSpeaking)
            {
                _context.Speaker.Interrupt();
                AddBargeInTimelineEntry("hotkey");
            }
            else
            {
                _currentVoiceRuntime.ListenForFollowUp();
            }
        }

        base.WndProc(ref m);
    }

    // Fires when the window is resized; minimizing sends Jarvis to the tray if that option is on.
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        if (_currentOptions.KeepRunningInTrayOnClose
            && WindowState == FormWindowState.Minimized
            && !_isInTrayMode)
        {
            SendToTray("window minimized");
        }
    }

    // Fires when the user closes the window; with the tray option on, the close is cancelled and the window hides instead.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowExit
            && _currentOptions.KeepRunningInTrayOnClose
            && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            SendToTray("window close");
            return;
        }

        _allowExit = true;
        base.OnFormClosing(e);
    }

    // Paints the dark gradient background with a faint 28-pixel grid.
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // The client area is briefly 0x0 while the border style changes; GDI+ throws Out of memory then.
        if (ClientRectangle.Width <= 0 || ClientRectangle.Height <= 0)
        {
            return;
        }

        using var backgroundBrush = new LinearGradientBrush(
            ClientRectangle,
            Color.FromArgb(7, 14, 24),
            Color.FromArgb(4, 9, 16),
            90f);
        e.Graphics.FillRectangle(backgroundBrush, ClientRectangle);

        using var gridPen = new Pen(Color.FromArgb(18, 38, 58), 1f);

        for (var x = 0; x < ClientRectangle.Width; x += 28)
        {
            e.Graphics.DrawLine(gridPen, x, 0, x, ClientRectangle.Height);
        }

        for (var y = 0; y < ClientRectangle.Height; y += 28)
        {
            e.Graphics.DrawLine(gridPen, 0, y, ClientRectangle.Width, y);
        }
    }

    // Cleans up when the form is destroyed: stops timers, unhooks events, disposes the voice runtime and cancels background work.
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _allowExit = true;
            _refreshTimer.Stop();
            _waveformTimer.Stop();
            _refreshTimer.Dispose();
            _waveformTimer.Dispose();

            _context.Assistant.Activity -= OnAssistantActivity;
            if (_context.Assistant.Tutor is { } tutor)
            {
                tutor.Said -= OnTutorSaid;
                tutor.Stop();
            }

            _context.Speaker.StatusChanged -= OnSpeakerStatusChanged;

            _currentVoiceRuntime.StatusChanged -= OnVoiceStatusChanged;
            _currentVoiceRuntime.SignalLevelChanged -= OnVoiceSignalChanged;
            _currentVoiceRuntime.CommandRecognized -= OnVoiceCommandRecognized;
            _currentVoiceRuntime.BargeInRequested -= OnVoiceBargeInRequested;

            try
            {
                _currentVoiceRuntime.Dispose();
            }
            catch
            {
            }

            _liveResponseCts?.Cancel();
            _liveResponseCts?.Dispose();
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource.Dispose();
            _optionsPersistenceGate.Dispose();

            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _actionCueOverlay.Dispose();
            _historySourceFont.Dispose();
            _historyMessageFont.Dispose();
        }

        base.Dispose(disposing);
    }

    // Basic window properties: title, minimum size, colors.
    // SuspendLayout/ResumeLayout stop WinForms from re-measuring after every change.
    private void InitializeWindow()
    {
        SuspendLayout();
        Text = $"{_currentOptions.AssistantName} Operations";
        StartPosition = FormStartPosition.Manual;
        Bounds = _operatorBounds;
        MinimumSize = new Size(1080, 760);
        BackColor = Color.FromArgb(7, 14, 24);
        ForeColor = Color.FromArgb(237, 244, 252);
        DoubleBuffered = true;
        KeyPreview = true;
        ShowIcon = true;
        ResumeLayout(false);
    }

    // Builds the layout: a left column (live console on top, chat history below) and a right column
    // (signal, approval card, activity list, timeline), then the composer (input box) and header.
    // Row heights and the 63/37 column split are plain layout choices.
    private void InitializeSurface()
    {
        SuspendLayout();

        _contentLayout = new TransparentTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(22, 10, 22, 0),
            Margin = Padding.Empty
        };
        _contentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 63f));
        _contentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 37f));

        _mainColumnLayout = new TransparentTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = new Padding(0, 0, 12, 0)
        };
        // Top row has a fixed height of 278 pixels; the history card gets the rest.
        _mainColumnLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 278f));
        _mainColumnLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var liveConsoleCard = BuildLiveConsoleCard();
        _historyCard = BuildHistoryCard();
        _mainColumnLayout.Controls.Add(liveConsoleCard, 0, 0);
        _mainColumnLayout.Controls.Add(_historyCard, 0, 1);

        _telemetryColumn = new TransparentPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 0, 0, 0),
            Margin = Padding.Empty
        };

        _rightColumnLayout = new TransparentTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty
        };
        // Approval card row starts at height 0 and is expanded only when an approval is pending.
        _rightColumnLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _rightColumnLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0f));
        _rightColumnLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 58f));
        _rightColumnLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 42f));

        var signalCard = BuildSignalCard();
        _approvalPanel = BuildApprovalCard();
        var activitiesCard = BuildActivitiesCard();
        var timelineCard = BuildTimelineCard();

        _rightColumnLayout.Controls.Add(signalCard, 0, 0);
        _rightColumnLayout.Controls.Add(_approvalPanel, 0, 1);
        _rightColumnLayout.Controls.Add(activitiesCard, 0, 2);
        _rightColumnLayout.Controls.Add(timelineCard, 0, 3);
        _telemetryColumn.Controls.Add(_rightColumnLayout);

        _contentLayout.Controls.Add(_mainColumnLayout, 0, 0);
        _contentLayout.Controls.Add(_telemetryColumn, 1, 0);

        _composerPanel = BuildComposerPanel();

        Controls.Add(_contentLayout);
        Controls.Add(_composerPanel);
        Controls.Add(BuildHeaderPanel());

        ResumeLayout(true);
    }

    // Creates the tray icon's right-click menu (Open, Hide, Exit) and double-click to reopen.
    private void InitializeTrayIcon()
    {
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Open", null, (_, _) => BringAssistantToFront("tray menu"));
        trayMenu.Items.Add("Hide", null, (_, _) => SendToTray("tray menu"));
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Exit", null, (_, _) => ExitApplication());

        _trayIcon.ContextMenuStrip = trayMenu;
        _trayIcon.DoubleClick += (_, _) => BringAssistantToFront("tray double click");
    }

    // Connects buttons and the input box to their handlers. The quick buttons (help, status, notes) just send a fixed prompt.
    private void HookUiEvents()
    {
        _sendButton.Click += async (_, _) => await SubmitPromptAsync();
        _helpButton.Click += async (_, _) => await ProcessPromptAsync("help", "SURFACE", clearInput: false, displayedInput: "Requested the help briefing.");
        _statusButton.Click += async (_, _) => await ProcessPromptAsync("status", "SURFACE", clearInput: false, displayedInput: "Requested the current runtime status.");
        _notesButton.Click += async (_, _) => await ProcessPromptAsync("notes", "SURFACE", clearInput: false, displayedInput: "Requested notes and memory context.");
        _memoryButton.Click += async (_, _) => await OpenMemoryWorkbenchAsync();
        _presenceButton.Click += async (_, _) => await OpenPresenceWorkbenchAsync();
        _approveButton.Click += async (_, _) => await HandleApprovalDecisionAsync(true);
        _denyButton.Click += async (_, _) => await HandleApprovalDecisionAsync(false);
        _micToggleButton.Click += async (_, _) => await ToggleMicrophoneAsync();
        _pushToTalkButton.Click += async (_, _) => await TogglePushToTalkAsync();
        _deviceRefreshButton.Click += (_, _) => RefreshAudioDevices();
        _calibrateMicButton.Click += async (_, _) => await CalibrateMicrophoneAsync();
        _validateMicButton.Click += async (_, _) => await ValidateMicrophoneAsync();
        _inputBox.KeyDown += InputBoxOnKeyDown;
    }

    // Subscribes to events from the assistant, speaker and voice runtime. Dispose must unsubscribe the same ones.
    private void HookRuntimeEvents()
    {
        _context.Assistant.Activity += OnAssistantActivity;
        if (_context.Assistant.Tutor is { } tutor)
        {
            tutor.Said += OnTutorSaid;
        }

        _context.Speaker.StatusChanged += OnSpeakerStatusChanged;
        _currentVoiceRuntime.StatusChanged += OnVoiceStatusChanged;
        _currentVoiceRuntime.SignalLevelChanged += OnVoiceSignalChanged;
        _currentVoiceRuntime.CommandRecognized += OnVoiceCommandRecognized;
        _currentVoiceRuntime.BargeInRequested += OnVoiceBargeInRequested;
        _currentVoiceRuntime.SetPlaybackState(_context.Speaker.CurrentStatus.IsSpeaking);
    }

    // Called after the window shows: loads the first runtime status, then starts listening. A voice failure is shown in the chat, not fatal.
    private async Task InitializeRuntimeAsync()
    {
        try
        {
            await RefreshRuntimeStateAsync();
        }
        catch
        {
        }

        try
        {
            await _currentVoiceRuntime.StartAsync(_cancellationTokenSource.Token);
        }
        catch (Exception exception)
        {
            AppendSystemMessage($"Voice runtime failed to start: {exception.Message}");
        }
    }

    // Swaps in a new speaker after settings change, moving the event subscription to the new one.
    private void ReplaceSpeaker(JarvisOptions options)
    {
        _context.Options = options;
        _context.Speaker.StatusChanged -= OnSpeakerStatusChanged;
        _context.Speaker = SpeakerFactory.Create(options);
        _context.Speaker.StatusChanged += OnSpeakerStatusChanged;
        UpdateSpeakerUi(_context.Speaker.CurrentStatus);
    }

    // False once the form is closing or has no window handle yet; background code checks this before touching controls.
    private bool CanUseUi()
    {
        return !IsDisposed && !Disposing && IsHandleCreated;
    }

    // Tray tooltip text; Windows limits it to 63 characters.
    private string BuildTrayText(string status)
    {
        var text = $"{_currentOptions.AssistantName}: {TrimForSingleLine(status, 52)}";
        return text.Length <= 63 ? text : text[..63];
    }

    // Shows the window again from the tray or minimized state and brings it to the front.
    private void BringAssistantToFront(string reason)
    {
        if (_isInTrayMode)
        {
            Show();
            ApplySurfaceMode(_surfaceMode);
            _isInTrayMode = false;
        }

        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        ShowInTaskbar = true;
        Activate();
        BringToFront();

        if (CanUseUi())
        {
            AppendTimelineEntry(
                "SURFACE",
                "Operator surface brought to the foreground.",
                $"Trigger: {TrimForSingleLine(reason, 96)}.",
                Color.FromArgb(132, 196, 255));
        }
    }

    // Hides the window into the tray, leaving voice running.
    private void SendToTray(string reason)
    {
        if (_isInTrayMode)
        {
            return;
        }

        _isInTrayMode = true;
        ShowInTaskbar = false;
        _trayIcon.Visible = true;
        Hide();

        if (CanUseUi())
        {
            AppendTimelineEntry(
                "SURFACE",
                "Jarvis moved to tray mode.",
                $"Trigger: {TrimForSingleLine(reason, 96)}. Voice runtime remains available.",
                Color.FromArgb(255, 191, 105));
        }
    }

    // Real exit (from the tray menu): lets the close go through instead of hiding to the tray.
    private void ExitApplication()
    {
        if (_allowExit)
        {
            return;
        }

        _allowExit = true;
        _trayIcon.Visible = false;
        Close();
    }
}
