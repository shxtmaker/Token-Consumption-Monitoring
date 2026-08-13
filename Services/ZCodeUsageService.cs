using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TokenUsageMonitorV3.Services;

/// <summary>
/// zcode 本地用量：读取 CLI SQLite 库（%USERPROFILE%\.zcode\cli\db\db.sqlite）model_usage 表，
/// 按供应商（provider）归组——同一模型名在不同供应商下分别统计，避免混算。
/// 口径：provider_total_tokens（缺省回退 computed/input+output），与 zcode 官方统计一致。
/// rollout jsonl 仅记录部分请求（实测约 1/6），不作为数据源。
/// </summary>
public sealed class ZCodeUsageService
{
    private static readonly string ZCodeHome = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zcode");

    public sealed record ModelTokens(string Model, long Tokens);
    public sealed record ProviderInfo(string Id, string Name, string? ApiKey, string? BaseUrl);
    public sealed record ProviderUsage(ProviderInfo Provider, List<ModelTokens> Models);

    /// <summary>今日（本地时区）用量：按供应商归组（区分不同供应商的同名模型）。</summary>
    public async Task<List<ProviderUsage>> ComputeTodayByProviderAsync(CancellationToken ct)
    {
        var providers = LoadProviders();
        var result = new List<ProviderUsage>();
        if (providers.Count == 0) return result;

        var dbPath = Path.Combine(ZCodeHome, "cli", "db", "db.sqlite");
        if (!File.Exists(dbPath)) return result;

        // db 被 zcode 进程持有：复制主库 + WAL 到临时目录后打开（复制窗口极小，WAL 尾部少量丢失可接受，下次刷新补齐）
        var tempDir = Path.Combine(Path.GetTempPath(), "TokenUsageMonitorV3");
        Directory.CreateDirectory(tempDir);
        var tempDb = Path.Combine(tempDir, $"zcode_{Guid.NewGuid():N}.sqlite");
        var acc = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        try
        {
            CopyShared(dbPath, tempDb);
            CopyShared(dbPath + "-wal", tempDb + "-wal");

            var todayStart = new DateTimeOffset(DateTime.Today);       // 本地零点
            var todayStartMs = todayStart.ToUnixTimeMilliseconds();
            var tomorrowStartMs = todayStart.AddDays(1).ToUnixTimeMilliseconds();

            await Task.Run(() =>
            {
                var builder = new SqliteConnectionStringBuilder
                {
                    DataSource = tempDb,
                    Mode = SqliteOpenMode.ReadOnly,
                };
                using var conn = new SqliteConnection(builder.ToString());
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT provider_id, model_id,
                           COALESCE(provider_total_tokens, computed_total_tokens, input_tokens + output_tokens)
                    FROM model_usage
                    WHERE completed_at >= $start AND completed_at < $end";
                cmd.Parameters.AddWithValue("$start", todayStartMs);
                cmd.Parameters.AddWithValue("$end", tomorrowStartMs);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    var providerId = reader.IsDBNull(0) ? null : reader.GetString(0);
                    if (providerId is null) continue;
                    var model = reader.IsDBNull(1) ? "未知模型" : reader.GetString(1);
                    var tokens = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                    if (tokens <= 0) continue;
                    if (!acc.TryGetValue(providerId, out var models))
                        acc[providerId] = models = new Dictionary<string, long>(StringComparer.Ordinal);
                    models[model] = models.TryGetValue(model, out var old) ? old + tokens : tokens;
                }
            }, ct);
        }
        catch (SqliteException ex) { Logger.LogException("zcode db read", ex); }
        finally
        {
            TryDelete(tempDb); TryDelete(tempDb + "-wal"); TryDelete(tempDb + "-shm");
        }

        foreach (var (providerId, models) in acc)
        {
            if (!providers.TryGetValue(providerId, out var info)) continue;
            result.Add(new ProviderUsage(info,
                models.Select(m => new ModelTokens(m.Key, m.Value)).ToList()));
        }
        return result;
    }

    private static void CopyShared(string source, string dest)
    {
        if (!File.Exists(source)) return;
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 临时文件残留不影响功能 */ }
    }

    /// <summary>zcode provider 配置（v2/config.json）：id → 名称/apiKey/baseURL。密钥仅内存比对，不落盘不输出。</summary>
    private static Dictionary<string, ProviderInfo> LoadProviders()
    {
        var map = new Dictionary<string, ProviderInfo>(StringComparer.Ordinal);
        var configPath = Path.Combine(ZCodeHome, "v2", "config.json");
        if (!File.Exists(configPath)) return map;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (!doc.RootElement.TryGetProperty("provider", out var providers)) return map;
            foreach (var p in providers.EnumerateObject())
            {
                string? apiKey = null, baseUrl = null;
                string name = p.Name;
                if (p.Value.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                    name = nameEl.GetString() ?? p.Name;
                if (p.Value.TryGetProperty("options", out var opt))
                {
                    if (opt.TryGetProperty("apiKey", out var key)
                        && key.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(key.GetString()))
                        apiKey = key.GetString();
                    if (opt.TryGetProperty("baseURL", out var url) && url.ValueKind == JsonValueKind.String)
                        baseUrl = url.GetString();
                }
                map[p.Name] = new ProviderInfo(p.Name, name, apiKey, baseUrl);
            }
        }
        catch (Exception ex) { Logger.LogException("zcode config parse", ex); }
        return map;
    }
}
