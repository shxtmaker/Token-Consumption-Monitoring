using System.Text.Json;
using System.Text.Json.Serialization;
using TokenConsumptionMonitoring.Models;

namespace TokenConsumptionMonitoring.Services;

public enum PageConfigurationLoadState
{
    Ready,
    Migrated,
    RecoveryRequired,
}

/// <summary>只有可写配置才会获得写入许可。</summary>
public sealed record PageConfigurationWriteLease(Guid Id);

public sealed record PageConfigurationLoadResult(
    PageConfigDocument Document,
    PageConfigurationLoadState State,
    PageConfigurationWriteLease? WriteLease,
    string? Diagnostic)
{
    public bool RequiresSchemaRewrite => State == PageConfigurationLoadState.Migrated;
    public bool IsRecoveryRequired => State == PageConfigurationLoadState.RecoveryRequired;
}

public sealed record PageConfigurationSaveResult(
    bool Succeeded,
    string? Diagnostic = null,
    bool RecoveryRequired = false);

/// <summary>
/// pages.json 存储：当前 schema 可写，损坏/未来版本/完整性失败进入只读恢复态。
/// 不读取其他产品名称、其他数据目录或其他凭据 target。
/// </summary>
public sealed class PageConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _directoryPath;
    private readonly string _filePath;
    private readonly string _backupPath;
    private readonly object _lock = new();
    private PageConfigurationWriteLease? _writeLease;
    private bool _recoveryRequired;

    public PageConfigStore(string? baseDirectory = null)
    {
        _directoryPath = baseDirectory ?? SettingsStore.DataDirectory;
        _filePath = System.IO.Path.Combine(_directoryPath, "pages.json");
        _backupPath = _filePath + ".bak";
    }

    public PageConfigurationLoadResult Load()
    {
        lock (_lock)
        {
            _writeLease = null;
            _recoveryRequired = false;

            if (!File.Exists(_filePath))
            {
                _writeLease = NewLease();
                return new PageConfigurationLoadResult(
                    new PageConfigDocument(), PageConfigurationLoadState.Ready, _writeLease, null);
            }

            var text = ReadTextSafe(_filePath);
            if (text is null)
                return Recovery("pages.json 无法读取");

            var document = PageConfigParser.Parse(text);
            if (document.IsCorrupt)
                return Recovery(document.Diagnostic ?? "pages.json 需要恢复");

            _writeLease = NewLease();
            var state = document.RequiresSchemaRewrite
                ? PageConfigurationLoadState.Migrated
                : PageConfigurationLoadState.Ready;
            return new PageConfigurationLoadResult(document, state, _writeLease, null);
        }
    }

    /// <summary>使用最近一次正常 Load 发放的写入许可保存配置。</summary>
    public PageConfigurationSaveResult Save(PageConfigDocument document)
    {
        lock (_lock)
        {
            if (_writeLease is null)
                return new PageConfigurationSaveResult(false, "页面配置尚未获得写入许可", _recoveryRequired);
            return Save(document, _writeLease);
        }
    }

    /// <summary>原子写入；恢复态没有许可，因此普通保存不会触碰原文件。</summary>
    public PageConfigurationSaveResult Save(PageConfigDocument document, PageConfigurationWriteLease lease)
    {
        lock (_lock)
        {
            if (_recoveryRequired || _writeLease is null || lease.Id != _writeLease.Id)
                return new PageConfigurationSaveResult(false, "页面配置处于只读恢复态，未写入原文件", _recoveryRequired);
            if (document.IsCorrupt)
                return new PageConfigurationSaveResult(false, document.Diagnostic ?? "页面配置无效", true);
            if (document.SchemaVersion != PageConfigDocument.CurrentSchemaVersion)
                return new PageConfigurationSaveResult(false, "页面配置 schemaVersion 不受支持", true);
            if (!PageConfigParser.ValidateForSave(document))
                return new PageConfigurationSaveResult(false, document.Diagnostic ?? "页面配置字段无效", true);

            var tmp = _filePath + ".tmp";
            try
            {
                Directory.CreateDirectory(_directoryPath);
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(fs, document, Options);
                    fs.Flush(flushToDisk: true);
                }
                if (File.Exists(_filePath)) File.Copy(_filePath, _backupPath, overwrite: true);
                File.Move(tmp, _filePath, overwrite: true);
                document.RequiresSchemaRewrite = false;
                return new PageConfigurationSaveResult(true);
            }
            catch (Exception ex)
            {
                Logger.LogException("save pages", ex);
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return new PageConfigurationSaveResult(false, "页面配置保存失败", false);
            }
        }
    }

    private PageConfigurationLoadResult Recovery(string diagnostic)
    {
        _recoveryRequired = true;
        _writeLease = null;
        return new PageConfigurationLoadResult(
            new PageConfigDocument { Diagnostic = diagnostic },
            PageConfigurationLoadState.RecoveryRequired,
            null,
            diagnostic);
    }

    private static PageConfigurationWriteLease NewLease() => new(Guid.NewGuid());

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
