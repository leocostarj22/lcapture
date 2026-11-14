using System.IO;
using System.Text.Json;

namespace LCapture.Services;

public sealed class AppSettings
{
    public string? DeviceId { get; set; }
    public uint FormatWidth { get; set; }
    public uint FormatHeight { get; set; }
    public uint FormatFpsNumerator { get; set; }
    public uint FormatFpsDenominator { get; set; }
    public string? FormatSubtype { get; set; }

    public string? AudioDeviceId { get; set; }
    public float AudioVolume { get; set; } = 1f;
    public bool AudioMuted { get; set; }

    public bool SharpenEnabled { get; set; }
    public double SharpenStrength { get; set; } = 0.6;

    private static string FilePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LCapture");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings is not null) return settings;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file: fall back to defaults.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Best-effort only; not being able to persist settings should not crash the app.
        }
    }
}
