// MemoryWorkbenchForm.cs: a window for browsing and editing what Jarvis remembers. It lists the active memory
// notes in a grid (left) with an editor for the selected note (right): content, kind, tags, importance,
// retention, expiry and reminder. Opened from the main window (JarvisMainForm.Runtime.cs). All reading and
// writing goes through JarvisAssistant (Jarvis.Core), which owns the real memory store.
using System.Drawing;
using System.Windows.Forms;
using Jarvis.Core;

namespace Jarvis.App;

internal sealed class MemoryWorkbenchForm : Form
{
    // The assistant that owns the memory store; this form only calls GetMemoriesAsync, UpdateMemoryAsync and ForgetMemoryAsync.
    private readonly JarvisAssistant _assistant;
    // Cancelled when the window closes, so pending loads and saves stop quietly.
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    // Feeds the grid with MemoryRow items.
    private readonly BindingSource _gridBinding = new();

    // The notes currently loaded; the grid shows a summary of each, the editor shows the full note.
    private List<MemoryNote> _memories = [];
    // True while the editor fields are being filled in code, so that does not count as the user changing the selection.
    private bool _isLoadingSelection;

    // Grid and editor controls, created in InitializeUi / BuildEditorPanel.
    private DataGridView _grid = null!;
    private Label _statusLabel = null!;
    private Button _refreshButton = null!;
    private Button _saveButton = null!;
    private Button _forgetButton = null!;
    private Label _idLabel = null!;
    private Label _createdLabel = null!;
    private Label _entitiesLabel = null!;
    private ComboBox _kindComboBox = null!;
    private ComboBox _retentionComboBox = null!;
    private ComboBox _privacyComboBox = null!;
    private TextBox _contentBox = null!;
    private TextBox _categoryBox = null!;
    private TextBox _tagsBox = null!;
    private TextBox _reminderTextBox = null!;
    private NumericUpDown _importanceUpDown = null!;
    private DateTimePicker _expiryPicker = null!;
    private DateTimePicker _reminderPicker = null!;
    private CheckBox _userApprovedCheckBox = null!;

    public MemoryWorkbenchForm(JarvisAssistant assistant)
    {
        _assistant = assistant;
        InitializeUi();
        // Load the notes as soon as the window is first shown.
        Shown += async (_, _) => await RefreshMemoriesAsync();
    }

    // Stop any in-flight work and release resources when the window is disposed.
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource.Dispose();
            _gridBinding.Dispose();
        }

        base.Dispose(disposing);
    }

    // Build the window: a toolbar (Refresh, Save, Forget, status text) above a split view with the grid on the left and the editor on the right.
    private void InitializeUi()
    {
        Text = "Memory Workbench";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(1040, 700);
        Size = new Size(1180, 780);
        BackColor = Color.FromArgb(11, 18, 28);
        ForeColor = Color.FromArgb(236, 243, 252);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(12)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };

        _refreshButton = CreateButton("Refresh");
        _saveButton = CreateButton("Save");
        _forgetButton = CreateButton("Forget");
        _statusLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(176, 196, 216),
            Font = new Font("Consolas", 10f, FontStyle.Regular, GraphicsUnit.Point),
            Margin = new Padding(16, 10, 0, 0),
            Text = "Loading memories..."
        };

        // Buttons are async: they await the work so the window stays responsive.
        _refreshButton.Click += async (_, _) => await RefreshMemoriesAsync();
        _saveButton.Click += async (_, _) => await SaveSelectedMemoryAsync();
        _forgetButton.Click += async (_, _) => await ForgetSelectedMemoryAsync();

        toolbar.Controls.Add(_refreshButton);
        toolbar.Controls.Add(_saveButton);
        toolbar.Controls.Add(_forgetButton);
        toolbar.Controls.Add(_statusLabel);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 560,
            BackColor = Color.FromArgb(11, 18, 28)
        };

        // Left grid: read-only, one row per memory note, whole-row selection. Columns are filled in by hand (AutoGenerateColumns is off) and bound to MemoryRow properties by name.
        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Color.FromArgb(13, 21, 33),
            BorderStyle = BorderStyle.None,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoGenerateColumns = false,
            ReadOnly = true,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            DataSource = _gridBinding,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(13, 21, 33),
                ForeColor = Color.FromArgb(236, 243, 252),
                SelectionBackColor = Color.FromArgb(28, 52, 78),
                SelectionForeColor = Color.FromArgb(248, 251, 255)
            },
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(18, 30, 46),
                ForeColor = Color.FromArgb(224, 234, 246),
                SelectionBackColor = Color.FromArgb(18, 30, 46),
                SelectionForeColor = Color.FromArgb(224, 234, 246)
            },
            EnableHeadersVisualStyles = false
        };
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(MemoryRow.Id), HeaderText = "Id", Width = 74 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(MemoryRow.Kind), HeaderText = "Kind", Width = 84 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(MemoryRow.Category), HeaderText = "Category", Width = 92 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(MemoryRow.Due), HeaderText = "Due", Width = 120 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(MemoryRow.Retention), HeaderText = "Retention", Width = 96 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(MemoryRow.Content), HeaderText = "Content", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        // Show the selected note in the editor.
        _grid.SelectionChanged += (_, _) => OnGridSelectionChanged();

        split.Panel1.Controls.Add(_grid);
        split.Panel2.Controls.Add(BuildEditorPanel());

        root.Controls.Add(toolbar, 0, 0);
        root.Controls.Add(split, 0, 1);
        Controls.Add(root);
    }

    // Right panel: a two-column form (label, field) with 11 rows.
    private Control BuildEditorPanel()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(14, 22, 34),
            Padding = new Padding(14)
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 11
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // Row 2 (Content) takes all spare height; the rest are a fixed 44 pixels.
        for (var index = 0; index < 11; index++)
        {
            layout.RowStyles.Add(new RowStyle(index == 2 ? SizeType.Percent : SizeType.Absolute, index == 2 ? 100 : 44));
        }

        _idLabel = CreateValueLabel();
        _createdLabel = CreateValueLabel();
        _entitiesLabel = CreateValueLabel();
        _contentBox = CreateTextBox(multiline: true);
        _categoryBox = CreateTextBox();
        _tagsBox = CreateTextBox();
        _reminderTextBox = CreateTextBox();
        // Allowed values for the drop-downs. They must match what the memory store understands.
        _kindComboBox = CreateComboBox(["note", "preference", "entity", "relationship", "episodic", "summary", "reminder"]);
        _retentionComboBox = CreateComboBox(["standard", "short", "episodic", "rolling", "long", "forever", "until-complete"]);
        _privacyComboBox = CreateComboBox(["persistent", "private"]);
        // Importance is stored as 0 to 1 but edited as 5 to 100 in steps of 5 (see PopulateEditor and SaveSelectedMemoryAsync).
        _importanceUpDown = new NumericUpDown
        {
            Dock = DockStyle.Left,
            Width = 140,
            DecimalPlaces = 0,
            Minimum = 5,
            Maximum = 100,
            Increment = 5,
            BackColor = Color.FromArgb(10, 18, 30),
            ForeColor = Color.FromArgb(236, 243, 252)
        };
        _expiryPicker = CreateDateTimePicker();
        _reminderPicker = CreateDateTimePicker();
        _userApprovedCheckBox = new CheckBox
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(236, 243, 252),
            Text = "User-approved retention"
        };

        AddEditorRow(layout, 0, "Memory Id", _idLabel);
        AddEditorRow(layout, 1, "Created", _createdLabel);
        AddEditorRow(layout, 2, "Content", _contentBox);
        AddEditorRow(layout, 3, "Kind", _kindComboBox);
        AddEditorRow(layout, 4, "Category", _categoryBox);
        AddEditorRow(layout, 5, "Tags", _tagsBox);
        AddEditorRow(layout, 6, "Importance", _importanceUpDown);
        AddEditorRow(layout, 7, "Retention", _retentionComboBox);
        AddEditorRow(layout, 8, "Expires", _expiryPicker);
        AddEditorRow(layout, 9, "Reminder", BuildReminderPanel());
        AddEditorRow(layout, 10, "Entities", _entitiesLabel);

        panel.Controls.Add(layout);
        return panel;
    }

    // Group the reminder text, reminder time, privacy and approval checkbox into one cell.
    private Control BuildReminderPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

        panel.Controls.Add(_reminderTextBox, 0, 0);
        panel.Controls.Add(_reminderPicker, 0, 1);
        panel.Controls.Add(_privacyComboBox, 0, 2);
        panel.Controls.Add(_userApprovedCheckBox, 0, 3);
        return panel;
    }

    // Reload every active note from the assistant and refill the grid, selecting the first row.
    private async Task RefreshMemoriesAsync()
    {
        SetBusy(true);

        try
        {
            _memories = (await _assistant.GetMemoriesAsync(_cancellationTokenSource.Token)).ToList();
            // Show the reminder text instead of the content for notes that are reminders; Due is the reminder time in local time.
            _gridBinding.DataSource = _memories.Select(note => new MemoryRow(
                ShortId(note.Id),
                note.Id,
                note.Kind,
                note.Category,
                string.IsNullOrWhiteSpace(note.ReminderText) ? note.Content : note.ReminderText,
                note.ReminderAtUtc?.ToLocalTime().ToString("MM-dd h:mm tt") ?? string.Empty,
                note.RetentionPolicy)).ToList();
            _statusLabel.Text = $"Loaded {_memories.Count} active memory item(s).";

            if (_grid.Rows.Count > 0)
            {
                _grid.Rows[0].Selected = true;
                OnGridSelectionChanged();
            }
            else
            {
                PopulateEditor(null);
            }
        }
        // Window closed mid-load: ignore.
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _statusLabel.Text = "Memory refresh failed.";
            MessageBox.Show(this, exception.Message, "Memory Refresh Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // When the user picks a row, find the full note it came from and show it in the editor.
    private void OnGridSelectionChanged()
    {
        if (_isLoadingSelection || _grid.CurrentRow?.DataBoundItem is not MemoryRow row)
        {
            return;
        }

        var note = _memories.FirstOrDefault(candidate => string.Equals(candidate.Id, row.FullId, StringComparison.OrdinalIgnoreCase));
        PopulateEditor(note);
    }

    // Fill the editor from a note, or reset it to blanks when there is none.
    private void PopulateEditor(MemoryNote? note)
    {
        // Block OnGridSelectionChanged from reacting to our own changes.
        _isLoadingSelection = true;

        try
        {
            if (note is null)
            {
                _idLabel.Text = "No selection";
                _createdLabel.Text = string.Empty;
                _entitiesLabel.Text = string.Empty;
                _contentBox.Text = string.Empty;
                _categoryBox.Text = string.Empty;
                _tagsBox.Text = string.Empty;
                _reminderTextBox.Text = string.Empty;
                _kindComboBox.SelectedIndex = 0;
                _retentionComboBox.SelectedIndex = 0;
                _privacyComboBox.SelectedIndex = 0;
                _importanceUpDown.Value = 35;
                _expiryPicker.Checked = false;
                _reminderPicker.Checked = false;
                _userApprovedCheckBox.Checked = false;
                return;
            }

            _idLabel.Text = note.Id;
            _createdLabel.Text = note.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd h:mm tt");
            _entitiesLabel.Text = note.Entities is { Length: > 0 }
                ? string.Join(" | ", note.Entities.Select(entity => entity.ToInlineText()))
                : "None";
            _contentBox.Text = note.Content;
            _categoryBox.Text = note.Category;
            _tagsBox.Text = string.Join(", ", note.Tags ?? Array.Empty<string>());
            _reminderTextBox.Text = note.ReminderText;
            SelectComboValue(_kindComboBox, note.Kind, "note");
            SelectComboValue(_retentionComboBox, note.RetentionPolicy, "standard");
            SelectComboValue(_privacyComboBox, note.Privacy, "persistent");
            // Importance 0.0 to 1.0 becomes 5 to 100, kept inside the box's limits.
            _importanceUpDown.Value = Math.Clamp((decimal)Math.Round(note.ImportanceScore * 100d), 5, 100);
            _expiryPicker.Checked = note.ExpiresAtUtc is not null;
            // Date pickers need a value even when unchecked; unchecked means "no expiry/reminder".
            _expiryPicker.Value = (note.ExpiresAtUtc ?? DateTimeOffset.Now).ToLocalTime().DateTime;
            _reminderPicker.Checked = note.ReminderAtUtc is not null;
            _reminderPicker.Value = (note.ReminderAtUtc ?? DateTimeOffset.Now).ToLocalTime().DateTime;
            _userApprovedCheckBox.Checked = note.UserApprovedRetention;
        }
        finally
        {
            _isLoadingSelection = false;
        }
    }

    // Save the editor's contents back to the selected note.
    private async Task SaveSelectedMemoryAsync()
    {
        // Nothing selected: nothing to save.
        if (string.IsNullOrWhiteSpace(_idLabel.Text) || _idLabel.Text == "No selection")
        {
            return;
        }

        SetBusy(true);

        try
        {
            // Collect all fields into an update request. A reminder is marked "pending" when it has a time, so it will fire.
            var request = new MemoryUpdateRequest(
                _idLabel.Text,
                _contentBox.Text.Trim(),
                _kindComboBox.Text,
                _categoryBox.Text.Trim(),
                _privacyComboBox.Text,
                ParseTags(_tagsBox.Text),
                (double)_importanceUpDown.Value / 100d,
                _retentionComboBox.Text,
                _expiryPicker.Checked ? new DateTimeOffset(_expiryPicker.Value) : null,
                _userApprovedCheckBox.Checked,
                _reminderPicker.Checked ? new DateTimeOffset(_reminderPicker.Value) : null,
                _reminderTextBox.Text.Trim(),
                _reminderPicker.Checked ? "pending" : string.Empty);

            var updated = await _assistant.UpdateMemoryAsync(request, _cancellationTokenSource.Token);

            // The note was deleted elsewhere in the meantime.
            if (updated is null)
            {
                _statusLabel.Text = "Selected memory no longer exists.";
                return;
            }

            _statusLabel.Text = $"Saved {ShortId(updated.Id)}.";
            await RefreshMemoriesAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Memory Save Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // "Forget" removes the note from active recall (after asking the user to confirm).
    private async Task ForgetSelectedMemoryAsync()
    {
        if (string.IsNullOrWhiteSpace(_idLabel.Text) || _idLabel.Text == "No selection")
        {
            return;
        }

        var confirmed = MessageBox.Show(
            this,
            "Forget the selected memory from active recall?",
            "Forget Memory",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (confirmed != DialogResult.Yes)
        {
            return;
        }

        SetBusy(true);

        try
        {
            var forgot = await _assistant.ForgetMemoryAsync(_idLabel.Text, "ui request", _cancellationTokenSource.Token);
            _statusLabel.Text = forgot ? $"Forgot {_idLabel.Text[..8]}." : "Forget request failed.";
            await RefreshMemoriesAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Memory Forget Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // Disable the buttons and show the wait cursor during a load, save or forget.
    private void SetBusy(bool isBusy)
    {
        _refreshButton.Enabled = !isBusy;
        _saveButton.Enabled = !isBusy;
        _forgetButton.Enabled = !isBusy;
        Cursor = isBusy ? Cursors.WaitCursor : Cursors.Default;
    }

    // Add a label (left) and its control (right) to one editor row.
    private static void AddEditorRow(TableLayoutPanel layout, int row, string label, Control control)
    {
        var header = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(184, 204, 224),
            Font = new Font("Bahnschrift SemiBold", 10f, FontStyle.Bold, GraphicsUnit.Point),
            Text = label,
            TextAlign = ContentAlignment.MiddleLeft
        };

        control.Dock = DockStyle.Fill;
        layout.Controls.Add(header, 0, row);
        layout.Controls.Add(control, 1, row);
    }

    // The remaining helpers create the dark-themed controls with a shared look.
    private static Button CreateButton(string text)
    {
        return new Button
        {
            AutoSize = true,
            Height = 32,
            Margin = new Padding(0, 6, 8, 0),
            Padding = new Padding(14, 0, 14, 0),
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(20, 34, 50),
            ForeColor = Color.FromArgb(236, 243, 252)
        };
    }

    private static Label CreateValueLabel()
    {
        return new Label
        {
            AutoEllipsis = true,
            ForeColor = Color.FromArgb(236, 243, 252),
            Font = new Font("Consolas", 10f, FontStyle.Regular, GraphicsUnit.Point),
            TextAlign = ContentAlignment.MiddleLeft
        };
    }

    private static TextBox CreateTextBox(bool multiline = false)
    {
        return new TextBox
        {
            Multiline = multiline,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(10, 18, 30),
            ForeColor = Color.FromArgb(236, 243, 252)
        };
    }

    private static ComboBox CreateComboBox(IEnumerable<string> values)
    {
        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(10, 18, 30),
            ForeColor = Color.FromArgb(236, 243, 252)
        };

        foreach (var value in values)
        {
            combo.Items.Add(value);
        }

        combo.SelectedIndex = 0;
        return combo;
    }

    private static DateTimePicker CreateDateTimePicker()
    {
        return new DateTimePicker
        {
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "yyyy-MM-dd h:mm tt",
            ShowCheckBox = true,
            CalendarMonthBackground = Color.FromArgb(10, 18, 30)
        };
    }

    // "a, b, A" becomes ["a", "b"]: split on commas, trim, drop empties, and drop duplicates ignoring case.
    private static string[] ParseTags(string value)
    {
        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // Pick the combo item matching the value (ignoring case), otherwise the first item.
    private static void SelectComboValue(ComboBox comboBox, string? value, string fallback)
    {
        var target = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var index = comboBox.Items.Cast<object>()
            .Select((item, idx) => new { Value = item.ToString() ?? string.Empty, Index = idx })
            .FirstOrDefault(item => string.Equals(item.Value, target, StringComparison.OrdinalIgnoreCase))
            ?.Index ?? 0;
        comboBox.SelectedIndex = index;
    }

    // First 8 characters of an id, for display.
    private static string ShortId(string id)
    {
        return string.IsNullOrWhiteSpace(id) ? "n/a" : id.Length <= 8 ? id : id[..8];
    }

    // One grid row: Id (short), FullId (used to look the note up again), and the shown columns. FullId has no column.
    private sealed record MemoryRow(
        string Id,
        string FullId,
        string Kind,
        string Category,
        string Content,
        string Due,
        string Retention);
}
