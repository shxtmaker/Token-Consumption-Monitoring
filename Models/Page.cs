using TokenConsumptionMonitoring.Services;

namespace TokenConsumptionMonitoring.Models;

/// <summary>
/// 页面：一套完整的 API 配置单元（v4 取代 Account）。
/// key 不存于此，仅保存页面身份相关的凭据 target 计算结果。
/// </summary>
public sealed class Page
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>页面名称（= 小组件名称）。</summary>
    public string Name { get; set; } = "";

    /// <summary>Base URL（仅作为来源识别提示）。</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>API 协议（决定认证方式与探测端点）。</summary>
    public KeyFormat.Protocol Protocol { get; set; } = KeyFormat.Protocol.ChatCompletions;

    /// <summary>模型列表（保存时自动拉取 + 手动补充）。</summary>
    public List<string> Models { get; set; } = new();

    // 告警（选填：留空 = 不告警）
    public decimal? AmountWarnCny { get; set; }      // 金额警告阈值 ¥（黄）
    public decimal? AmountCriticalCny { get; set; }  // 金额临界阈值 ¥（红+通知）
    public long? TokenWarn { get; set; }             // 每日 token 警告阈值（黄）
    public long? TokenCritical { get; set; }         // 每日 token 临界阈值（红+通知）

    public int SortOrder { get; set; }

    /// <summary>凭据管理器 target（DeepSeekConsole 页面无 key）。</summary>
    public string KeyTarget => Services.AppIdentity.ApiKeyTarget(Id);

    /// <summary>是否已配置 key（控制台会话协议无需 key）。</summary>
    public bool NeedsKey => Protocol != KeyFormat.Protocol.DeepSeekConsole;
}
