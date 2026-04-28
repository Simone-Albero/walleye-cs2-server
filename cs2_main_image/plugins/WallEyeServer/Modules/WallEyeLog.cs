namespace WallEyeServer;

public sealed class WallEyeLog
{
    private static readonly object FileLock = new();
    private readonly string _dataPath;
    private readonly string _module;

    public WallEyeLog(string dataPath, string module)
    {
        _dataPath = dataPath;
        _module = module;
    }

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception == null ? message : $"{message}: {exception}");

    private void Write(string level, string message)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var line = $"{timestamp} [{level}] [{_module}] {message}";

        Console.WriteLine($"[WallEye] {line}");

        try
        {
            var logDir = Path.Combine(_dataPath, "logs");
            Directory.CreateDirectory(logDir);
            var path = Path.Combine(logDir, $"walleye-{DateTime.UtcNow:yyyyMMdd}.log");

            lock (FileLock)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never break gameplay.
        }
    }
}
