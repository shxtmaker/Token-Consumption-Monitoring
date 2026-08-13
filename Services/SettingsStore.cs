using System.Text.Json;
using TokenUsageMonitorV3.Models;

namespace TokenUsageMonitorV3.Services;

public sealed class SettingsStore
{
    public static readonly string DataDirectory = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TokenUsageMonitorV3");

    private static readonly string FilePath = System.IO.Path.Combine(DataDirectory, "settings.json");
    private readonly object _lock = new();

    public AppSettings Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
            catch { }
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
