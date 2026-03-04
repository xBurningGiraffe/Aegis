using System.Diagnostics;
using Aegis.Forms;
using Aegis.Models;
using Aegis.Services;

namespace Aegis;

/// <summary>
/// Owns the NotifyIcon, builds the context menu, reacts to backup events,
/// and manages the lifetime of the settings window.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon      _tray;
    private readonly SchedulerService _scheduler;
    private AppSettings               _settings;
    private SettingsForm?             _settingsForm;
    private System.Windows.Forms.Timer? _animTimer;

    public TrayApplicationContext()
    {
        _settings  = SettingsService.Load();
        _scheduler = new SchedulerService(_settings);

        _tray = new NotifyIcon
        {
            Visible = true,
            Text    = "Aegis — starting…",
            Icon    = TrayIconRenderer.Render(TrayIconState.Idle),
        };
        _tray.DoubleClick += (_, _) => OpenSettings();

        _scheduler.JobStarted  += OnJobStarted;
        _scheduler.JobFinished += OnJobFinished;
        _scheduler.Start();

        RebuildMenu();
        RefreshTrayTooltip();
    }

    // =========================================================================
    // Context menu
    // =========================================================================

    private void RebuildMenu()
    {
        var old = _tray.ContextMenuStrip;

        var menu = new ContextMenuStrip();

        // Title (non-clickable)
        var title = new ToolStripMenuItem("Aegis") { Enabled = false };
        title.Font = new Font(title.Font, FontStyle.Bold);
        menu.Items.Add(title);
        menu.Items.Add(new ToolStripSeparator());

        // Open settings
        menu.Items.Add("Open Settings", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());

        // Per-job entries
        foreach (var job in _settings.Jobs)
        {
            var j = job;  // capture for lambdas

            var jobHeader = new ToolStripMenuItem($"{j.SourceDrive}  —  {j.Label}") { Enabled = false };
            jobHeader.Font = new Font(jobHeader.Font, FontStyle.Italic);
            menu.Items.Add(jobHeader);

            if (j.FullEnabled)
            {
                var item = new ToolStripMenuItem("    Run Full Backup Now");
                item.Click += async (_, _) => await _scheduler.RunJobAsync(j, "FULL");
                menu.Items.Add(item);
            }

            if (j.DiffEnabled)
            {
                var item = new ToolStripMenuItem("    Run Differential Now");
                item.Click += async (_, _) => await _scheduler.RunJobAsync(j, "DIFF");
                menu.Items.Add(item);
            }

            menu.Items.Add(new ToolStripSeparator());
        }

        // View log
        menu.Items.Add("View Log", null, (_, _) =>
        {
            if (File.Exists(SettingsService.LogPath))
                Process.Start("notepad.exe", SettingsService.LogPath);
            else
                MessageBox.Show("No log file yet.", "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });

        // Start with Windows toggle
        var startItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked     = _settings.StartWithWindows,
            CheckOnClick = true,
        };
        startItem.CheckedChanged += (_, _) =>
        {
            _settings.StartWithWindows = startItem.Checked;
            ApplyStartWithWindows(_settings.StartWithWindows);
            SettingsService.Save(_settings);
        };
        menu.Items.Add(startItem);

        menu.Items.Add(new ToolStripSeparator());

        // Exit
        menu.Items.Add("Exit", null, (_, _) =>
        {
            _tray.Visible = false;
            Application.Exit();
        });

        _tray.ContextMenuStrip = menu;
        old?.Dispose();
    }

    // =========================================================================
    // Settings window
    // =========================================================================

    private void OpenSettings()
    {
        if (_settingsForm != null && !_settingsForm.IsDisposed)
        {
            _settingsForm.BringToFront();
            _settingsForm.Focus();
            return;
        }

        _settingsForm = new SettingsForm(_settings);
        _settingsForm.SettingsSaved += OnSettingsSaved;
        _settingsForm.Show();
    }

    private void OnSettingsSaved(object? _, AppSettings updated)
    {
        // _settings and updated are the same object (SettingsForm modifies in-place)
        _settings = updated;
        SettingsService.Save(_settings);
        _scheduler.UpdateSettings(_settings);
        RebuildMenu();
        RefreshTrayTooltip();
    }

    // =========================================================================
    // Backup event handlers
    // =========================================================================

    private void OnJobStarted(object? _, JobEventArgs e)
    {
        // Start animation
        _animTimer?.Stop();
        _animTimer?.Dispose();
        _animTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _animTimer.Tick += (__, ___) =>
        {
            TrayIconRenderer.NextFrame();
            SwapIcon(TrayIconRenderer.Render(TrayIconState.Running));
        };
        _animTimer.Start();
        SwapIcon(TrayIconRenderer.Render(TrayIconState.Running));
        RefreshTrayTooltip();

        if (_settings.ShowNotifications)
            _tray.ShowBalloonTip(3000, "Aegis",
                $"{e.Type} backup started: {e.Job.SourceDrive} — {e.Job.Label}",
                ToolTipIcon.Info);
    }

    private void OnJobFinished(object? _, JobEventArgs e)
    {
        _animTimer?.Stop();
        _animTimer?.Dispose();
        _animTimer = null;

        SwapIcon(TrayIconRenderer.Render(DetermineIconState()));
        RefreshTrayTooltip();

        if (_settings.ShowNotifications)
        {
            if (e.Success)
                _tray.ShowBalloonTip(5000, "Aegis",
                    $"{e.Type} backup completed: {e.Job.SourceDrive} — {e.Job.Label}\n{e.Message}",
                    ToolTipIcon.Info);
            else
                _tray.ShowBalloonTip(8000, "Aegis — Error",
                    $"{e.Type} backup FAILED: {e.Job.Label}\n{e.Message}",
                    ToolTipIcon.Error);
        }
    }

    // =========================================================================
    // Icon helpers
    // =========================================================================

    private void SwapIcon(Icon newIcon)
    {
        var old = _tray.Icon;
        _tray.Icon = newIcon;
        // Don't dispose cached idle/error icons; only dispose Running frames
        if (old != null &&
            old != TrayIconRenderer.Render(TrayIconState.Idle) &&
            old != TrayIconRenderer.Render(TrayIconState.Error))
        {
            old.Dispose();
        }
    }

    private TrayIconState DetermineIconState()
    {
        if (_scheduler.IsRunning) return TrayIconState.Running;

        foreach (var j in _settings.Jobs)
        {
            if (j.LastFullStatus.StartsWith("Error", StringComparison.OrdinalIgnoreCase) ||
                j.LastDiffStatus.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                return TrayIconState.Error;
        }

        return TrayIconState.Idle;
    }

    private void RefreshTrayTooltip()
    {
        string tip;

        if (_scheduler.IsRunning && _scheduler.CurrentJob is { } running)
        {
            tip = $"Aegis — {_scheduler.CurrentJobType} running\n{running.SourceDrive} {running.Label}";
        }
        else if (_settings.Jobs.Count == 0)
        {
            tip = "Aegis — no jobs configured";
        }
        else
        {
            var parts = _settings.Jobs.Select(j =>
            {
                var full = j.LastFullBackupUtc.HasValue ? AgeLabel(j.LastFullBackupUtc.Value) : "never";
                var diff = j.LastDiffBackupUtc.HasValue ? AgeLabel(j.LastDiffBackupUtc.Value) : "never";
                return $"{j.SourceDrive} Full:{full} Diff:{diff}";
            });
            tip = "Aegis\n" + string.Join("\n", parts);
        }

        // NotifyIcon tooltip is limited to 63 chars in older Windows; truncate gracefully
        _tray.Text = tip.Length > 63 ? tip[..60] + "…" : tip;
    }

    private static string AgeLabel(DateTime utc)
    {
        int days = (DateTime.UtcNow - utc).Days;
        return days == 0 ? "today" : days == 1 ? "1d ago" : $"{days}d ago";
    }

    // =========================================================================
    // Start with Windows
    // =========================================================================

    private static void ApplyStartWithWindows(bool enable)
    {
        const string runKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKey, writable: true);
        if (key == null) return;

        if (enable)
            key.SetValue("Aegis", $"\"{Application.ExecutablePath}\"");
        else
            key.DeleteValue("Aegis", throwOnMissingValue: false);
    }

    // =========================================================================
    // Cleanup
    // =========================================================================

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _scheduler.Stop();
            _scheduler.Dispose();
            _animTimer?.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
