using System;
using System.IO;

namespace VibeOCR.Bootstrapper;

/// <summary>
/// Best-effort launch log for a GUI-subsystem entry point. Console.Error is
/// invisible when the bootstrapper is started by double-click, so every
/// startup failure is mirrored to a file. The default directory is derived
/// from the bootstrapper's own executable (the stable portable root) and
/// stays reachable even when the layout descriptor itself cannot be read; it
/// never falls back to AppData.
/// </summary>
internal static class BootstrapperLog
{
    private static readonly object Gate = new();
    private static string? _logPath;

    public static string DefaultLogDirectory() => Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "state",
        "logs");

    public static void Initialize(string? logDirectory = null)
    {
        try
        {
            string directory = logDirectory ?? DefaultLogDirectory();
            Directory.CreateDirectory(directory);
            _logPath = Path.Combine(directory, $"bootstrapper-{DateTime.Now:yyyyMMdd}.log");
        }
        catch
        {
            // Logging must never block or crash the launch.
            _logPath = null;
        }
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        string? path = _logPath;
        if (path is null)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                File.AppendAllText(
                    path,
                    $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Best-effort: diagnostics must never take the launcher down.
        }
    }
}
