using System.IO;
using System.Threading;

namespace TokenConsumptionMonitoring.Services;

/// <summary>
/// 应用临时目录维护：负责清理历史遗留的 zcode 临时副本。
///
/// 背景：旧版 ZCodeUsageService 每次统计会把 zcode 主库 + WAL 复制到
/// %TEMP%\TokenConsumptionMonitoring\zcode_<guid>.sqlite(-wal/-shm)，删除失败时静默忽略，
/// 导致残留累积。线上化改造后该复制路径已整体移除（不再读本地 zcode），
/// 这里仍保留一次性清扫，把历史上已产生的残留清掉。
/// 删除失败不再静默：记录日志，并安排后续重试（自愈）。
/// </summary>
public sealed class TempFileJanitor
{
    private static readonly string TempRoot = Path.Combine(
        Path.GetTempPath(), AppIdentity.DataDirectoryName);

    private readonly int _maxAttempts;

    public TempFileJanitor(int maxAttempts = 3)
        => _maxAttempts = Math.Max(1, maxAttempts);

    /// <summary>清扫历史 zcode 临时副本；不触碰其它应用文件。</summary>
    public void CleanupLegacyZcodeCopies()
    {
        try
        {
            if (!Directory.Exists(TempRoot)) return;
            // 旧版命名：zcode_<guid>.sqlite / zcode_schema_<guid>.sqlite（含 -wal/-shm 伴生文件）
            foreach (var pattern in new[] { "zcode_*.sqlite", "zcode_*.sqlite-wal", "zcode_*.sqlite-shm", "zcode_schema_*.sqlite", "zcode_schema_*.sqlite-wal", "zcode_schema_*.sqlite-shm" })
            {
                foreach (var file in Directory.EnumerateFiles(TempRoot, pattern, SearchOption.TopDirectoryOnly))
                {
                    TryDeleteWithLog(file);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogException("temp janitor scan", ex);
        }
    }

    /// <summary>删除失败记录日志；短重试若干次。所有 zcode 临时副本都是可再生缓存，删除失败不阻断功能。</summary>
    private void TryDeleteWithLog(string path)
    {
        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            try
            {
                File.Delete(path);
                Logger.Log($"temp janitor: deleted leftover {Path.GetFileName(path)}");
                return;
            }
            catch (Exception ex) when (attempt < _maxAttempts)
            {
                Logger.Log($"temp janitor: delete failed (attempt {attempt}/{_maxAttempts}) for {Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}");
                Thread.Sleep(200 * attempt);
            }
            catch (Exception ex)
            {
                Logger.Log($"temp janitor: delete FAILED (gave up after {_maxAttempts} attempts) for {Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
