namespace Aegis.Models;

public class AppSettings
{
    /// <summary>Drive letter of the USB backup destination (e.g. "E:").</summary>
    public string BackupDestDrive { get; set; } = "E:";

    /// <summary>Root folder on the backup drive for all backup output.</summary>
    public string BackupRootPath { get; set; } = @"E:\Aegis";

    /// <summary>Maximum size (GB) each per-drive VHDX container may grow to.</summary>
    public int VhdMaxGb { get; set; } = 120;

    /// <summary>Number of daily differential archives to keep per drive before rotating.</summary>
    public int RetainDifferentials { get; set; } = 7;

    /// <summary>Whether to register a Task Scheduler logon task for auto-start.</summary>
    public bool StartWithWindows { get; set; } = false;

    /// <summary>Whether to show system-tray balloon tips on backup start/completion.</summary>
    public bool ShowNotifications { get; set; } = true;

    /// <summary>One entry per source drive to back up.</summary>
    public List<BackupJob> Jobs { get; set; } = new();
}
