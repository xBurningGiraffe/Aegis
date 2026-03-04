using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Win32;
using Aegis.Models;

namespace Aegis.Services;

/// <summary>
/// Runs full (wbAdmin + VHDX) and differential (file-level ZIP) backups.
/// All methods are async and safe to run on a thread-pool thread.
/// </summary>
public static class BackupEngine
{
    // =========================================================================
    // Full backup
    // =========================================================================

    public static async Task RunFullBackupAsync(
        BackupJob job, AppSettings settings,
        IProgress<string> progress, CancellationToken ct)
    {
        var driveLetter = NormaliseDrive(job.SourceDrive);
        var vhdPath     = VhdPath(settings, driveLetter);
        var vhdDir      = Path.GetDirectoryName(vhdPath)!;

        string? mountedLetter = null;

        try
        {
            // ── Diagnostic entry log (always first) ───────────────────────────
            SettingsService.AppendLog(job.SourceDrive, "FULL", "INFO",
                $"RunFullBackupAsync started. vhdx={vhdPath}  dest={settings.BackupRootPath}");

            Directory.CreateDirectory(vhdDir);
            SettingsService.AppendLog(job.SourceDrive, "FULL", "INFO",
                $"Output directory ensured: {vhdDir}");

            job.LastFullStatus = "Running";

            // Ensure any previously-mounted copy of this VHDX is dismounted first
            await TryDismountVhdxAsync(vhdPath, ct);
            SettingsService.AppendLog(job.SourceDrive, "FULL", "INFO",
                "Pre-flight dismount complete.");

            if (!File.Exists(vhdPath))
            {
                SettingsService.AppendLog(job.SourceDrive, "FULL", "INFO",
                    $"VHDX not found — creating new container ({settings.VhdMaxGb} GB max).");
                progress.Report($"Creating VHDX container (max {settings.VhdMaxGb} GB) — first run only...");
                await CreateVhdxAsync(vhdPath, settings.VhdMaxGb, driveLetter, ct);
                progress.Report("VHDX created.");
                SettingsService.AppendLog(job.SourceDrive, "FULL", "INFO", "VHDX container created.");
            }
            else
            {
                SettingsService.AppendLog(job.SourceDrive, "FULL", "INFO",
                    $"Existing VHDX found: {vhdPath}");
            }

            // Suppress Windows AutoPlay before mounting so no "What do you want to do with D:?"
            // notification fires — which could trigger an NTFS permission dialog at an unattended hour.
            SuppressAutoPlay();

            progress.Report("Mounting VHDX...");
            SettingsService.AppendLog(job.SourceDrive, "FULL", "INFO", "Mounting VHDX...");
            mountedLetter = await MountVhdxAsync(vhdPath, driveLetter, ct);
            progress.Report($"VHDX mounted at {mountedLetter}:\\");
            SettingsService.AppendLog(job.SourceDrive, "FULL", "INFO",
                $"VHDX mounted at {mountedLetter}:\\");

            progress.Report("Starting wbAdmin (30–90 min for first run, ~10 min for subsequent)...");
            SettingsService.AppendLog(job.SourceDrive, "FULL", "INFO",
                $"Launching wbAdmin: source={job.SourceDrive}  target={mountedLetter}:");
            await RunWbAdminAsync(job.SourceDrive, mountedLetter, progress, ct);

            // Success
            job.LastFullBackupUtc = DateTime.UtcNow;
            job.LastFullStatus    = "OK";
            SettingsService.AppendLog(job.SourceDrive, "FULL", "OK", "Backup completed successfully.");

            // Purge superseded differential archives
            var diffDir = DiffDir(settings, driveLetter);
            if (Directory.Exists(diffDir))
            {
                var old = Directory.GetFiles(diffDir, "diff_*.zip");
                foreach (var f in old) File.Delete(f);
                if (old.Length > 0)
                    progress.Report($"Removed {old.Length} superseded differential archive(s).");
            }

            // Reset the diff baseline so next diff captures from this moment
            job.LastDiffBackupUtc = null;
        }
        catch (Exception ex)
        {
            job.LastFullStatus = $"Error: {ex.Message}";
            SettingsService.AppendLog(job.SourceDrive, "FULL", "ERROR", ex.Message);
            throw;
        }
        finally
        {
            if (mountedLetter != null)
            {
                try
                {
                    await DismountVhdxAsync(vhdPath, CancellationToken.None);
                    progress.Report("VHDX dismounted.");
                }
                catch (Exception ex)
                {
                    progress.Report($"Warning — could not dismount VHDX: {ex.Message}");
                }
            }

            // Always restore AutoPlay regardless of success/failure.
            RestoreAutoPlay();
        }
    }

    // =========================================================================
    // Differential backup
    // =========================================================================

    public static async Task RunDifferentialBackupAsync(
        BackupJob job, AppSettings settings,
        IProgress<string> progress, CancellationToken ct)
    {
        if (job.LastFullBackupUtc is null)
            throw new InvalidOperationException(
                $"No full backup found for {job.SourceDrive}. Run a full backup first.");

        var driveLetter = NormaliseDrive(job.SourceDrive);
        var since       = job.LastFullBackupUtc.Value.ToLocalTime();
        var dateStamp   = DateTime.Now.ToString("yyyy-MM-dd");
        var stagingDir  = Path.Combine(Path.GetTempPath(), $"Aegis_{driveLetter}_{dateStamp}");
        var diffDir     = DiffDir(settings, driveLetter);
        var archivePath = Path.Combine(diffDir, $"diff_{dateStamp}.zip");

        try
        {
            // ── Diagnostic entry log (always first) ───────────────────────────
            SettingsService.AppendLog(job.SourceDrive, "DIFF", "INFO",
                $"RunDifferentialBackupAsync started. baseline={since:yyyy-MM-dd HH:mm:ss}  dest={diffDir}");

            job.LastDiffStatus = "Running";
            Directory.CreateDirectory(diffDir);
            SettingsService.AppendLog(job.SourceDrive, "DIFF", "INFO",
                $"Output directory ensured: {diffDir}");

            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, true);
            Directory.CreateDirectory(stagingDir);

            progress.Report($"Scanning files changed since {since:yyyy-MM-dd HH:mm:ss}...");

            int copied = 0, skippedLocked = 0;

            foreach (var rawPath in job.UserDataPaths)
            {
                ct.ThrowIfCancellationRequested();

                var sourcePath = Environment.ExpandEnvironmentVariables(rawPath);
                if (!Directory.Exists(sourcePath))
                {
                    progress.Report($"  Not found, skipping: {sourcePath}");
                    continue;
                }

                progress.Report($"  Scanning: {sourcePath}");

                // EnumerationOptions instead of SearchOption.AllDirectories:
                //  • IgnoreInaccessible — silently skips directories we can't read, rather
                //    than throwing UnauthorizedAccessException from the enumerator's MoveNext()
                //    (which would escape the per-file catch block and fail the whole backup).
                //  • AttributesToSkip includes ReparsePoint — skips Windows shell junction
                //    points (e.g. "My Music", "My Pictures", "My Videos" inside Documents)
                //    that are legacy symlinks to the real folders; following them causes
                //    access-denied errors and could introduce duplicate files.
                var enumOpts = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible    = true,
                    AttributesToSkip      = FileAttributes.System | FileAttributes.ReparsePoint
                };

                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(sourcePath, "*", enumOpts); }
                catch (Exception ex)
                {
                    progress.Report($"  Cannot enumerate {sourcePath}: {ex.Message}");
                    continue;
                }

                foreach (var filePath in files)
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        // Exclusion check
                        if (job.Exclusions.Any(ex =>
                            filePath.Contains(ex, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        var fi = new FileInfo(filePath);
                        if (fi.LastWriteTime <= since) continue;

                        // Mirror directory structure under staging root
                        // Strip drive letter: "C:\Users\..." → "Users\..."
                        var relative = filePath.Length > 3
                            ? filePath[3..]   // skip "C:\"
                            : filePath;
                        var destFile = Path.Combine(stagingDir, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);

                        await using var src = new FileStream(
                            filePath, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete, 65536, useAsync: true);
                        await using var dst = new FileStream(
                            destFile, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);

                        await src.CopyToAsync(dst, ct);
                        copied++;
                    }
                    catch (IOException)
                    {
                        skippedLocked++;  // Locked exclusively — skip silently
                    }
                    catch (UnauthorizedAccessException)
                    {
                        skippedLocked++;
                    }
                    catch (Exception ex)
                    {
                        progress.Report($"  Warning — {Path.GetFileName(filePath)}: {ex.Message}");
                    }
                }
            }

            progress.Report($"Staged {copied} file(s)  ({skippedLocked} locked/skipped).");

            if (copied == 0)
            {
                progress.Report("No changed files — no archive created.");
                job.LastDiffStatus    = "OK (no changes)";
                job.LastDiffBackupUtc = DateTime.UtcNow;
                return;
            }

            // Compress
            if (File.Exists(archivePath)) File.Delete(archivePath);
            progress.Report($"Compressing to {Path.GetFileName(archivePath)}...");

            await Task.Run(() =>
                ZipFile.CreateFromDirectory(stagingDir, archivePath, CompressionLevel.Optimal, false), ct);

            var sizeMb = Math.Round(new FileInfo(archivePath).Length / 1_048_576.0, 2);
            progress.Report($"Archive created: {sizeMb} MB");

            // Rotate old archives
            var archives = Directory.GetFiles(diffDir, "diff_*.zip")
                .OrderByDescending(f => f).ToArray();

            if (archives.Length > settings.RetainDifferentials)
            {
                foreach (var old in archives.Skip(settings.RetainDifferentials))
                {
                    File.Delete(old);
                    progress.Report($"Rotated: {Path.GetFileName(old)}");
                }
            }

            job.LastDiffStatus    = $"OK — {copied} files, {sizeMb} MB";
            job.LastDiffBackupUtc = DateTime.UtcNow;
            SettingsService.AppendLog(job.SourceDrive, "DIFF", "OK", $"{copied} files, {sizeMb} MB");
        }
        catch (Exception ex)
        {
            job.LastDiffStatus = $"Error: {ex.Message}";
            SettingsService.AppendLog(job.SourceDrive, "DIFF", "ERROR", ex.Message);
            throw;
        }
        finally
        {
            try { Directory.Delete(stagingDir, recursive: true); } catch { }
        }
    }

    // =========================================================================
    // VHDX helpers
    // =========================================================================

    private static async Task CreateVhdxAsync(string vhdPath, int maxGb, string driveLetter, CancellationToken ct)
    {
        var label = $"Aegis-{driveLetter}";

        // Step 1 — Create, partition, format and assign a drive letter. Do NOT detach yet
        //          so we can set NTFS permissions before releasing the volume.
        var scriptCreate = $"""
            create vdisk file="{vhdPath}" maximum={maxGb * 1024} type=expandable
            select vdisk file="{vhdPath}"
            attach vdisk
            create partition primary
            format fs=ntfs label="{label}" quick
            assign
            exit
            """;
        await RunDiskpartAsync(scriptCreate, ct);

        // Step 2 — Wait for Windows to register the new volume, then grant BUILTIN\Administrators
        //          full control (S-1-5-32-544 is locale-independent). This prevents the
        //          "You don't have permission to access this folder → Click Continue" dialog
        //          that Explorer shows when the volume is opened on subsequent mounts.
        string? newLetter = null;
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(500, ct);
            var found = DriveInfo.GetDrives().FirstOrDefault(d =>
                d.IsReady && d.VolumeLabel.Equals(label, StringComparison.OrdinalIgnoreCase));
            if (found != null) { newLetter = found.Name[..1]; break; }
        }

        if (newLetter != null)
        {
            // *S-1-5-32-544 = BUILTIN\Administrators (works on all Windows locales).
            await RunProcessAsync("icacls",
                $"{newLetter}: /grant *S-1-5-32-544:(OI)(CI)F /T /C /Q", null, ct);
        }

        // Step 3 — Detach.
        var scriptDetach = $"""
            select vdisk file="{vhdPath}"
            detach vdisk
            exit
            """;
        await RunDiskpartAsync(scriptDetach, ct);
    }

    private static async Task<string> MountVhdxAsync(string vhdPath, string driveLetter, CancellationToken ct)
    {
        var script = $"""
            select vdisk file="{vhdPath}"
            attach vdisk
            exit
            """;
        await RunDiskpartAsync(script, ct);

        // Wait for Windows to finish assigning the drive letter (retry up to 10 s).
        // Also accept the legacy "PCBackup-{letter}" label so that VHDX containers
        // created before the rename to Aegis continue to work without recreation.
        var label       = $"Aegis-{driveLetter}";
        var legacyLabel = $"PCBackup-{driveLetter}";
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(500, ct);
            var found = DriveInfo.GetDrives()
                .FirstOrDefault(d => d.IsReady &&
                    (d.VolumeLabel.Equals(label,       StringComparison.OrdinalIgnoreCase) ||
                     d.VolumeLabel.Equals(legacyLabel, StringComparison.OrdinalIgnoreCase)));
            if (found != null)
                return found.Name[..1];  // Just the letter, e.g. "D"
        }

        throw new InvalidOperationException(
            $"VHDX was mounted but the volume '{label}' was not assigned a drive letter within 10 seconds.");
    }

    private static async Task DismountVhdxAsync(string vhdPath, CancellationToken ct)
    {
        var script = $"""
            select vdisk file="{vhdPath}"
            detach vdisk
            exit
            """;
        await RunDiskpartAsync(script, ct);
    }

    private static async Task TryDismountVhdxAsync(string vhdPath, CancellationToken ct)
    {
        try { await DismountVhdxAsync(vhdPath, ct); } catch { }
    }

    private static async Task RunDiskpartAsync(string script, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"pcbk_{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(tmp, script, ct);
        try
        {
            int exit = await RunProcessAsync("diskpart", $"/s \"{tmp}\"", null, ct);
            if (exit != 0)
                throw new InvalidOperationException($"diskpart exited with code {exit}.");
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    private static async Task RunWbAdminAsync(
        string sourceDrive, string targetLetter,
        IProgress<string> progress, CancellationToken ct)
    {
        var args = $"start backup -backupTarget:{targetLetter}: " +
                   $"-include:{sourceDrive} -allCritical -vssCopy -quiet";

        int exit = await RunProcessAsync("wbadmin", args, progress, ct);
        if (exit != 0)
            throw new InvalidOperationException($"wbAdmin exited with code {exit}.");
    }

    private static async Task<int> RunProcessAsync(
        string exe, string args, IProgress<string>? progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        proc.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                progress?.Report(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                progress?.Report($"[stderr] {e.Data}");
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }

    // =========================================================================
    // Path helpers
    // =========================================================================

    private static string NormaliseDrive(string drive) =>
        drive.TrimEnd('\\', ':').ToUpperInvariant();

    private static string VhdPath(AppSettings s, string letter) =>
        Path.Combine(s.BackupRootPath, letter, "Full", "SystemImage.vhdx");

    private static string DiffDir(AppSettings s, string letter) =>
        Path.Combine(s.BackupRootPath, letter, "Differential");

    // =========================================================================
    // AutoPlay suppression
    // =========================================================================
    // When diskpart mounts a VHDX, Windows fires an AutoPlay notification for
    // the new drive letter, which can pop up an Explorer window. If the user
    // clicks into it, Windows shows a "You don't have permission — Click Continue"
    // dialog.  At an unattended hour that dialog would stall the backup indefinitely.
    // We temporarily disable AutoPlay for all drive types while the VHDX is mounted.

    private const string AutoPlayKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";

    private static void SuppressAutoPlay()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AutoPlayKeyPath, writable: true);
            key?.SetValue("NoDriveTypeAutoRun", 0xFF, RegistryValueKind.DWord);
        }
        catch { /* best-effort */ }
    }

    private static void RestoreAutoPlay()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoPlayKeyPath, writable: true);
            key?.DeleteValue("NoDriveTypeAutoRun", throwOnMissingValue: false);
        }
        catch { /* best-effort */ }
    }
}
