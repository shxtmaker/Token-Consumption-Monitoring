using System.Text.Json;
using TokenUsageMonitorV3.Models;

namespace TokenUsageMonitorV3.Services;

/// <summary>页面存储（pages.json，不含 key——密钥在凭据管理器）。</summary>
public sealed class PageStore
{
    private static readonly string FilePath = System.IO.Path.Combine(SettingsStore.DataDirectory, "pages.json");
    private readonly object _lock = new();

    public List<Page> Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<List<Page>>(File.ReadAllText(FilePath)) ?? new List<Page>();
            }
            catch (Exception ex)
            {
                Logger.LogException("load pages", ex);
            }
            return new List<Page>();
        }
    }

    public void Save(List<Page> pages)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(SettingsStore.DataDirectory);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(pages, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Logger.LogException("save pages", ex);
            }
        }
    }
}
