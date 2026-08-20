# Token Consumption Monitoring

**V1.2.0** — Win11 桌面小部件：统一查询方法与能力驱动的 API 用量监控。

托盘常驻 + 悬浮窗小组件 + 面板（含扫描诊断工作台）。按“查询方法”的方式统一识别和读取各类来源的用量，页面不再绑定供应商或套餐布局。

## 架构

项目从「页面 URL → 单一适配器 → 固定布局」重构为「方法无关页面配置 → 候选扫描 → 方法选择 → 能力快照 → 能力驱动渲染」：

```text
PageConfigStore ──┐
CredentialResolver┼─> PageScanCoordinator ─> CandidateChain
LocalRecordRegistry┘            │                    │
                                 ▼                    ▼
                       MethodSelector ───────> UsageQueryCoordinator
                                                      │
              ┌───────────────────────────────────────┼───────────────┐
              ▼                                       ▼               ▼
      CapabilitySnapshot                        MethodStateStore  AlertEvaluator
              │                                       │
              ▼                                       ▼
    MainPanel / FloatingWindow（按能力渲染）     指纹缓存 / 重试 / 单飞
```

- **查询方法**（`IQueryMethod`）：描述 → 扫描 → 查询 三阶段，按“来源、能力、凭据范围”拆到可独立失败/回退的最小单元；套餐名称/planId 不参与。
- **能力化快照**（`CapabilitySnapshot`）：报告用量、报告费用、估算成本、余额/额度、滚动窗口、响应遥测、Probe 独立表达；同一能力只选一份事实，不跨来源相加，不把连接探测冒充用量。
- **自动扫描**：保存、端点/协议/凭据变化、指纹变化、连续失败、手动重扫触发；普通轮询只查已选方法。
- **诊断工作台**：面板三栏 —— 配置与扫描状态 / 候选方法链 / 能力矩阵；候选并列时可“使用此方法”临时覆盖（仅运行时，重扫后恢复）。

## 目录

| 目录 | 职责 |
|---|---|
| `Models/Usage/` | 领域契约：能力、来源、候选、快照、凭据引用 |
| `Models/PageConfig.cs` | 方法无关页面配置 + envelope 迁移 |
| `Services/QueryMethods/` | 查询方法实现 + 注册表 + 首期方法目录 |
| `Services/Scanning/` | 指纹、扫描上下文、凭据解析、候选选择 |
| `Services/Runtime/` | 页面运行时协调器、单飞、缓存、重试 |
| `Services/Persistence/` | 方法状态存储（候选链/指纹） |
| `UI/Diagnostics/` | 能力快照 / 诊断工作台 ViewModel |
| `tests/` | 迁移、契约、选择、协调器单元测试 |

## 已接入的查询方法

已实现（真实端点，使用现有客户端）：

- `endpoint.probe` —— 通用连接/鉴权/模型目录（任意协议）
- `opencode.rolling-window.api-key` —— 5h/周/月 滚动窗口
- `opencode.allowance.oauth` —— OAuth 会话窗口绝对额度
- `deepseek.balance.api-key` —— 官方余额
- `deepseek.console-usage.compat` —— 控制台会话用量（私有兼容，仅显式控制台页面启用）
- `commandcode.allowance-window.compat` —— 月额度 + 5h/周窗口（私有兼容）
- `local.zcode.usage` —— ZCode 本地 SQLite 记录（本地备选）

已登记目录（凭据门控，待接入端点）：OpenRouter key/credits/activity、OpenAI Admin Usage/Costs、Anthropic Admin Usage/Costs、OpenCode Console 导出、xAI Management、Fireworks quota/usage 与本地 OpenCode/Claude Code/Codex/Gemini 记录。这些方法参与扫描并给出可解释的凭据/未接入状态，不会冒充可用来源。

## 迁移

- `pages.json` 使用文件级 `schemaVersion` envelope；旧 `List<Page>` 数组自动迁移，**页面 Id 稳定保留**，凭据继续用兼容 target `TokenUsageMonitorV3.ApiKey.<Id>`。
- 未知 schema / 损坏 JSON / 重复 Id 只写入脱敏诊断，**不覆盖原文件**；写入为原子替换并保留 `.bak`。
- 旧数据目录 `%APPDATA%\TokenUsageMonitorV3` 先读后迁移到 `TokenConsumptionMonitoring`；旧凭据 target、互斥量、自动启动键集中在 `Legacy` 兼容常量。
- 项目 exe 名称保持 `TokenUsageMonitorV3`（覆盖升级入口不变）。

## 构建与测试

要求 .NET 8 SDK：

```bash
# Release 构建（应用 + 测试）
dotnet build TokenConsumptionMonitoring.sln -c Release

# 单元测试（迁移 / 契约 / 选择 / 协调器）
dotnet test tests/TokenConsumptionMonitoring.Tests/TokenConsumptionMonitoring.Tests.csproj -c Release

# 发行单文件（Windows 验证 WPF / WebView2 / 凭据管理器 / 通知）
dotnet publish -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

产物为单文件 `TokenUsageMonitorV3.exe`。安装包（Inno Setup 6）脚本见 `packaging/TokenUsage-Setup.iss`。

## 使用

1. 托盘图标 / 悬浮窗 → 打开配置面板
2. 「新建」：填写名称、Base URL、API Key、API 格式；保存后自动扫描可用查询方法并显示候选链
3. 「编辑」载入选中页面修改；「重新扫描」手动重扫
4. 「登录」按候选凭据类型分发（控制台会话 → WebView2；OAuth → 设备码；API Key 无需登录）
5. 候选并列时在面板点击「使用此方法」临时覆盖自动选择

## 作者

[shxtmaker](https://github.com/shxtmaker)
