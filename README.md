# TokenConsumptionMonitoring

**V1.2.0**：Windows 桌面用量与额度监控工具。应用常驻托盘，提供桌面组件和配置面板，并以能力快照统一展示不同来源的数据。

## 安装与升级

本版本使用新的应用标识 `TokenConsumptionMonitoring`，不能覆盖安装旧版 `TokenUsageMonitorV3`。从旧版升级时必须退出旧程序，并重新安装 `TokenConsumptionMonitoring-Setup-1.2.0.exe`，或下载免安装压缩包后解压到新目录运行 `TokenConsumptionMonitoring.exe`。

本版本不会自动读取旧版的数据目录、页面配置或凭据。升级前请在旧版中自行记录需要保留的配置，安装后重新创建页面并重新配置凭据。

## 架构

页面只保存名称、端点、协议、凭据引用和配置提示。运行时按以下边界工作：

```text
PageConfigStore
      │
      ▼
PageRuntimeCoordinator ──> CapabilitySourcePlan ──> QueryMethod
      │                                      │
      ▼                                      ▼
PageRuntimeStateStore                    CapabilitySnapshot
      │                                      │
      └──────────────> 活动页投影到面板、桌面组件和托盘
```

- `IQueryMethod` 统一描述、扫描和查询阶段。方法不绑定套餐或固定页面布局。
- `CapabilitySourcePlan` 按能力槽选择来源。同一能力槽只保留一个来源，选中来源返回的多个窗口、币种或统计条目全部保留。
- 普通轮询只查询当前计划中的来源。来源失败时保留最近成功值并标记为过期，同时为受影响能力尝试候选回退。
- `PageRuntimeStateStore` 按页面保存扫描报告、来源计划、快照、失败状态和进程内临时覆盖。非活动页面不会改写活动页面的界面、托盘或桌面组件。
- 报告用量和报告成本直接展示；估算成本、模型目录和 token 细分只保留在领域接口中。

## 目录

| 目录 | 职责 |
|---|---|
| `Models/Usage/` | 能力、来源、候选、快照和凭据引用契约 |
| `Models/PageConfig.cs` | 方法无关的页面配置和版本化文档 |
| `Services/QueryMethods/` | 查询方法、错误分类和注册表 |
| `Services/Scanning/` | 指纹、扫描上下文、凭据解析和候选选择 |
| `Services/Runtime/` | 页面协调、来源计划、缓存、重试和运行时状态 |
| `Services/Persistence/` | 页面扫描状态和候选选择持久化 |
| `UI/Diagnostics/` | 能力快照和扫描诊断视图模型 |
| `tests/` | 配置恢复、能力契约、来源选择和协调器回归测试 |

## 已接入的查询方法

已实现并注册：

- `endpoint.probe`：通用连接、鉴权和模型目录探测，不产生用量结论。
- `deepseek.balance.api-key`：DeepSeek 官方余额。
- `local.zcode.usage`：本机 ZCode SQLite 记录，本地回退来源。
- `deepseek.console-usage.compat`：DeepSeek 控制台会话用量，需页面显式启用。
- `opencode.rolling-window.api-key`：OpenCode Go 窗口数据，需页面显式启用。
- `opencode.allowance.oauth`：OpenCode OAuth 窗口额度，需页面显式启用。
- `commandcode.allowance-window.compat`：Command Code 窗口额度，需页面显式启用。

尚未实现的方法不注册为候选，不会以占位结果参与扫描或查询。

## 配置与恢复

- `pages.json` 使用带 `schemaVersion` 的文件级 envelope。旧根数组只做当前文档结构迁移，页面 Id 保持不变，并使用新的页面 API key 引用格式。
- JSON 损坏、版本过新、重复 Id 或关键字段缺失时进入只读恢复态。普通保存会被存储层拒绝，原文件保持不变。
- 配置写入使用临时文件和原子替换，并保留一份 `.bak` 备份。
- 新版本只使用 `TokenConsumptionMonitoring` 的数据目录、日志、凭据 target、互斥量、自动启动项和发布产物；不会自动导入其他产品标识下的数据或凭据。
- 私有兼容方法默认不执行网络请求。临时来源覆盖只存于当前进程，重新扫描、配置变化或重启后失效。

## 构建与测试

要求 .NET 8 SDK 和 Windows WPF 工具链：

```bash
dotnet build TokenConsumptionMonitoring.sln -c Release
dotnet test tests/TokenConsumptionMonitoring.Tests/TokenConsumptionMonitoring.Tests.csproj -c Release
dotnet publish TokenConsumptionMonitoring.csproj -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

发布产物为 `TokenConsumptionMonitoring.exe`。Inno Setup 6 安装脚本位于 `packaging/TokenConsumptionMonitoring-Setup.iss`。

## 使用

1. 从托盘或桌面组件打开配置面板。
2. 点击「新建」，填写名称、Base URL、API Key、API 格式和配置提示，保存后自动扫描。
3. 在候选方法链中查看来源、凭据范围、状态和诊断证据。
4. 候选并列时可点击「使用此方法」临时覆盖自动选择；重新扫描后恢复自动选择。
5. 「登录」按当前页面凭据类型打开 DeepSeek 控制台或 OpenCode 设备码流程。

## 作者

[shxtmaker](https://github.com/shxtmaker)
