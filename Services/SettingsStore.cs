using System.Text.Json;
using TokenConsumptionMonitoring.Models;

namespace TokenConsumptionMonitoring.Services;

/// <summary>应用设置存储。所有设置与页面共享同一个正式数据目录。</summary>
public sealed class SettingsStore
{
    public static readonly string DataDirectory = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppIdentity.DataDirectoryName);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _directoryPath;
    private readonly string _filePath;
    private readonly object _lock = new();
    private AppSettings? _current;

    public SettingsStore(string? baseDirectory = null)
    {
        _directoryPath = baseDirectory ?? DataDirectory;
        _filePath = System.IO.Path.Combine(_directoryPath, "settings.json");
    }

    public AppSettings Load()
    {
        lock (_lock)
        {
            if (_current is not null) return _current;
            try
            {
                if (File.Exists(_filePath))
                    _current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_filePath));
            }
            catch (Exception ex)
            {
                Logger.LogException("read settings", ex);
            }
            return _current ??= new AppSettings();
        }
    }

    /// <summary>保存同一个设置对象，避免其他调用方持有的旧对象覆盖 ActivePageId。</summary>
    public void Save(AppSettings settings)
    {
        lock (_lock)
        {
            _current = settings;
            WriteUnsafe(settings);
        }
    }

    /// <summary>在存储层原子更新设置，并把更新应用到当前内存对象。</summary>
    public void Update(Action<AppSettings> update)
    {
        lock (_lock)
        {
            var settings = Load();
            update(settings);
            WriteUnsafe(settings);
        }
    }

    /// <summary>立即持久化当前活动页，调用方的内存设置对象也同步更新。</summary>
    public void SaveActivePage(string? activePageId)
        => Update(settings => settings.ActivePageId = activePageId);

    private void WriteUnsafe(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(_directoryPath);
            var tmp = _filePath + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(fs, settings, Options);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.LogException("save settings", ex);
        }
    }
}
