using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.Scanning;

namespace TokenConsumptionMonitoring.Services.QueryMethods;

public interface ICodexAccountClient
{
    bool IsAvailable { get; }
    Task<JsonElement> ReadAsync(string method, CancellationToken ct);
}

/// <summary>通过已安装 Codex CLI 的官方 stdio 协议只读账户状态，不启动对话或读取登录秘密。</summary>
public sealed class CodexAppServerClient : ICodexAccountClient
{
    public bool IsAvailable => FindExecutable() is not null;

    internal static string? FindExecutable()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.Combine(directory.Trim('"'), "codex.exe");
            if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public async Task<JsonElement> ReadAsync(string method, CancellationToken ct)
    {
        if (method is not ("account/rateLimits/read" or "account/usage/read")) throw new ArgumentException("只允许账户查询方法");
        var executable = FindExecutable() ?? throw new QueryTransportException(CandidateStatus.Unsupported, "未找到 PATH 中的 codex.exe");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var token = timeout.Token;
        using var process = new Process { StartInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        } };
        process.StartInfo.ArgumentList.Add("app-server");
        process.StartInfo.ArgumentList.Add("--listen");
        process.StartInfo.ArgumentList.Add("stdio://");
        if (!process.Start()) throw new QueryTransportException(CandidateStatus.NetworkFailure, "Codex 账户查询进程未启动");
        // 持续排空 stderr，不保存可能含本机路径或账户诊断的原文。
        var drain = DrainAsync(process.StandardError, token);
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                id = 0, method = "initialize", @params = new
                { clientInfo = new { name = "token_consumption_monitoring", title = "Token Consumption Monitoring", version = "1.2.3" } },
            }).AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
            _ = await ReadResponseAsync(process.StandardOutput, 0, token);
            await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\",\"params\":{}}".AsMemory(), token);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id = 1, method }).AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
            return await ReadResponseAsync(process.StandardOutput, 1, token);
        }
        finally
        {
            process.StandardInput.Close();
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await process.WaitForExitAsync(shutdown.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            timeout.Cancel();
            try { await drain; } catch (OperationCanceledException) { } catch (IOException) { }
        }
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer.AsMemory(), ct) > 0) { }
    }

    internal static async Task<JsonElement> ReadResponseAsync(StreamReader reader, int id, CancellationToken ct)
    {
        for (var messages = 0; messages < 200; messages++)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) throw new QueryTransportException(CandidateStatus.NetworkFailure, "Codex 查询进程提前结束");
            if (line.Length > 4 * 1024 * 1024) throw new QueryTransportException(CandidateStatus.SchemaMismatch, "Codex 响应超过读取上限");
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.Number || !responseId.TryGetInt32(out var actualId) || actualId != id) continue;
            if (root.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : 0;
                throw new QueryTransportException(code == -32601 ? CandidateStatus.Unsupported : code is -32600 or -32602 ? CandidateStatus.SchemaMismatch : CandidateStatus.NetworkFailure,
                    code == -32601 ? "当前 Codex CLI 不支持此账户查询，请升级 CLI" : "Codex 账户查询失败，请检查 CLI 登录、网络和账户权限");
            }
            return root.GetProperty("result").Clone();
        }
        throw new QueryTransportException(CandidateStatus.SchemaMismatch, "Codex 未返回对应查询结果");
    }
}

/// <summary>账户订阅窗口与累计 Token 分别查询，不能用 API Key 账单替代 ChatGPT 订阅。</summary>
public sealed class CodexAccountMethod(bool usage = false, ICodexAccountClient? client = null) : IQueryMethod
{
    private readonly ICodexAccountClient _client = client ?? new CodexAppServerClient();
    private readonly QueryMethodDescriptor _descriptor = new(
        usage ? "codex.account-usage.local-session" : "codex.rate-limits.local-session",
        usage ? SourceKind.RemoteOfficialStats : SourceKind.RollingWindowSnapshot, CredentialClass.LocalRecord,
        QueryMethodDescriptor.CapabilitiesOf(usage ? CapabilityKind.ReportedUsage : CapabilityKind.RollingWindow),
        SourceStability.OfficialConditional, MethodEnablement.Conditional, 30, "1.0.0");

    public QueryMethodDescriptor Describe() => _descriptor;
    internal static bool Matches(PageConfigRecord page) => page.CredentialRef.Kind == CredentialRefKind.LocalRecord
        && Uri.TryCreate(page.BaseUrl, UriKind.Absolute, out var url) && url.Scheme == "https" && url.IsDefaultPort
        && url.Host == "chatgpt.com" && url.AbsolutePath == "/" && url.Query.Length == 0 && url.UserInfo.Length == 0 && url.Fragment.Length == 0;

    public async Task<MethodCandidate> ScanAsync(PageConfigRecord page, ScanContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Matches(page))
            return MethodSupport.NotAvailable(_descriptor, CandidateStatus.Unsupported, "需要选择本机 Codex 登录并使用 https://chatgpt.com");
        if (!_client.IsAvailable)
            return MethodSupport.NotAvailable(_descriptor, CandidateStatus.Unsupported, "未找到 PATH 中的 codex.exe，请安装并登录 Codex CLI");

        var result = await QueryAsync(page, null!, ct);
        if (result.Failure is not null || result.Status is not (SnapshotStatus.Success or SnapshotStatus.NoData))
        {
            // 查询快照将不支持归为永久失败，候选仍需保留不支持的明确状态。
            var status = result.Status == SnapshotStatus.PermanentFailure
                ? CandidateStatus.Unsupported : result.Failure?.Status ?? CandidateStatus.SchemaMismatch;
            return MethodSupport.NotAvailable(_descriptor, status, result.Failure?.Reason ?? "Codex 账户查询未通过");
        }

        // 成功解析的空窗口或空累计值表示接口可用，但没有可显示的数据。
        var capability = result.Capabilities.FirstOrDefault();
        return MethodSupport.Available(_descriptor, capability?.CredentialScope ?? new(CredentialClass.LocalRecord, "codex"),
            capability?.Coverage ?? Coverage.Unknown,
            [DetectionEvidence.LocalSchema("Codex 账户接口与响应结构已验证")], capability?.Source ?? Source(page), 85);
    }

    private SourceIdentity Source(PageConfigRecord page, string? account = null) => new("codex",
        $"local-session:page:{page.Id}:{account ?? "current"}", _descriptor.MethodId, "codex-app-server:stdio", account);

    public async Task<MethodQueryResult> QueryAsync(PageConfigRecord page, MethodCandidate candidate, CancellationToken ct)
    {
        if (!Matches(page)) return MethodQueryResult.Empty(SnapshotStatus.PermanentFailure, "本机 Codex 查询配置不匹配");
        try
        {
            var data = await _client.ReadAsync(usage ? "account/usage/read" : "account/rateLimits/read", ct);
            var values = Parse(page, data);
            return new(values, values.Count > 0 ? SnapshotStatus.Success : SnapshotStatus.NoData, null, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is QueryTransportException or JsonException or InvalidOperationException or KeyNotFoundException
            or FormatException or OverflowException or ArgumentOutOfRangeException or IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            var error = ex as QueryTransportException;
            var status = error is not null ? QueryFailureClassifier.SnapshotStatusOf(error.Status)
                : ex is IOException or OperationCanceledException or System.ComponentModel.Win32Exception ? SnapshotStatus.TemporaryFailure : SnapshotStatus.SchemaMismatch;
            return MethodQueryResult.Empty(status, error?.Message ?? "Codex 账户查询不可用或响应结构不兼容");
        }
    }

    private IReadOnlyList<CapabilityValue> Parse(PageConfigRecord page, JsonElement root)
    {
        var now = DateTimeOffset.UtcNow;
        var values = new List<CapabilityValue>();
        var account = root.TryGetProperty("accountId", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
        var scope = new CredentialScope(CredentialClass.LocalRecord, "codex", account);
        if (usage)
        {
            var summary = root.GetProperty("summary");
            var lifetime = summary.GetProperty("lifetimeTokens");
            if (lifetime.ValueKind == JsonValueKind.Null) return [];
            var tokens = lifetime.GetInt64();
            if (tokens < 0) throw new FormatException();
            return [new ReportedUsageValue(CapabilityKind.ReportedUsage, Source(page), scope,
                new Coverage(null, null, Granularity.Unknown, "Codex 账户累计 Token · 非今日用量"), now, 1, false, false,
                tokens, null, [], now.AddMinutes(5))];
        }
        var foundMap = root.TryGetProperty("rateLimitsByLimitId", out var map)
            && map.ValueKind == JsonValueKind.Object && map.EnumerateObject().Any();
        if (foundMap)
        {
            foreach (var bucket in map.EnumerateObject()) AddBucket(bucket.Name, bucket.Value);
        }
        else if (root.TryGetProperty("rateLimits", out var legacy) && legacy.ValueKind == JsonValueKind.Object)
            AddBucket("codex", legacy);
        else if (!root.TryGetProperty("rateLimits", out _) && !root.TryGetProperty("rateLimitsByLimitId", out _))
            throw new FormatException();
        return values;

        void AddBucket(string id, JsonElement bucket)
        {
            if (bucket.ValueKind == JsonValueKind.Null) return;
            var name = bucket.TryGetProperty("limitName", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : id;
            foreach (var slot in new[] { "primary", "secondary" })
            {
                if (!bucket.TryGetProperty(slot, out var window) || window.ValueKind == JsonValueKind.Null) continue;
                var percent = window.GetProperty("usedPercent").GetDecimal();
                if (percent is < 0 or > 100) throw new FormatException();
                var minutes = window.TryGetProperty("windowDurationMins", out var duration) && duration.ValueKind != JsonValueKind.Null ? duration.GetInt64() : (long?)null;
                var reset = window.TryGetProperty("resetsAt", out var r) && r.ValueKind != JsonValueKind.Null ? DateTimeOffset.FromUnixTimeSeconds(r.GetInt64()) : (DateTimeOffset?)null;
                if (minutes <= 0) throw new FormatException();
                var label = minutes is { } m ? m % 1440 == 0 ? $"{m / 1440} 天" : m % 60 == 0 ? $"{m / 60} 小时" : $"{m} 分钟" : slot;
                values.Add(new RollingWindowValue(CapabilityKind.RollingWindow, Source(page, account), scope, Coverage.Unknown,
                    now, 1, false, false, $"{id}:{slot}", $"{name} · {label}", null, null, null, null,
                    (int)Math.Round(percent), reset, "%", reset is { } end && end < now.AddMinutes(5) ? end : now.AddMinutes(5)));
            }
        }
    }
}
