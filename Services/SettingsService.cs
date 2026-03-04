using System.Text.Json;
using System.Text.Json.Serialization;
using Aegis.Models;

namespace Aegis.Services;

public static class SettingsService
{
    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Aegis");

    public static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");
    public static readonly string LogPath    = Path.Combine(ConfigDir, "backup.log");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters    = { new JsonStringEnumConverter() },
    };

    // -------------------------------------------------------------------------
    // Settings load / save
    // -------------------------------------------------------------------------

    public static AppSettings Load()
    {
        Directory.CreateDirectory(ConfigDir);

        if (!File.Exists(ConfigPath))
        {
            var defaults = new AppSettings();
            Save(defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(settings, JsonOpts));
    }

    // -------------------------------------------------------------------------
    // Logging
    // -------------------------------------------------------------------------

    public static void AppendLog(string drive, string type, string level, string message)
    {
        Directory.CreateDirectory(ConfigDir);

        // Rotate when > 10 MB
        if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 10L * 1024 * 1024)
        {
            var old = Path.ChangeExtension(LogPath, ".old.log");
            if (File.Exists(old)) File.Delete(old);
            File.Move(LogPath, old);
        }

        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{type}][{drive}][{level}] {message}";
        File.AppendAllText(LogPath, line + Environment.NewLine);
    }

    public static string[] GetRecentLogLines(int count = 30)
    {
        if (!File.Exists(LogPath)) return Array.Empty<string>();
        try
        {
            var lines = File.ReadAllLines(LogPath);
            return lines.Length <= count ? lines : lines[^count..];
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    // -------------------------------------------------------------------------
    // Deep copy helper (used by SettingsForm for working copies)
    // -------------------------------------------------------------------------
    public static AppSettings DeepCopy(AppSettings source)
    {
        var json = JsonSerializer.Serialize(source, JsonOpts);
        return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts)!;
    }
}
