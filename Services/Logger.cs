namespace TokenConsumptionMonitoring.Services;

/// <summary>极简文件日志（%APPDATA%\TokenConsumptionMonitoring\app.log）。</summary>
public static class Logger
{
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppIdentity.DataDirectoryName, "app.log");

    private static readonly object Lock = new();

    public static void Log(string message)
    {
        try
        {
            lock (Lock)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                System.IO.File.AppendAllText(Path, $"{DateTimeOffset.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch { }
    }

    public static void LogException(string context, Exception ex)
    {
        Log($"[EX] {context}: {ex.GetType().Name}: {ex.Message}");
        Log($"[EX] {context}: {ex.StackTrace}");
    }
}
