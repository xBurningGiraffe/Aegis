using System.Text.Json.Serialization;

namespace Aegis.Models;

public class BackupJob
{
    // -------------------------------------------------------------------------
    // Identity
    // -------------------------------------------------------------------------
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Drive letter to back up (e.g. "C:").</summary>
    public string SourceDrive { get; set; } = "C:";

    /// <summary>Human-readable name shown in the tray menu and settings form.</summary>
    public string Label { get; set; } = "Windows Drive";

    // -------------------------------------------------------------------------
    // Full system image schedule
    // -------------------------------------------------------------------------
    public bool FullEnabled { get; set; } = true;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DayOfWeek FullDayOfWeek { get; set; } = DayOfWeek.Sunday;

    /// <summary>Time of day for the full backup (stored as ticks for JSON compatibility).</summary>
    public long FullTimeTicks { get; set; } = TimeSpan.FromHours(1).Ticks;  // 1:00 AM

    [JsonIgnore]
    public TimeSpan FullTime
    {
        get => TimeSpan.FromTicks(FullTimeTicks);
        set => FullTimeTicks = value.Ticks;
    }

    // -------------------------------------------------------------------------
    // Daily differential schedule
    // -------------------------------------------------------------------------
    public bool DiffEnabled { get; set; } = true;

    public long DiffTimeTicks { get; set; } = TimeSpan.FromHours(2).Add(TimeSpan.FromMinutes(30)).Ticks;  // 2:30 AM

    [JsonIgnore]
    public TimeSpan DiffTime
    {
        get => TimeSpan.FromTicks(DiffTimeTicks);
        set => DiffTimeTicks = value.Ticks;
    }

    // -------------------------------------------------------------------------
    // Included paths (support %ENV% tokens)
    // -------------------------------------------------------------------------
    public List<string> UserDataPaths { get; set; } = DefaultPaths();

    // -------------------------------------------------------------------------
    // Exclusion patterns (case-insensitive substring match against full path)
    // -------------------------------------------------------------------------
    public List<string> Exclusions { get; set; } = DefaultExclusions();

    // -------------------------------------------------------------------------
    // Runtime state (persisted so it survives restarts)
    // -------------------------------------------------------------------------
    public DateTime? LastFullBackupUtc { get; set; }
    public DateTime? LastDiffBackupUtc { get; set; }
    public string LastFullStatus { get; set; } = "Never run";
    public string LastDiffStatus { get; set; } = "Never run";

    // -------------------------------------------------------------------------
    // Defaults
    // -------------------------------------------------------------------------
    public static List<string> DefaultPaths() => new()
    {
        @"%USERPROFILE%\Documents",
        @"%USERPROFILE%\Desktop",
        @"%USERPROFILE%\Downloads",
        @"%USERPROFILE%\Pictures",
        @"%USERPROFILE%\Videos",
        @"%USERPROFILE%\Music",
        @"%APPDATA%",
        @"%LOCALAPPDATA%",
    };

    public static List<string> DefaultExclusions() => new()
    {
        @"\AppData\Local\Temp\",
        @"\AppData\Local\Microsoft\Windows\INetCache\",
        @"\AppData\Local\Microsoft\Windows\WebCache\",
        @"\AppData\Local\Google\Chrome\User Data\Default\Cache\",
        @"\AppData\Local\Google\Chrome\User Data\Default\Code Cache\",
        @"\AppData\Local\Microsoft\Edge\User Data\Default\Cache\",
        @"\AppData\Local\Microsoft\Edge\User Data\Default\Code Cache\",
        @"\AppData\Roaming\Spotify\Storage\",
        @"\AppData\Local\Packages\",
        @"\.git\objects\",
        @"\node_modules\",
    };
}
