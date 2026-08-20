using System.Text.Json;
using TokenConsumptionMonitoring.Models;

namespace TokenConsumptionMonitoring.Services;

public sealed class SettingsStore
{
    /// <summary>当前数据目录（%APPDATA%\TokenConsumptionMonitoring，迁移成功后写入）。</summary>
    public static readonly string DataDirectory = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Legacy.CurrentDataDirectoryName);

    /// <summary>旧数据目录（%APPDATA%\TokenUsageMonitorV3，先读后迁移；兼容标识见 <see cref="Legacy"/>）。</summary>
    public static readonly string LegacyDataDirectory = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Legacy.DataDirectoryName);

    private static readonly string FilePath = System.IO.Path.Combine(DataDirectory, "settings.json");
    private static readonly string LegacyFilePath = System.IO.Path.Combine(LegacyDataDirectory, "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

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

            // 旧目录兼容读取（先读后迁移）
            try
            {
                if (File.Exists(LegacyFilePath))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(LegacyFilePath)) ?? new AppSettings();
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
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
    }

    /// <summary>立即持久化当前活动页（页面切换成功后调用）。</summary>
    public void SaveActivePage(string? activePageId)
    {
        var settings = Load();
        if (settings.ActivePageId == activePageId) return;
        settings.ActivePageId = activePageId;
        Save(settings);
    }
}
