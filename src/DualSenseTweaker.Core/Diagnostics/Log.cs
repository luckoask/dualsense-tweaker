using System.Diagnostics;

namespace DualSenseTweaker.Core.Diagnostics;

/// <summary>Minimal thread-safe file logger; mainly for diagnosing controller communication.</summary>
public static class Log
{
    private const long MaxFileSize = 1_000_000;
    private static readonly Lock Gate = new();
    private static string? _path;

    public static string? FilePath => _path;

    /// <summary>Starts logging into &lt;directory&gt;/dualsense.log; a log over 1 MB is kept as dualsense.1.log.</summary>
    public static void Initialize(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "dualsense.log");
            if (File.Exists(path) && new FileInfo(path).Length > MaxFileSize)
                File.Move(path, Path.Combine(directory, "dualsense.1.log"), overwrite: true);
            _path = path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _path = null;
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [T{Environment.CurrentManagedThreadId}] {message}";
        Debug.WriteLine(line);
        if (_path is null)
            return;
        lock (Gate)
        {
            try
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the app down.
            }
        }
    }
}
