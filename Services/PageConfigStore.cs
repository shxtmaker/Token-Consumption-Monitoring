using System.Text.Json;
using TokenConsumptionMonitoring.Models;

namespace TokenConsumptionMonitoring.Services;

/// <summary>页面配置加载结果：文档 + 是否需要持久化（legacy 迁移后）。</summary>
public sealed record PageConfigLoadResult(PageConfigDocument Document, bool NeedsMigrateWrite);

/// <summary>
/// 页面配置存储（pages.json）：文件级 envelope + schema 版本迁移。
/// - 解析/迁移逻辑见 <see cref="PageConfigParser"/>（纯逻辑可测试）。
/// - 当前目录缺失时读取旧目录并标记需要迁移写入。
/// - 写入使用临时文件 + 原子替换，并保留一份 .bak 备份。
/// </summary>
public sealed class PageConfigStore
{
    private static readonly string FilePath = System.IO.Path.Combine(SettingsStore.DataDirectory, "pages.json");
    private static readonly string LegacyFilePath = System.IO.Path.Combine(SettingsStore.LegacyDataDirectory, "pages.json");
    private static readonly string BackupPath = FilePath + ".bak";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _lock = new();

    public PageConfigLoadResult Load()
    {
        lock (_lock)
        {
            // 1) 当前目录新格式
            if (File.Exists(FilePath))
            {
                var text = ReadTextSafe(FilePath);
                if (text is not null)
                    return new PageConfigLoadResult(PageConfigParser.Parse(text), NeedsMigrateWrite: false);
                return new PageConfigLoadResult(new PageConfigDocument { Diagnostic = "pages.json 无法读取" }, false);
            }

            // 2) 旧目录兼容读取 → 标记需要迁移到新目录
            if (File.Exists(LegacyFilePath))
            {
                var text = ReadTextSafe(LegacyFilePath);
                if (text is not null)
                    return new PageConfigLoadResult(PageConfigParser.Parse(text), NeedsMigrateWrite: true);
                return new PageConfigLoadResult(new PageConfigDocument { Diagnostic = "旧 pages.json 无法读取" }, false);
            }

            return new PageConfigLoadResult(new PageConfigDocument(), NeedsMigrateWrite: false);
        }
    }

    /// <summary>原子写入：临时文件 → 刷新 → 替换，另保留一份 .bak 备份。</summary>
    public void Save(PageConfigDocument document)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(SettingsStore.DataDirectory);
                var tmp = FilePath + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(fs, document, Options);
                    fs.Flush(flushToDisk: true);
                }
                if (File.Exists(FilePath)) File.Copy(FilePath, BackupPath, overwrite: true);
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Logger.LogException("save pages", ex);
            }
        }
    }

    private static string? ReadTextSafe(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex)
        {
            Logger.LogException("read pages", ex);
            return null;
        }
    }
}
