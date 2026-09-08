# TokenConsumptionMonitoring

**V1.3.0**：Windows 桌面用量与额度监控工具。应用常驻托盘，提供桌面组件和配置面板，并以能力快照统一展示不同来源的数据。

本版本新增 OpenRouter、OpenAI、Anthropic、Moonshot、Z.ai／智谱、MiniMax 和 Codex 账户查询；首次扫描和重新扫描后的候选方法链只显示可用方法。详见[覆盖范围与配置说明](docs/query-coverage.md)。

## 安装与升级

本产品使用应用标识 `TokenConsumptionMonitoring`，不能覆盖安装旧产品 `TokenUsageMonitorV3`。从旧产品迁移时，请先退出旧程序，再安装 `TokenConsumptionMonitoring-Setup-<版本号>.exe`，或解压免安装包到新目录运行 `TokenConsumptionMonitoring.exe`。

应用不会自动读取 `TokenUsageMonitorV3` 的数据目录、页面配置或凭据。迁移前请自行记录需要保留的配置，安装后重新创建页面并重新配置凭据。同为 `TokenConsumptionMonitoring` 的构建使用当前产品的数据目录，不需要仅因本轮架构重构重新创建配置。

应用为单实例运行。测试源码构建前，请从托盘退出已安装实例，再运行构建目录中的程序；已有实例未退出时，新启动的实例会直接结束。编译源码不会自动替换安装目录中的程序或生成新版安装包。

应用与发布包版本均为 `1.3.0`，适用于 Windows x64。可从 [GitHub Releases](https://github.com/shxtmaker/Token-Consumption-Monitoring/releases) 或 [Gitea Releases](http://192.168.3.100:3300/lqy/Token-Consumption-Monitoring/releases) 下载；Gitea 地址需要能够访问对应局域网。

| 发布文件 | 使用方式 |
|---|---|
| `TokenConsumptionMonitoring-Setup-1.3.0.exe` | 首次安装，或覆盖升级同一产品的安装版。 |
| `TokenConsumptionMonitoring-Portable-1.3.0.zip` | 解压到可写目录运行。更新免安装版时，退出程序后用包内程序替换原程序。 |
| `TokenConsumptionMonitoring-Upgrade-1.3.0.exe` | 仅用于已安装的同一产品，沿用原安装目录。没有安装记录时，请使用安装包。 |

更新前从托盘退出程序。同一 Windows 用户下，三个包均使用 `%APPDATA%\TokenConsumptionMonitoring` 和 Windows 凭据存储；安装、升级和卸载不删除这些数据。免安装表示无需安装程序，不表示配置和凭据随压缩包迁移。

三个包均包含 .NET 8 运行组件，无需单独安装 .NET。DeepSeek 控制台登录另需 Microsoft Edge WebView2 Runtime；发布包不捆绑该运行时。Codex 查询需要本机已有已登录的 Codex CLI，其他官方接口需要对应权限的密钥。

完整项目源码包含解决方案、应用、测试、说明和打包脚本，可使用 Release 页面自动生成的源码归档。源码归档不包含个人配置、凭据、编译缓存或已打包的二进制文件。

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
- `deepseek.console-usage.online`：DeepSeek 控制台会话今日用量（私有前端接口，控制台会话来源）。
- `deepseek.console-usage.compat`：DeepSeek 控制台会话用量（私有兼容，需显式启用；与新线上方法能力重叠时按默认优先级让位）。
- `opencode.rolling-window.api-key`：OpenCode Go 窗口数据，需页面显式启用。
- `opencode.allowance.oauth`：OpenCode OAuth 窗口额度，需页面显式启用。
- `commandcode.allowance-window.compat`：Command Code 服务端返回的窗口额度及余额／credits，需页面显式启用；不从余额推算不存在的窗口或上限。

新增官方及第一方客户端查询：

- OpenRouter：当前 Key 额度、UTC 今日费用、Management Key 账户 credits。
- OpenAI：Admin Key 组织模型生成用量及费用。
- Anthropic：Admin Key 组织 Messages 用量及费用。
- Moonshot：国内 CNY 与国际 USD 可用余额。
- Z.ai／智谱：官方插件公开的 Token 5 小时与 MCP 月配额百分比。
- MiniMax：Token Plan 明确报告的模型窗口及周窗口百分比。
- Codex：通过本机已登录 CLI 读取订阅窗口及账户累计 Token。

组织报表默认查询最近已完成的 UTC 日，明确标注范围；Codex 累计用量不标为今日数据。DeepSeek 余额保留全部币种，控制台来源按私有前端接口标记。默认注册表共 19 个方法，完整方法 ID、配置地址和限制见[覆盖评估](docs/query-coverage.md)。

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

发布产物为 `TokenConsumptionMonitoring.exe`。安装 Inno Setup 6 后，从项目根目录运行以下命令，可一次生成安装包、免安装包和升级包：

```powershell
pwsh ./packaging/release.ps1 -Version 1.3.0
```

脚本校验项目版本、程序文件版本和产品标识；三个发布包输出到 `dist/`。Inno Setup 不在默认路径时，可通过 `-IsccPath` 指定 `ISCC.exe`。安装脚本位于 `packaging/TokenConsumptionMonitoring-Setup.iss`。

安装、升级和卸载的独立验证脚本为 `packaging/verify-packages.ps1`，使用不同于正式产品的测试安装标识。具体结果与验证边界见 [1.3.0 发布验证](docs/releases/v1.3.0-verification.md)。

## 使用

1. 从托盘或桌面组件打开配置面板。
2. 点击「新建」，填写名称、Base URL、API 格式和模型配置提示。选择普通 API Key、Admin Key、Management Key 或本机 Codex 登录；DeepSeekConsole 使用控制台会话。保存后自动扫描。
3. 首次扫描或重新扫描完成后，候选方法链仅列出本次扫描可用的方法，并显示来源、凭据范围及检测证据。不可用的方法不显示；没有可用方法时，列表为空且数量为 0。
4. 候选并列时可点击「使用此方法」临时覆盖自动选择；重新扫描后恢复自动选择。
5. 编辑表单中的「登录」按当前表单协议处理：DeepSeekConsole 打开控制台登录窗；存在等待 OAuth 的活动页候选时打开 OpenCode 设备码流程；其他 API Key 页面提示无需登录。
6. 托盘或表单中的「立即刷新」刷新全部页面；诊断区的「重新扫描」只处理当前页面。结果仅投影到活动页面。
7. 面板右上角关闭按钮会隐藏面板，应用继续常驻；完全退出请使用托盘菜单的「退出」。

## 验证情况与范围

2026-09-08，查询覆盖扩展通过 **136 项自动测试**，WPF 凭据选择与保存、候选列表、窄窗口和小组件渲染验证通过。本机 Codex CLI 0.153.4 的两个账户查询均真实成功。其他提供商接口通过契约与模拟 HTTP 测试，尚未使用真实账户密钥联调。详见[验证记录](docs/query-coverage-verification.md)。

以下为原有 1.2.3 基线的历史验证记录。

2026-09-07，本轮重构的自动化测试为 **67 项通过**，Release 构建为 **0 警告、0 错误**。测试覆盖配置与凭据提交失败、损坏配置保护、冷却、迟到结果隔离、活动页切换及停止等待等行为。

独立桌面测试使用真实面板、引擎、协调器和存储，查询源与托盘输出使用测试适配器；已验证模拟断网保留历史值、会话变化后恢复、切页、连续刷新、关闭、重启和配置备份恢复。测试程序中的「模拟断网」等按钮仅用于验收，不属于正式界面，详见 [桌面验收说明](tests/DesktopSmoke/README.md)。

正式构建已完成实机使用确认，反馈为整体正常。该反馈及模拟测试不代表所有真实供应商的登录和查询接口均已逐项联调。

## 作者

[shxtmaker](https://github.com/shxtmaker)
