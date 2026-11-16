using System.IO;

namespace LCapture.Services;

/// <summary>
/// Small diagnostic log at %LOCALAPPDATA%\LCapture\lcapture.log (kept under ~1 MB). Only app
/// events and errors: never pictures, sound or window contents.
/// </summary>
public static class Log
{
    private static readonly object s_lock = new();
    private static readonly string s_path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LCapture", "lcapture.log");

    public static string FilePath => s_path;

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERRO", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (s_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(s_path)!);
                var info = new FileInfo(s_path);
                if (info.Exists && info.Length > 1_000_000)
                    File.Move(s_path, s_path + ".old", overwrite: true);
                File.AppendAllText(s_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break the app.
        }
    }
}
