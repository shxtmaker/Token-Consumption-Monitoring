# TokenConsumptionMonitoring

Windows 桌面用量与额度监控工具。通过桌面小组件、托盘和配置面板，查看多个服务账户的额度窗口、Token 用量、费用与余额。

当前版本：**1.3.2** · Windows x64

## 下载与安装

从 [GitHub 1.3.2 发布页](https://github.com/shxtmaker/Token-Consumption-Monitoring/releases/tag/v1.3.2) 下载。

| 发布文件 | 适用情况 |
| --- | --- |
| `TokenConsumptionMonitoring-Setup-1.3.2.exe` | 首次安装，或覆盖升级已有安装版。 |
| `TokenConsumptionMonitoring-Portable-1.3.2.zip` | 解压到可写目录，运行 `TokenConsumptionMonitoring.exe`。 |
| `TokenConsumptionMonitoring-Upgrade-1.3.2.exe` | 升级已安装的同一产品，保留原安装目录和安装范围；没有安装记录时请使用安装包。 |

三个包均包含 .NET 8 运行组件，无需另行安装 .NET。DeepSeek 和 Fireworks 控制台登录还需要 Microsoft Edge WebView2 Runtime；发布包不包含 WebView2 或 Codex CLI。

## 1.3.2 更新内容

- 新增 Fireworks AI 账户本月消费查询，以及登录控制台后读取真实 Credits 余额。
- Fireworks 登录会话按页面隔离保存，读取余额时核对 API Key 可访问的账户，重启后自动恢复会话。
- 小组件补全报告成本显示，并隐藏额度窗口中重复的 `ok` 状态文字。
- 补充 OpenCode Go、Command Code 和 Fireworks 的查询配置说明。
## 支持的查询

| 服务 | 可显示的数据 | 所需凭据或条件 |
| --- | --- | --- |
| Codex | 订阅额度窗口、账户累计 Token | 本机已安装并登录 Codex CLI |
| DeepSeek | 官方余额、控制台今日用量 | API Key；控制台用量另需登录会话 |
| OpenRouter | 当前 Key 额度、UTC 今日费用、账户 credits | API Key；账户 credits 需要 Management Key |
| OpenAI | 组织模型生成用量、费用 | 有对应权限的 Admin Key |
| Anthropic | 组织 Messages 用量、费用 | 有对应权限的 Admin Key |
| Moonshot | 国内 CNY、国际 USD 可用余额 | 对应站点的 API Key |
| Z.ai／智谱 | Token 5 小时、MCP 月配额百分比 | 支持相应配额接口的账户密钥 |
| MiniMax | Token Plan 模型窗口及周窗口百分比 | 对应账户密钥与套餐权限 |
| OpenCode | 滚动窗口或 OAuth 额度 | 在页面中显式启用对应查询方法 |
| Fireworks AI | 账户本月消费、控制台 Credits 余额 | API Key；余额需启用兼容查询并登录控制台 |
| Command Code | 服务端报告的窗口额度、余额或 credits | 在页面中显式启用兼容查询方法 |

实际显示内容以服务方返回的数据和账户权限为准。组织报表默认查询最近已完成的 UTC 日；Codex 累计用量不会标为今日用量。仅连接或模型目录探测成功，不代表服务提供额度接口。

完整地址、密钥类型、方法列表及限制见[查询覆盖与配置说明](docs/query-coverage.md)。DeepSeek 控制台及部分兼容方法属于私有接口，可能随服务方变化而失效。

## 开始使用

1. 启动应用，点击托盘图标打开配置面板。
2. 点击“新建”，填写页面名称、服务地址和 API 格式，选择所需凭据类型并保存。保存后自动扫描。
3. 在候选方法链中查看本次扫描可用的方法。不可用方法不会列出；没有可用方法时，列表为空。
4. 点击小组件标题切换页面。使用“刷新”更新数据，或使用“编辑”打开配置面板；锁定后小组件不可拖动并保持置顶。
5. 在诊断区使用“重新扫描”重新检测当前页面。候选并列时，可通过“使用此方法”临时选择来源；重新扫描后恢复自动选择。
6. 完全退出时，使用托盘菜单中的“退出”。关闭配置面板只会隐藏窗口，应用仍在后台运行。

“立即刷新”会刷新全部页面，“重新扫描”只处理当前页面。小组件和托盘显示当前活动页面的数据。查询失败时可能保留最近成功值，并显示相应状态；请结合最后更新时间判断数据是否有效。

## 升级与数据保留

升级前，请先从托盘退出正在运行的程序。

- **安装版**：运行安装包或升级包。升级包沿用匹配安装记录的目录与范围，不支持降级。
- **免安装版**：退出后，用新版免安装包中的全部文件覆盖原程序目录。升级包不能用于更新免安装版。
- **配置和凭据**：同一 Windows 用户下，安装版与免安装版使用相同的数据目录及 Windows 凭据存储。安装、升级和卸载均保留这些数据。

免安装仅表示无需安装程序，配置和凭据不会随压缩包迁移到其他电脑。应用采用单实例运行，已有实例未退出时，新启动的实例会结束。

本产品标识为 `TokenConsumptionMonitoring`，不能覆盖升级旧产品 `TokenUsageMonitorV3`，也不会自动读取其配置或凭据。从旧产品迁移时，请先记录所需配置、退出旧程序，再安装本产品并重新配置页面。

## 配置与恢复

配置和日志位于 `%APPDATA%\TokenConsumptionMonitoring`。API Key 保存在当前 Windows 用户的凭据存储中，`pages.json` 只保存凭据引用。

配置保存采用原子替换，并保留 `pages.json.bak`。配置损坏、版本不兼容、页面 ID 重复或关键字段缺失时，程序会进入只读恢复状态，不会直接覆盖原文件。

遇到恢复提示时，请先退出应用并备份原文件，检查有效的 `pages.json.bak` 或自行保留的配置副本，再恢复有效配置并重新启动。程序不会自动用备份覆盖损坏文件。删除页面后，历史凭据不会自动清理，以保留备份恢复能力。

## 从源码构建

需要 Windows、.NET 8 SDK 和 WPF 工具链。Release 页面提供完整源码归档，包含应用、测试、文档和打包脚本，不包含个人配置、凭据、编译缓存或发布二进制文件。

```powershell
dotnet build TokenConsumptionMonitoring.sln -c Release
dotnet test tests/TokenConsumptionMonitoring.Tests/TokenConsumptionMonitoring.Tests.csproj -c Release
dotnet run --project tests/OfficialQuerySmoke/OfficialQuerySmoke.csproj -c Release -- artifacts/official-query-verification
```

生成安装包、免安装包和升级包还需要 Inno Setup 6：

```powershell
pwsh ./packaging/release.ps1 -Version 1.3.2
```

三个包输出到 `dist/`。Inno Setup 不在默认路径时，可用 `-IsccPath` 指定 `ISCC.exe`。源码构建不会自动替换已安装程序。

## 相关文档

- [供应商数据查询与控制台登录](docs/provider-query-troubleshooting.md)
- [查询覆盖与配置说明](docs/query-coverage.md)
- [接口验证结果与范围](docs/query-coverage-verification.md)
- [1.3.2 发布验证说明](docs/releases/v1.3.2-verification.md)
- [开发与架构说明](docs/architecture.md)
- [桌面验收程序](tests/DesktopSmoke/README.md)

## 作者

[shxtmaker](https://github.com/shxtmaker)
