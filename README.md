# TokenConsumptionMonitoring

**V1.2.3**：Windows 桌面用量与额度监控工具。应用常驻托盘，提供桌面组件和配置面板，并以能力快照统一展示不同来源的数据。

本轮更新主要重构配置提交、后台刷新和故障恢复，保留原有正式界面与当前产品的数据目录。启动新构建后，页面外观和已有配置基本不变；它不是一次界面改版。

## 安装与升级

本产品使用应用标识 `TokenConsumptionMonitoring`，不能覆盖安装旧产品 `TokenUsageMonitorV3`。从旧产品迁移时，请先退出旧程序，再安装 `TokenConsumptionMonitoring-Setup-<版本号>.exe`，或解压免安装包到新目录运行 `TokenConsumptionMonitoring.exe`。

应用不会自动读取 `TokenUsageMonitorV3` 的数据目录、页面配置或凭据。迁移前请自行记录需要保留的配置，安装后重新创建页面并重新配置凭据。同为 `TokenConsumptionMonitoring` 的构建使用当前产品的数据目录，不需要仅因本轮架构重构重新创建配置。

应用为单实例运行。测试源码构建前，请从托盘退出已安装实例，再运行构建目录中的程序；已有实例未退出时，新启动的实例会直接结束。编译源码不会自动替换安装目录中的程序或生成新版安装包。

应用与安装包版本均为 `1.2.3`。安装包为 `TokenConsumptionMonitoring-Setup-1.2.3.exe`，免安装包为 `TokenConsumptionMonitoring-Portable-1.2.3.zip`，可从 [GitHub Releases](https://github.com/shxtmaker/Token-Consumption-Monitoring/releases) 下载。

## 架构

页面只保存名称、端点、协议、凭据引用和配置提示。运行时按以下边界工作：

```text
MainPanel / PageEditorViewModel
      │
      ▼
PageConfigurationCommands ──> 凭据存储 + PageConfigStore
      │ 提交成功后发布
      ▼
PageCatalog ──> PageEngine / PageRefreshQueue
                        │
                        ▼
              PageRuntimeCoordinator
                ├─ CapabilitySourcePlan：按能力选择来源
                ├─ MethodExecutor ──> IQueryMethod：查询、重试与缓存
                └─ SnapshotPolicy：状态与历史值决策
                        │
                        ▼
              活动页投影到面板、桌面组件和托盘
```

- `IQueryMethod` 统一描述、扫描和查询阶段。方法不绑定套餐或固定页面布局。
- `CapabilitySourcePlan` 按能力槽选择来源。同一能力槽只保留一个来源，选中来源返回的多个窗口、币种或统计条目全部保留。
- 普通轮询只查询当前计划中的来源。来源失败时保留最近成功值并标记为过期，同时为受影响能力尝试候选回退。
- `PageCatalog` 统一管理页面集合并提供独立副本。配置修订或删除后，即使旧请求忽略取消并迟到返回，也不能覆盖新页面的运行时结果。
- `PageRefreshQueue` 合并相同页面、修订和刷新原因的在途请求；手动重扫不会被普通轮询吞掉。退出时取消并等待引擎任务完成后再释放资源。
- 手动重扫绕过普通结果缓存，但保留同一数据身份下的最近成功值。限流冷却仍生效：使用已传递的 `Retry-After`，未提供时默认冷却一分钟。
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
| `Services/PageCatalog.cs` | 页面集合所有权、修订检查与提交后发布 |
| `Services/PageConfigurationCommands.cs` | 配置与版本化凭据的一致提交 |
| `UI/PageEditorViewModel.cs` | 编辑草稿、校验与保存状态 |
| `UI/MonitorState.cs` | 活动页面的展示状态 |
| `UI/Diagnostics/` | 能力快照和扫描诊断视图模型 |
| `tests/TokenConsumptionMonitoring.Tests/` | 配置恢复、凭据提交、能力契约、来源选择及生命周期回归测试 |
| `tests/DesktopSmoke/` | 使用模拟查询源的独立桌面引擎验收程序 |

## 已接入的查询方法

已实现并注册：

- `endpoint.probe`：通用连接、鉴权和模型目录探测，不产生用量结论。
- `deepseek.balance.api-key`：DeepSeek 官方余额。
- `deepseek.console-usage.online`：DeepSeek 控制台会话今日用量（线上拉取，官方账号页面常开来源）。
- `deepseek.console-usage.compat`：DeepSeek 控制台会话用量（私有兼容，需显式启用；与新线上方法能力重叠时按来源稳定性让位）。
- `opencode.rolling-window.api-key`：OpenCode Go 窗口数据，需页面显式启用。
- `opencode.allowance.oauth`：OpenCode OAuth 窗口额度，需页面显式启用。
- `commandcode.allowance-window.compat`：Command Code 服务端返回的窗口额度及余额／credits，需页面显式启用；不从余额推算不存在的窗口或上限。

尚未实现的方法不注册为候选，不会以占位结果参与扫描或查询。

## 配置与恢复

- `pages.json` 使用带 `schemaVersion` 的文件级 envelope。旧根数组只做当前文档结构迁移，页面 Id 保持不变，并使用新的页面 API key 引用格式。
- JSON 损坏、版本过新、重复 Id 或关键字段缺失时进入只读恢复态。普通保存会被存储层拒绝，原文件保持不变。
- 配置面板会持续显示只读恢复提示。请先退出应用并保留损坏文件，再检查有效的 `pages.json.bak` 或自行保留的配置副本；恢复有效配置后重新启动。程序不会自动用备份覆盖损坏文件。
- 配置写入使用临时文件和原子替换，并保留一份 `.bak` 备份。
- 修改 API Key 时先写入新的版本化凭据，再提交配置引用；未修改密钥时保留原引用。提交失败不覆盖旧凭据；删除页面不会自动删除其凭据，历史版本也不会自动清理，以保留备份恢复能力。
- 配置和日志位于 `%APPDATA%\TokenConsumptionMonitoring`。API Key 通过 Windows 凭据存储保存，`pages.json` 保存凭据引用，不保存密钥明文。
- 新版本只使用 `TokenConsumptionMonitoring` 的数据目录、日志、凭据 target、互斥量、自动启动项和发布产物；不会自动导入其他产品标识下的数据或凭据。
- 私有兼容方法默认不执行网络请求。临时来源覆盖只存于当前进程，重新扫描、配置变化或重启后失效。

## 构建与测试

要求 .NET 8 SDK 和 Windows WPF 工具链：

```powershell
dotnet build TokenConsumptionMonitoring.sln -c Release
dotnet test tests/TokenConsumptionMonitoring.Tests/TokenConsumptionMonitoring.Tests.csproj -c Release
dotnet publish TokenConsumptionMonitoring.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

发布产物为 `TokenConsumptionMonitoring.exe`。Inno Setup 6 安装脚本位于 `packaging/TokenConsumptionMonitoring-Setup.iss`。

## 使用

1. 从托盘或桌面组件打开配置面板。
2. 点击「新建」，填写名称、Base URL、API 格式和模型配置提示。API Key 协议需填写密钥；DeepSeekConsole 使用控制台会话。保存后自动扫描。
3. 在候选方法链中查看来源、凭据范围、状态和诊断证据。
4. 候选并列时可点击「使用此方法」临时覆盖自动选择；重新扫描后恢复自动选择。
5. 编辑表单中的「登录」按当前表单协议处理：DeepSeekConsole 打开控制台登录窗；存在等待 OAuth 的活动页候选时打开 OpenCode 设备码流程；其他 API Key 页面提示无需登录。
6. 托盘或表单中的「立即刷新」刷新全部页面；诊断区的「重新扫描」只处理当前页面。结果仅投影到活动页面。
7. 面板右上角关闭按钮会隐藏面板，应用继续常驻；完全退出请使用托盘菜单的「退出」。

## 验证情况与范围

2026-09-07，本轮重构的自动化测试为 **67 项通过**，Release 构建为 **0 警告、0 错误**。测试覆盖配置与凭据提交失败、损坏配置保护、冷却、迟到结果隔离、活动页切换及停止等待等行为。

独立桌面测试使用真实面板、引擎、协调器和存储，查询源与托盘输出使用测试适配器；已验证模拟断网保留历史值、会话变化后恢复、切页、连续刷新、关闭、重启和配置备份恢复。测试程序中的「模拟断网」等按钮仅用于验收，不属于正式界面，详见 [桌面验收说明](tests/DesktopSmoke/README.md)。

正式构建已完成实机使用确认，反馈为整体正常。该反馈及模拟测试不代表所有真实供应商的登录和查询接口均已逐项联调。

## 作者

[shxtmaker](https://github.com/shxtmaker)
