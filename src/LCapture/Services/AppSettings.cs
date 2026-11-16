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

    /// <summary>Output height of recordings; 0 = same as the source.</summary>
    public int RecordingHeight { get; set; }
    public bool RecordingFps30 { get; set; }
    public Models.RecordingQuality RecordingQuality { get; set; } = Models.RecordingQuality.High;
    public string? RecordingFolder { get; set; }

    /// <summary>Webcam and picture layers, bottom to top (windows are not kept: they change).</summary>
    public List<Models.SceneLayer> Layers { get; set; } = [];
    public bool LayersLocked { get; set; }

    public static string PicturesFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LCapture", "Imagens");
    public bool RecordWindowAudio { get; set; } = true;
    public float WindowAudioVolume { get; set; } = 1f;
    public bool WindowAudioMuted { get; set; }
    public Models.CaptureMethod CaptureMethod { get; set; } = Models.CaptureMethod.Automatic;

    public string? MicDeviceId { get; set; }
    public float MicVolume { get; set; } = 1f;
    public bool MicMuted { get; set; }
    public bool MicRecord { get; set; } = true;
    public bool AudioPanelOpen { get; set; } = true;

    public static string DefaultRecordingFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "LCapture");

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
