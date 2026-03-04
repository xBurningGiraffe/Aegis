# Aegis — Restore Guide

## What you have

| File                                        | Created by                | Contains                                            |
| ------------------------------------------- | ------------------------- | --------------------------------------------------- |
| `E:\Aegis\Full\SystemImage.vhdx`            | `Backup-Full.ps1`         | Complete bootable system image (OS, apps, settings) |
| `E:\Aegis\Differential\diff_YYYY-MM-DD.zip` | `Backup-Differential.ps1` | All user files changed since Sunday's full backup   |
| `E:\Aegis\Logs\backup_log.txt`              | Both scripts              | Timestamped log of every backup run                 |

To fully recover after a catastrophic failure (corrupt OS, failed update, dead drive) you need:

1. The VHDX — to restore Windows and all your apps
2. The most recent ZIP — to restore files you changed after Sunday

---

## Scenario A — Restore the full system image (OS corruption, dead drive)

### What you need

- The USB backup drive
- A **Windows 11 installation USB** (create one free from microsoft.com/software-download/windows11)
  - You only need to create this once; store it with your backup drive

### Steps

1. Plug in the USB backup drive.
2. Boot from the Windows 11 installation USB.
   - Restart the PC, press the boot menu key (usually F8, F11, or F12 depending on your motherboard), and select the USB.
3. On the "Install now" screen, click **"Repair your computer"** (bottom-left).
4. Choose **Troubleshoot → System Image Recovery**.
5. Windows will scan for system images. If it doesn't find the VHDX automatically:
   - Click "Select a system image" → "Advanced" → "Search for a system image on the network" is NOT needed; instead choose "Use the latest available system image on this computer" and ensure the USB drive is listed.
   - Alternatively: before booting the USB, mount the VHDX in Windows (see "Manually mount" below) so the recovery environment can find it as a regular WindowsImageBackup folder.
6. Follow the wizard to restore. The process takes 20–60 minutes.
7. Once Windows boots, copy your files back from the latest `diff_YYYY-MM-DD.zip` (see Scenario B).

### Manually mounting the VHDX so WinRE finds the image

If the recovery wizard doesn't see the VHDX automatically, do this from a working PC before you boot the recovery USB:

```
diskpart
select vdisk file="E:\Aegis\Full\SystemImage.vhdx"
attach vdisk readonly
exit
```

The VHDX will appear as a new drive letter containing the `WindowsImageBackup` folder, which WinRE can find.

---

## Scenario B — Restore specific files (accidental deletion, file corruption)

No need to boot from recovery media. Do this from within Windows.

1. Plug in the USB backup drive.
2. Open the most recent `E:\Aegis\Differential\diff_YYYY-MM-DD.zip`.
3. The archive mirrors your original folder structure.
   - Example: a file that was at `C:\Users\Alice\Documents\report.docx`
     is stored inside the ZIP at `Users\Alice\Documents\report.docx`
4. Extract just the files you need and copy them back to their original location.

If the file you need is older than a week (before the last full backup), it will be in the VHDX — mount it as described above and browse to the path.

---

## Scenario C — Restore after a bad Windows Update

This is the same as Scenario A. The weekly full backup was taken before the bad update applied, so restoring it rolls the OS back completely.

If the update ran on a Thursday and you want to keep your files from Mon–Thu, extract them from `diff_YYYY-MM-DD.zip` after restoring the image.

---

## Checking backup health (without restoring)

Run this in PowerShell (no admin needed) to see recent backup log entries:

```powershell
Get-Content E:\Aegis\Logs\backup_log.txt | Select-Object -Last 50
```

To see all available full and differential backup dates:

```powershell
# Full backup date
Get-Content E:\Aegis\Logs\last_full_backup.txt

# Differential archives
Get-ChildItem E:\Aegis\Differential\diff_*.zip | Select-Object Name, LastWriteTime, @{n='MB';e={[math]::Round($_.Length/1MB,1)}}
```

---

## Backup schedule summary

| Task                 | When               | Runtime estimate                              |
| -------------------- | ------------------ | --------------------------------------------- |
| Full system image    | Sunday ~1:00 AM    | 30–90 min (first run), 10–30 min (subsequent) |
| Differential archive | Every day ~2:30 AM | 2–15 min                                      |

Both tasks run silently in the background. They require the USB drive to be connected — if the drive is absent, the task logs an error and exits without retrying.
