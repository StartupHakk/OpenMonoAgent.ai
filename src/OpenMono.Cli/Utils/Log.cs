namespace OpenMono.Utils;

public static class Log
{
    private static string? _logPath;
    private static readonly object _lock = new();

    public static string? LogPath => _logPath;

    public static void Initialize(string dataDirectory)
    {
        var logDir = Path.Combine(dataDirectory, "logs");
        Directory.CreateDirectory(logDir);
        _logPath = Path.Combine(logDir, $"openmono-{DateTime.UtcNow:yyyy-MM-dd}.log");
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
    {
        Write("ERROR", message);
        if (ex is not null)
            Write("ERROR", ex.ToString());
    }

    public static void Debug(string message) => Write("DEBUG", message);

    private static void Write(string level, string message)
    {
        if (_logPath is null) return;

        var line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";

        lock (_lock)
        {
            try
            {
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
            catch
            {

            }
        }
    }
}
