using Aegis.Models;

namespace Aegis.Services;

// ─────────────────────────────────────────────────────────────────────────────
// Event args
// ─────────────────────────────────────────────────────────────────────────────

public sealed class JobEventArgs : EventArgs
{
    public BackupJob Job     { get; }
    public string    Type    { get; }   // "FULL" or "DIFF"
    public bool      Success { get; }
    public string    Message { get; }

    public JobEventArgs(BackupJob job, string type, bool success, string message)
    {
        Job = job; Type = type; Success = success; Message = message;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Scheduler
// ─────────────────────────────────────────────────────────────────────────────

public sealed class SchedulerService : IDisposable
{
    private readonly System.Timers.Timer _timer;
    private readonly SemaphoreSlim       _lock    = new(1, 1);
    private readonly SynchronizationContext _uiCtx;
    private AppSettings _settings;
    private bool _disposed;

    // Tracks the last time the scheduler itself dispatched each job (keyed by
    // "SourceDrive|TYPE").  Prevents a fast-failing backup from being retried on
    // every timer tick within the same ±1-minute IsDue window.
    private readonly Dictionary<string, DateTime> _lastScheduledAttempt = new();

    // ----- Public state -------------------------------------------------------

    public bool       IsRunning       { get; private set; }
    public BackupJob? CurrentJob      { get; private set; }
    public string     CurrentJobType  { get; private set; } = "";

    // ----- Events (always raised on the UI thread) ----------------------------

    public event EventHandler<JobEventArgs>? JobStarted;
    public event EventHandler<JobEventArgs>? JobFinished;

    // -------------------------------------------------------------------------

    public SchedulerService(AppSettings settings)
    {
        _settings = settings;
        _uiCtx    = SynchronizationContext.Current ?? new SynchronizationContext();

        _timer           = new System.Timers.Timer(60_000);  // tick every minute
        _timer.Elapsed  += OnTick;
        _timer.AutoReset = true;
    }

    public void UpdateSettings(AppSettings settings) => _settings = settings;

    public void Start() => _timer.Start();
    public void Stop()  => _timer.Stop();

    // ─────────────────────────────────────────────────────────────────────────
    // Timer tick — runs on a thread-pool thread
    // ─────────────────────────────────────────────────────────────────────────

    private void OnTick(object? _, System.Timers.ElapsedEventArgs __)
    {
        var now = DateTime.Now;

        foreach (var job in _settings.Jobs.ToList())
        {
            // Full backup due?
            if (job.FullEnabled &&
                IsDue(now, job.FullDayOfWeek, job.FullTime, job.LastFullBackupUtc) &&
                NotRecentlyAttempted(job.SourceDrive, "FULL", now))
            {
                RecordAttempt(job.SourceDrive, "FULL", now);
                _ = RunJobAsync(job, "FULL");
                return;   // One job at a time; next tick handles any others
            }

            // Differential due? (skip on full-backup day so full runs first)
            if (job.DiffEnabled &&
                now.DayOfWeek != job.FullDayOfWeek &&
                IsDue(now, dayOfWeek: null, job.DiffTime, job.LastDiffBackupUtc) &&
                NotRecentlyAttempted(job.SourceDrive, "DIFF", now))
            {
                RecordAttempt(job.SourceDrive, "DIFF", now);
                _ = RunJobAsync(job, "DIFF");
                return;
            }
        }
    }

    private static bool IsDue(
        DateTime now, DayOfWeek? dayOfWeek,
        TimeSpan scheduledTime, DateTime? lastRun)
    {
        if (dayOfWeek.HasValue && now.DayOfWeek != dayOfWeek.Value)
            return false;

        // Within one minute of scheduled time?
        if (Math.Abs((now.TimeOfDay - scheduledTime).TotalMinutes) > 1.0)
            return false;

        // Has it already run today?
        if (lastRun.HasValue && lastRun.Value.ToLocalTime().Date >= now.Date)
            return false;

        return true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Job runner (can also be called directly from the tray "Run Now" menu)
    // ─────────────────────────────────────────────────────────────────────────

    public async Task RunJobAsync(BackupJob job, string type)
    {
        // Diagnostic: always log that we were invoked, even before the semaphore check.
        SettingsService.AppendLog(job.SourceDrive, type, "INFO",
            $"RunJobAsync invoked — acquiring lock.");

        // Non-blocking tryacquire — silently skip if another backup is already in progress
        if (!await _lock.WaitAsync(0))
        {
            SettingsService.AppendLog(job.SourceDrive, type, "WARN",
                "Skipped — another backup is already running.");
            return;
        }

        try
        {
            IsRunning      = true;
            CurrentJob     = job;
            CurrentJobType = type;

            RaiseOnUi(JobStarted, new JobEventArgs(job, type, true, "Started"));

            var progress = new Progress<string>(msg =>
                SettingsService.AppendLog(job.SourceDrive, type, "INFO", msg));

            bool   success;
            string message;

            try
            {
                if (type == "FULL")
                    await BackupEngine.RunFullBackupAsync(job, _settings, progress, CancellationToken.None);
                else
                    await BackupEngine.RunDifferentialBackupAsync(job, _settings, progress, CancellationToken.None);

                success = true;
                message = type == "FULL" ? job.LastFullStatus : job.LastDiffStatus;
            }
            catch (Exception ex)
            {
                success = false;
                message = ex.Message;
            }

            SettingsService.Save(_settings);
            RaiseOnUi(JobFinished, new JobEventArgs(job, type, success, message));
        }
        finally
        {
            IsRunning  = false;
            CurrentJob = null;
            _lock.Release();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    // Returns true if the scheduler has NOT dispatched this job within the last
    // 30 minutes.  This prevents a fast-failing backup from being retried on every
    // timer tick while the IsDue window is still open (the window spans ~2 minutes).
    private bool NotRecentlyAttempted(string drive, string type, DateTime now)
    {
        var key = $"{drive}|{type}";
        return !_lastScheduledAttempt.TryGetValue(key, out var last) ||
               (now - last).TotalMinutes >= 30;
    }

    private void RecordAttempt(string drive, string type, DateTime now) =>
        _lastScheduledAttempt[$"{drive}|{type}"] = now;

    private void RaiseOnUi<T>(EventHandler<T>? handler, T args) where T : EventArgs
    {
        if (handler == null) return;
        _uiCtx.Post(_ => handler.Invoke(this, args), null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        _lock.Dispose();
    }
}
