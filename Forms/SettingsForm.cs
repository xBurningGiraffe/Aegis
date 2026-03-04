using Aegis.Models;
using Aegis.Services;

namespace Aegis.Forms;

/// <summary>
/// Main configuration window.
/// Layout: destination bar (top) │ SplitContainer [job list | job editor] (fill) │
///         options strip (bottom) │ log tail (bottom) │ button bar (bottom).
/// </summary>
public sealed class SettingsForm : Form
{
    // ── Settings objects ─────────────────────────────────────────────────────
    private readonly AppSettings     _live;     // The object owned by TrayApplicationContext
    private readonly AppSettings     _work;     // Deep copy used for all editing
    private int                      _selIdx = -1;

    // ── Destination controls ─────────────────────────────────────────────────
    private ComboBox         _cbDestDrive  = null!;
    private TextBox          _txtRootPath  = null!;
    private NumericUpDown    _nudVhdMax    = null!;
    private NumericUpDown    _nudRetain    = null!;

    // ── Job list (left panel) ─────────────────────────────────────────────────
    private ListView         _lvJobs       = null!;

    // ── Job editor (right panel) ──────────────────────────────────────────────
    private Panel            _pnlEditor    = null!;
    private Panel            _pnlNoJob     = null!;
    private ComboBox         _cbSrcDrive   = null!;
    private TextBox          _txtLabel     = null!;
    // Full
    private CheckBox         _chkFull      = null!;
    private ComboBox         _cbFullDay    = null!;
    private DateTimePicker   _dtpFullTime  = null!;
    // Diff
    private CheckBox         _chkDiff      = null!;
    private DateTimePicker   _dtpDiffTime  = null!;
    // Paths
    private ListBox          _lbPaths      = null!;
    // Exclusions
    private ListBox          _lbExcl       = null!;

    // ── Options ───────────────────────────────────────────────────────────────
    private CheckBox _chkStartWithWindows = null!;
    private CheckBox _chkNotifications    = null!;

    // ── Log tail ─────────────────────────────────────────────────────────────
    private RichTextBox _rtbLog = null!;

    // ── Splitter (distance set on Load once layout has run) ───────────────────
    private SplitContainer _splitJobs = null!;

    // ── Event ─────────────────────────────────────────────────────────────────
    public event EventHandler<AppSettings>? SettingsSaved;

    // WM_SETTINGCHANGE is broadcast whenever system settings change, including the
    // "Apps use light/dark theme" preference in Personalisation.
    private const int WM_SETTINGCHANGE = 0x001A;

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WM_SETTINGCHANGE && ThemeManager.Detect())
        {
            ThemeManager.Apply(this);
            ThemeManager.ApplyNativeThemes(this);  // re-theme OS-drawn control parts
            RefreshLogTail();   // re-colour RichTextBox text runs with the new foreground colour
            RefreshJobList();   // ensure ListView items inherit the new ListView ForeColor
        }
    }

    // =========================================================================
    public SettingsForm(AppSettings liveSettings)
    {
        SuspendLayout();

        _live = liveSettings;
        _work = SettingsService.DeepCopy(liveSettings);

        Text                 = "Aegis — Settings";
        Size                 = new Size(900, 640);
        MinimumSize          = new Size(780, 500);
        StartPosition        = FormStartPosition.Manual;   // Positioned explicitly in Load
        FormBorderStyle      = FormBorderStyle.Sizable;
        AutoScaleDimensions  = new SizeF(96F, 96F);  // design-time DPI — enables proper DPI scaling
        AutoScaleMode        = AutoScaleMode.Dpi;
        Icon                 = TrayIconRenderer.Render(TrayIconState.Idle);

        // Detect theme early so RefreshLogTail / RefreshJobList write text in the
        // correct foreground colour (ThemeManager.Foreground) from the start.
        ThemeManager.Detect();

        BuildLayout();
        LoadGlobalSettingsIntoControls();
        RefreshJobList();
        RefreshLogTail();

        if (_work.Jobs.Count > 0) SelectJob(0);

        // Center on the working area (taskbar excluded) rather than the full screen.
        // With AutoScaleDimensions set, WinForms scales the form and all children by
        // DPI/96 at runtime.  After scaling, Form.Width/Height and Screen.WorkingArea
        // are both in physical pixels — simple arithmetic, no unit conversion needed.
        // Re-apply theme here because this is the first point at which all control
        // native handles exist, which is required for UseVisualStyleBackColor and
        // FlatStyle changes to fully take effect.
        Load += (_, _) =>
        {
            var wa = Screen.FromControl(this).WorkingArea;
            int w  = Math.Min(Width,  wa.Width);
            int h  = Math.Min(Height, wa.Height);
            SetBounds(wa.X + (wa.Width  - w) / 2,
                      wa.Y + (wa.Height - h) / 2,
                      w, h);
            ThemeManager.Apply(this);
        };

        // After the form is fully visible: apply SetWindowTheme to OS-drawn control
        // parts (ComboBox dropdown arrow, ListView rows, NumericUpDown spinners).
        // Also set the preferred left-panel splitter width.
        Shown += (_, _) =>
        {
            int minNeeded = _splitJobs.Panel1MinSize + _splitJobs.Panel2MinSize + _splitJobs.SplitterWidth;
            if (_splitJobs.Width >= minNeeded)
            {
                int target = (int)(210 * DeviceDpi / 96f);
                int max    = _splitJobs.Width - _splitJobs.Panel2MinSize - _splitJobs.SplitterWidth;
                _splitJobs.SplitterDistance = Math.Min(target, max);
            }
            ThemeManager.ApplyNativeThemes(this);
        };

        ResumeLayout(false);
        PerformLayout();

        ThemeManager.Apply(this);
    }

    // =========================================================================
    // Layout construction
    // =========================================================================

    private void BuildLayout()
    {
        // Pure Dock-based layout — no TLP for the main form structure.
        // Each section is docked directly to the form.  This is the most
        // reliable WinForms layout and immune to TLP row-math / DPI issues.
        Padding = new Padding(6);

        var dest = BuildDestinationPanel();
        var jobs = BuildJobSplitter();
        var opts = BuildOptionsPanel();
        var log  = BuildLogPanel();
        var btns = BuildButtonPanel();

        dest.Dock = DockStyle.Top;    dest.Height = 140;
        opts.Dock = DockStyle.Bottom; opts.Height = 72;
        log.Dock  = DockStyle.Bottom; log.Height  = 100;
        btns.Dock = DockStyle.Bottom; btns.Height = 40;
        jobs.Dock = DockStyle.Fill;

        // WinForms processes docking from highest index (front) to lowest (back).
        // Bottom-docked controls at higher indices sit closer to the form's bottom.
        // The Fill-docked control at index 0 (back) receives whatever space remains.
        Controls.AddRange(new Control[] { jobs, dest, opts, log, btns });
    }

    // ── Row 0: Backup Destination ─────────────────────────────────────────────

    private Control BuildDestinationPanel()
    {
        var grp = new GroupBox { Text = "Backup Destination", Dock = DockStyle.Fill, Padding = new Padding(6) };

        // Two-row table inside the GroupBox.
        // Percent rows always fill exactly the GroupBox's DisplayRectangle — no overflow
        // regardless of DPI, font size, or platform rendering of the GroupBox border/title.
        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 55)); // drive + path row
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 45)); // VHD / retention row

        // ── Top row: Drive | combo | "Root path:" | textbox (fills) | Browse ─────
        var topRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1 };
        topRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));     // "Drive:"
        topRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72)); // drive combo
        topRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));     // "Root path:"
        topRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // path textbox
        topRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88)); // Browse button
        topRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // Anchor = None centres the control both horizontally and vertically in its cell
        // without suppressing AutoSize (unlike Dock = Fill, which kills AutoSize on labels).
        topRow.Controls.Add(new Label { Text = "Drive:", AutoSize = true,
            Anchor = AnchorStyles.None }, 0, 0);
        _cbDestDrive = new ComboBox { Width = 68, DropDownStyle = ComboBoxStyle.DropDown,
            Anchor = AnchorStyles.None };
        foreach (var d in DriveInfo.GetDrives()) _cbDestDrive.Items.Add(d.Name[..2]);
        topRow.Controls.Add(_cbDestDrive, 1, 0);
        topRow.Controls.Add(new Label { Text = "Root path:", AutoSize = true,
            Anchor = AnchorStyles.None }, 2, 0);
        _txtRootPath = new TextBox { Dock = DockStyle.Fill };
        topRow.Controls.Add(_txtRootPath, 3, 0);
        var btnBrowse = new Button { Text = "Browse…", Dock = DockStyle.Fill };
        btnBrowse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "Select backup root folder" };
            if (dlg.ShowDialog(this) == DialogResult.OK) _txtRootPath.Text = dlg.SelectedPath;
        };
        topRow.Controls.Add(btnBrowse, 4, 0);

        // ── Bottom row: VHD max size and retention ────────────────────────────────
        // FlowLayoutPanel keeps controls at their natural height and lets Margin
        // handle the vertical spacing instead of fragile fixed Top coordinates.
        var botPanel = new FlowLayoutPanel
        {
            Dock          = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents  = false,
        };
        static Label FL(string t) =>
            new() { Text = t, AutoSize = true, Margin = new Padding(4, 6, 4, 0) };

        botPanel.Controls.Add(FL("VHD max size:"));
        _nudVhdMax = new NumericUpDown { Width = 76, Minimum = 20, Maximum = 4000, Value = 120,
            Margin = new Padding(0, 3, 6, 0) };
        botPanel.Controls.Add(_nudVhdMax);
        botPanel.Controls.Add(FL("GB"));

        botPanel.Controls.Add(new Label { Text = "Keep differentials:", AutoSize = true,
            Margin = new Padding(14, 6, 4, 0) });
        _nudRetain = new NumericUpDown { Width = 64, Minimum = 1, Maximum = 365, Value = 7,
            Margin = new Padding(0, 3, 6, 0) };
        botPanel.Controls.Add(_nudRetain);
        botPanel.Controls.Add(FL("days"));

        outer.Controls.Add(topRow,   0, 0);
        outer.Controls.Add(botPanel, 0, 1);
        grp.Controls.Add(outer);
        return grp;
    }

    // ── Row 1: SplitContainer ─────────────────────────────────────────────────

    private Control BuildJobSplitter()
    {
        _splitJobs = new SplitContainer
        {
            Dock          = DockStyle.Fill,
            Orientation   = Orientation.Vertical,
            Panel1MinSize = 150,
            // Panel2MinSize intentionally left at default (25).
            // Setting it to a large value (e.g. 300) causes WinForms to throw
            // "SplitterDistance must be between Panel1MinSize and Width - Panel2MinSize"
            // when the form is resizing from its initial default size to its target size,
            // because Panel1MinSize + Panel2MinSize + SplitterWidth can exceed the
            // intermediate control width during construction.
        };
        var sc = _splitJobs;

        // Left panel: job list
        BuildJobListPanel(sc.Panel1);

        // Right panel: editor or placeholder
        _pnlNoJob = new Panel { Dock = DockStyle.Fill };
        var hint = new Label
        {
            Text      = "Select a job from the list\nor click  +  to add a new one.",
            TextAlign = ContentAlignment.MiddleCenter,
            Dock      = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
        };
        _pnlNoJob.Controls.Add(hint);

        _pnlEditor = BuildJobEditorPanel();
        _pnlEditor.Visible = false;

        sc.Panel2.Controls.Add(_pnlEditor);
        sc.Panel2.Controls.Add(_pnlNoJob);

        return sc;
    }

    private void BuildJobListPanel(SplitterPanel panel)
    {
        // Toolbar
        var ts = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(2, 0, 0, 0) };
        var btnAdd = new ToolStripButton("+") { ToolTipText = "Add new backup job", Font = new Font("Segoe UI", 10, FontStyle.Bold) };
        var btnRemove = new ToolStripButton("−") { ToolTipText = "Remove selected job", Font = new Font("Segoe UI", 10, FontStyle.Bold) };
        btnAdd.Click    += OnAddJob;
        btnRemove.Click += OnRemoveJob;
        ts.Items.Add(btnAdd);
        ts.Items.Add(btnRemove);
        panel.Controls.Add(ts);

        // List
        _lvJobs = new ListView
        {
            Dock        = DockStyle.Fill,
            View        = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            HeaderStyle = ColumnHeaderStyle.None,
        };
        _lvJobs.Columns.Add("Job", -2);
        _lvJobs.SelectedIndexChanged += OnJobSelectionChanged;
        panel.Controls.Add(_lvJobs);
    }

    private Panel BuildJobEditorPanel()
    {
        var pnl = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12) };

        int y = 12;
        const int lw  = 100;   // label column width
        const int gap = 14;    // vertical gap between sections

        // ── Source drive (own row) ─────────────────────────────────────────
        pnl.Controls.Add(L("Source Drive:", 0, y + 3));
        _cbSrcDrive = new ComboBox { Left = lw, Top = y, Width = 80, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var d in DriveInfo.GetDrives()) _cbSrcDrive.Items.Add(d.Name[..2]);
        pnl.Controls.Add(_cbSrcDrive);
        y += 32;

        // ── Label (own row) ────────────────────────────────────────────────
        pnl.Controls.Add(L("Label:", 0, y + 3));
        _txtLabel = new TextBox { Left = lw, Top = y, Width = 340 };
        pnl.Controls.Add(_txtLabel);
        y += 32 + gap;

        // ── Full system image ──────────────────────────────────────────────
        var grpFull = new GroupBox { Text = "Full System Image", Left = 0, Top = y, Width = 540, Height = 100 };
        int gy = 22;
        _chkFull = new CheckBox { Text = "Enable weekly full backup", Left = 10, Top = gy, AutoSize = true };
        grpFull.Controls.Add(_chkFull);
        gy += 32;

        grpFull.Controls.Add(L("Day:", 10, gy + 3));
        _cbFullDay = new ComboBox { Left = 42, Top = gy, Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var name in Enum.GetNames<DayOfWeek>()) _cbFullDay.Items.Add(name);
        _cbFullDay.SelectedIndex = 0;
        grpFull.Controls.Add(_cbFullDay);

        grpFull.Controls.Add(L("At:", 154, gy + 3));
        _dtpFullTime = MakeTimePicker(178, gy);
        grpFull.Controls.Add(_dtpFullTime);

        _chkFull.CheckedChanged += (_, _) =>
        {
            _cbFullDay.Enabled   = _chkFull.Checked;
            _dtpFullTime.Enabled = _chkFull.Checked;
        };
        pnl.Controls.Add(grpFull);
        y += grpFull.Height + gap;

        // ── Daily differential ─────────────────────────────────────────────
        var grpDiff = new GroupBox { Text = "Daily Differential", Left = 0, Top = y, Width = 540, Height = 74 };
        _chkDiff = new CheckBox { Text = "Enable daily differential backup", Left = 10, Top = 22, AutoSize = true };
        grpDiff.Controls.Add(_chkDiff);
        grpDiff.Controls.Add(L("At:", 10, 52));
        _dtpDiffTime = MakeTimePicker(36, 48);
        grpDiff.Controls.Add(_dtpDiffTime);
        _chkDiff.CheckedChanged += (_, _) => _dtpDiffTime.Enabled = _chkDiff.Checked;
        pnl.Controls.Add(grpDiff);
        y += grpDiff.Height + gap;

        // ── Included paths ─────────────────────────────────────────────────
        var grpPaths = new GroupBox { Text = "Included Paths", Left = 0, Top = y, Width = 540, Height = 190 };
        _lbPaths = new ListBox { Left = 10, Top = 22, Width = 516, Height = 118, HorizontalScrollbar = true };
        grpPaths.Controls.Add(_lbPaths);

        var btnAddPath = new Button { Text = "+ Add Path", Left = 10, Top = 148, Width = 90, Height = 28 };
        btnAddPath.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "Add folder to backup" };
            if (dlg.ShowDialog(this) == DialogResult.OK) _lbPaths.Items.Add(dlg.SelectedPath);
        };
        var btnRemPath = new Button { Text = "Remove", Left = 108, Top = 148, Width = 80, Height = 28 };
        btnRemPath.Click += (_, _) =>
        {
            if (_lbPaths.SelectedIndex >= 0) _lbPaths.Items.RemoveAt(_lbPaths.SelectedIndex);
        };
        grpPaths.Controls.Add(btnAddPath);
        grpPaths.Controls.Add(btnRemPath);
        pnl.Controls.Add(grpPaths);
        y += grpPaths.Height + gap;

        // ── Exclusions ─────────────────────────────────────────────────────
        var grpExcl = new GroupBox { Text = "Exclusions  (case-insensitive substring, e.g. \\Cache\\)", Left = 0, Top = y, Width = 540, Height = 190 };
        _lbExcl = new ListBox { Left = 10, Top = 22, Width = 516, Height = 118, HorizontalScrollbar = true };
        grpExcl.Controls.Add(_lbExcl);

        var btnAddExcl = new Button { Text = "+ Add", Left = 10, Top = 148, Width = 70, Height = 28 };
        btnAddExcl.Click += (_, _) =>
        {
            var v = ShowInputDialog("Enter exclusion pattern (e.g. \\Cache\\):", "Add Exclusion");
            if (!string.IsNullOrWhiteSpace(v)) _lbExcl.Items.Add(v.Trim());
        };
        var btnRemExcl = new Button { Text = "Remove", Left = 88, Top = 148, Width = 80, Height = 28 };
        btnRemExcl.Click += (_, _) =>
        {
            if (_lbExcl.SelectedIndex >= 0) _lbExcl.Items.RemoveAt(_lbExcl.SelectedIndex);
        };
        grpExcl.Controls.Add(btnAddExcl);
        grpExcl.Controls.Add(btnRemExcl);
        pnl.Controls.Add(grpExcl);

        return pnl;
    }

    // ── Row 2: Options ────────────────────────────────────────────────────────

    private Control BuildOptionsPanel()
    {
        var grp = new GroupBox { Text = "Options", Dock = DockStyle.Fill, Padding = new Padding(6) };

        _chkStartWithWindows = new CheckBox { Text = "Start Aegis automatically when Windows starts", Left = 8, Top = 20, AutoSize = true };
        _chkNotifications    = new CheckBox { Text = "Show notifications when backups complete",           Left = 8, Top = 44, AutoSize = true };

        grp.Controls.Add(_chkStartWithWindows);
        grp.Controls.Add(_chkNotifications);
        return grp;
    }

    // ── Row 3: Log tail ───────────────────────────────────────────────────────

    private Control BuildLogPanel()
    {
        var tbl = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 1,
            RowCount    = 2,
            Padding     = new Padding(0),
        };
        tbl.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // "Recent activity:" label
        tbl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // log RichTextBox

        tbl.Controls.Add(new Label { Text = "Recent activity:", AutoSize = true,
            Margin = new Padding(4, 2, 0, 2) }, 0, 0);

        _rtbLog = new RichTextBox
        {
            Dock       = DockStyle.Fill,
            ReadOnly   = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            Font       = new Font("Consolas", 8.5f),
            BackColor  = SystemColors.Window,
        };
        tbl.Controls.Add(_rtbLog, 0, 1);
        return tbl;
    }

    // ── Button bar (Dock.Bottom, outside the TLP) ─────────────────────────────

    private Control BuildButtonPanel()
    {
        var pnl = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Padding       = new Padding(4),
        };

        var btnCancel = new Button { Text = "Cancel", Width = 80 };
        btnCancel.Click += (_, _) => Close();

        var btnSave = new Button { Text = "Save && Apply", Width = 110 };
        btnSave.Click += OnSave;

        pnl.Controls.Add(btnCancel);
        pnl.Controls.Add(btnSave);
        return pnl;
    }

    // =========================================================================
    // Loading / saving working data to/from controls
    // =========================================================================

    private void LoadGlobalSettingsIntoControls()
    {
        _cbDestDrive.Text = _work.BackupDestDrive;
        _txtRootPath.Text          = _work.BackupRootPath;
        _nudVhdMax.Value           = Math.Max(_nudVhdMax.Minimum, Math.Min(_nudVhdMax.Maximum, _work.VhdMaxGb));
        _nudRetain.Value           = Math.Max(_nudRetain.Minimum, Math.Min(_nudRetain.Maximum, _work.RetainDifferentials));
        _chkStartWithWindows.Checked = _work.StartWithWindows;
        _chkNotifications.Checked    = _work.ShowNotifications;
    }

    private void SaveGlobalSettingsFromControls()
    {
        _work.BackupDestDrive      = _cbDestDrive.Text;
        _work.BackupRootPath       = _txtRootPath.Text;
        _work.VhdMaxGb             = (int)_nudVhdMax.Value;
        _work.RetainDifferentials  = (int)_nudRetain.Value;
        _work.StartWithWindows     = _chkStartWithWindows.Checked;
        _work.ShowNotifications    = _chkNotifications.Checked;
    }

    private void LoadJobIntoEditor(BackupJob job)
    {
        SelectComboByText(_cbSrcDrive, job.SourceDrive);
        _txtLabel.Text = job.Label;

        _chkFull.Checked = job.FullEnabled;
        SelectComboByText(_cbFullDay, job.FullDayOfWeek.ToString());
        _dtpFullTime.Value   = DateTime.Today.Add(job.FullTime);
        _cbFullDay.Enabled   = job.FullEnabled;
        _dtpFullTime.Enabled = job.FullEnabled;

        _chkDiff.Checked     = job.DiffEnabled;
        _dtpDiffTime.Value   = DateTime.Today.Add(job.DiffTime);
        _dtpDiffTime.Enabled = job.DiffEnabled;

        _lbPaths.Items.Clear();
        foreach (var p in job.UserDataPaths) _lbPaths.Items.Add(p);

        _lbExcl.Items.Clear();
        foreach (var e in job.Exclusions) _lbExcl.Items.Add(e);
    }

    private void SaveEditorIntoJob(BackupJob job)
    {
        job.SourceDrive  = _cbSrcDrive.Text;
        job.Label        = _txtLabel.Text;

        job.FullEnabled   = _chkFull.Checked;
        job.FullDayOfWeek = Enum.Parse<DayOfWeek>(_cbFullDay.Text);
        job.FullTime      = _dtpFullTime.Value.TimeOfDay;

        job.DiffEnabled  = _chkDiff.Checked;
        job.DiffTime     = _dtpDiffTime.Value.TimeOfDay;

        job.UserDataPaths = _lbPaths.Items.Cast<string>().ToList();
        job.Exclusions    = _lbExcl.Items.Cast<string>().ToList();
    }

    // =========================================================================
    // Job list management
    // =========================================================================

    private void RefreshJobList()
    {
        _lvJobs.BeginUpdate();
        _lvJobs.Items.Clear();
        foreach (var job in _work.Jobs)
        {
            var fullOk = !job.LastFullStatus.StartsWith("Error", StringComparison.OrdinalIgnoreCase);
            var diffOk = !job.LastDiffStatus.StartsWith("Error", StringComparison.OrdinalIgnoreCase);
            var status = (fullOk && diffOk) ? "✓" : "✗";
            var item   = new ListViewItem($" {status}  {job.SourceDrive}  {job.Label}");
            if (!(fullOk && diffOk)) item.ForeColor = Color.Firebrick;
            _lvJobs.Items.Add(item);
        }
        _lvJobs.AutoResizeColumns(ColumnHeaderAutoResizeStyle.ColumnContent);
        _lvJobs.EndUpdate();
    }

    private void SelectJob(int index)
    {
        // Persist edits from the currently shown job first
        if (_selIdx >= 0 && _selIdx < _work.Jobs.Count)
            SaveEditorIntoJob(_work.Jobs[_selIdx]);

        _selIdx = index;

        if (_selIdx < 0 || _selIdx >= _work.Jobs.Count)
        {
            _pnlEditor.Visible = false;
            _pnlNoJob.Visible  = true;
            return;
        }

        LoadJobIntoEditor(_work.Jobs[_selIdx]);
        _pnlEditor.Visible = true;
        _pnlNoJob.Visible  = false;

        if (_lvJobs.SelectedIndices.Count == 0 || _lvJobs.SelectedIndices[0] != _selIdx)
        {
            _lvJobs.Items[_selIdx].Selected = true;
            _lvJobs.EnsureVisible(_selIdx);
        }
    }

    private void OnJobSelectionChanged(object? sender, EventArgs e)
    {
        if (_lvJobs.SelectedIndices.Count == 0) return;
        SelectJob(_lvJobs.SelectedIndices[0]);
    }

    private void OnAddJob(object? sender, EventArgs e)
    {
        var job = new BackupJob
        {
            Label       = "New Drive",
            SourceDrive = "C:",
        };
        _work.Jobs.Add(job);
        RefreshJobList();
        SelectJob(_work.Jobs.Count - 1);
    }

    private void OnRemoveJob(object? sender, EventArgs e)
    {
        if (_selIdx < 0 || _selIdx >= _work.Jobs.Count) return;

        var job = _work.Jobs[_selIdx];
        var result = MessageBox.Show(
            $"Remove backup job for {job.SourceDrive} — {job.Label}?",
            "Confirm Remove", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (result != DialogResult.Yes) return;

        _work.Jobs.RemoveAt(_selIdx);
        _selIdx = -1;
        RefreshJobList();

        if (_work.Jobs.Count > 0)
            SelectJob(0);
        else
        {
            _pnlEditor.Visible = false;
            _pnlNoJob.Visible  = true;
        }
    }

    // =========================================================================
    // Save & Apply
    // =========================================================================

    private void OnSave(object? sender, EventArgs e)
    {
        // Flush current editor state into working copy
        if (_selIdx >= 0 && _selIdx < _work.Jobs.Count)
            SaveEditorIntoJob(_work.Jobs[_selIdx]);

        SaveGlobalSettingsFromControls();

        // Merge working copy into live object (preserving runtime state for existing jobs)
        _live.BackupDestDrive     = _work.BackupDestDrive;
        _live.BackupRootPath      = _work.BackupRootPath;
        _live.VhdMaxGb            = _work.VhdMaxGb;
        _live.RetainDifferentials = _work.RetainDifferentials;
        _live.StartWithWindows    = _work.StartWithWindows;
        _live.ShowNotifications   = _work.ShowNotifications;

        var merged = new List<BackupJob>();
        foreach (var edited in _work.Jobs)
        {
            var existing = _live.Jobs.FirstOrDefault(j => j.Id == edited.Id);
            if (existing != null)
            {
                // Overwrite editable config; preserve runtime state from live object
                existing.SourceDrive   = edited.SourceDrive;
                existing.Label         = edited.Label;
                existing.FullEnabled   = edited.FullEnabled;
                existing.FullDayOfWeek = edited.FullDayOfWeek;
                existing.FullTimeTicks = edited.FullTimeTicks;
                existing.DiffEnabled   = edited.DiffEnabled;
                existing.DiffTimeTicks = edited.DiffTimeTicks;
                existing.UserDataPaths = edited.UserDataPaths;
                existing.Exclusions    = edited.Exclusions;
                merged.Add(existing);
            }
            else
            {
                // Brand-new job — add as-is (runtime state is already at defaults)
                merged.Add(edited);
            }
            // Jobs in _live that are NOT in _work.Jobs are implicitly removed
        }
        _live.Jobs = merged;

        SettingsSaved?.Invoke(this, _live);
        Close();
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private void RefreshLogTail()
    {
        var lines = SettingsService.GetRecentLogLines(30);
        _rtbLog.Clear();
        foreach (var line in lines)
        {
            var color = line.Contains("[ERROR]") ? Color.Firebrick
                      : line.Contains("[WARN]")  ? Color.DarkOrange
                      : ThemeManager.Foreground;
            _rtbLog.SelectionColor = color;
            _rtbLog.AppendText(line + "\n");
        }
        _rtbLog.ScrollToCaret();
    }

    private static Label L(string text, int x, int y) =>
        new() { Text = text, Left = x, Top = y, AutoSize = true };

    private static DateTimePicker MakeTimePicker(int x, int y) =>
        new()
        {
            Left         = x,
            Top          = y,
            Width        = 80,
            Format       = DateTimePickerFormat.Custom,
            CustomFormat = "HH:mm",
            ShowUpDown   = true,
            Value        = DateTime.Today,
        };

    private static void SelectComboByText(ComboBox cb, string text)
    {
        for (int i = 0; i < cb.Items.Count; i++)
        {
            if (cb.Items[i]?.ToString()?.Equals(text, StringComparison.OrdinalIgnoreCase) == true)
            {
                cb.SelectedIndex = i;
                return;
            }
        }
        if (cb.Items.Count > 0) cb.SelectedIndex = 0;
    }

    private string? ShowInputDialog(string prompt, string title, string defaultValue = "")
    {
        using var dlg   = new Form { Text = title, Size = new Size(420, 130), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false };
        var lbl         = new Label  { Text = prompt,       Left = 10, Top = 10, AutoSize = true };
        var txt         = new TextBox { Text = defaultValue, Left = 10, Top = 30, Width = 384 };
        var ok          = new Button  { Text = "OK",     Left = 230, Top = 58, Width = 80, DialogResult = DialogResult.OK };
        var cancel      = new Button  { Text = "Cancel", Left = 318, Top = 58, Width = 76, DialogResult = DialogResult.Cancel };
        dlg.Controls.AddRange(new Control[] { lbl, txt, ok, cancel });
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        ThemeManager.Apply(dlg);
        return dlg.ShowDialog(this) == DialogResult.OK ? txt.Text : null;
    }
}
