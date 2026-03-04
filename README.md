<p align="center">
  <img src="logo.svg" alt="Aegis Logo" width="130"/>
</p>

<h1 align="center">Aegis</h1>

<p align="center">
  <em>Automated Windows backup — named for the divine shield of Athena.</em>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white" alt=".NET 10"/>
  &nbsp;
  <img src="https://img.shields.io/badge/Platform-Windows%2010%2F11-0078D4?logo=windows&logoColor=white" alt="Windows 10/11"/>
  &nbsp;
  <img src="https://img.shields.io/badge/Requires-Administrator-red" alt="Requires Administrator"/>
</p>

---

## What is Aegis?

Aegis is a lightweight Windows system-tray application that protects your data with two complementary backup strategies:

- **Full system image** — a complete Windows Image Backup (via `wbAdmin`) written into an expandable VHDX container on your external drive. Restorable from the Windows Recovery Environment without any third-party software.
- **Daily differential** — a dated ZIP archive of every user file that changed since the last full backup. Fast, small, and instantly browsable.

Everything runs silently on a schedule. No cloud. No subscription. Just your drive.

---

## Features

- 🛡️ **Full system image** via `wbAdmin` into a self-contained VHDX — full bare-metal recovery capability
- 🔄 **Daily differential archive** of Documents, Desktop, Downloads, Pictures, Videos, Music and AppData
- 🌙 **Unattended overnight operation** — set it and forget it
- 🎨 **Automatic dark / light mode** — follows your Windows theme in real time
- 🔔 **Toast notifications** on backup start and completion
- 📋 **Detailed rotating log** — every step recorded, auto-rotated at 10 MB
- ⚙️ **Single-window settings UI** — no wizards, no accounts

---

## Requirements

| Requirement | Details |
|-------------|---------|
| OS | Windows 10 or 11 (64-bit) |
| Runtime | None — self-contained executable |
| Privileges | **Must run as Administrator** (required by `wbAdmin` and `diskpart`) |
| Backup drive | NTFS or exFAT; minimum free space = VHDX max size (default 120 GB) |

---

## Installation

1. Run the Build.bat script as an administrator
2. Move the compiled Aegis.exe from the dist/ directory to wherever you want it to run from
3. Execute the Aegis.exe
4. Check the system tray for the Aegis shield, right-click and choose "Open Settings" to configure

> **Note:** For unattended overnight backups, configure a Task Scheduler entry or the Windows startup registry key to launch Aegis as administrator at log-on.

---

## How It Works

### Full Backup (Weekly)

1. Creates an expandable VHDX container on your backup drive (first run only)
2. Mounts the VHDX as a temporary drive letter
3. Runs `wbAdmin start backup -allCritical` to write a complete Windows Image Backup into the mounted volume
4. Dismounts the VHDX

The result is a standard Windows Image Backup inside a portable VHDX — recoverable via **Windows Recovery Environment → System Image Recovery** with no additional tools.

### Differential Backup (Daily)

Scans the configured user-data paths for files modified since the last full backup and compresses them into a dated archive:

```
diff_2026-03-04.zip
```

Old archives are automatically rotated; the number retained is configurable (default: 7).

---

## File Layout

```
F:\                            ← backup root (your external drive)
└── C\
    ├── Full\
    │   └── SystemImage.vhdx  ← expandable VHDX with the full system image
    └── Differential\
        ├── diff_2026-03-03.zip
        └── diff_2026-03-04.zip
```

---

## Configuration

Open Settings by double-clicking the tray icon (or right-click → **Open Settings**).

| Setting | Description |
|---------|-------------|
| **Backup root path** | Folder on your external drive where all backup output is stored |
| **VHDX max size (GB)** | Upper size limit for the system image container |
| **Retain differentials** | Number of daily ZIPs to keep before rotating |
| **Show notifications** | Enable or disable balloon tips on backup events |
| **Start with Windows** | Register Aegis in the Windows startup registry key |

Each backup **job** targets one source drive and carries its own full and differential schedules (day of week, time of day).

---

## Log File

```
%APPDATA%\Aegis\backup.log
```

The log records every milestone — VHDX creation, `wbAdmin` progress, differential scan totals, errors and completion confirmations. Accessible from the tray menu via **View Log**.

---

## Restoring from Backup

### Full system restore

1. Boot from Windows installation media or a recovery drive
2. Choose **Repair your computer → Troubleshoot → System Image Recovery**
3. Point to your VHDX at `<drive>\C\Full\SystemImage.vhdx`

### Differential restore

Extract the appropriate `diff_YYYY-MM-DD.zip` and copy files back to their original paths. The archive mirrors the original directory structure.

---

## Manually Building from Source

```bash
git clone https://github.com/xBurningGiraffe/Aegis
cd Aegis
dotnet publish Aegis.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist
```

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

---

## License

MIT © 2026
