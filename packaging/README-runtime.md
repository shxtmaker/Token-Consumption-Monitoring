# TokenConsumptionMonitoring @@VERSION@@

TokenConsumptionMonitoring 是 Windows x64 桌面用量与额度监控工具。程序常驻托盘，提供桌面组件和配置面板。

## 选择发布包

| 文件 | 用途 |
| --- | --- |
| `TokenConsumptionMonitoring-Setup-@@VERSION@@.exe` | 首次安装，或覆盖升级同一产品的安装版。 |
| `TokenConsumptionMonitoring-Portable-@@VERSION@@.zip` | 解压到可写目录后运行 `TokenConsumptionMonitoring.exe`，无需安装。 |
| `TokenConsumptionMonitoring-Upgrade-@@VERSION@@.exe` | 升级已安装的同一产品，保留原安装目录和安装范围。没有安装记录时，请使用安装包。 |

升级或卸载前，请先在托盘菜单中选择“退出”。关闭配置面板只会隐藏窗口，程序仍在后台运行。升级包不支持降级，也不能用于更新免安装版。更新免安装版时，请先退出程序，再用新包中的全部文件覆盖原程序目录。

本产品不能覆盖升级旧产品 `TokenUsageMonitorV3`，也不会自动迁移其他产品的配置或凭据。

## 运行环境

发布包包含 .NET 8 运行时，无需另行安装 .NET。内嵌登录窗口还需要 Microsoft WebView2 Runtime；缺少此运行时的电脑可从 [Microsoft WebView2 官方页面](https://developer.microsoft.com/microsoft-edge/webview2/) 获取。

Codex 账户查询需要本机安装并登录 Codex CLI。其他提供商查询需要对应权限的 API Key、管理密钥或有效控制台会话。各提供商的查询范围、地址和权限要求见 [@@VERSION@@ 查询覆盖说明](https://github.com/shxtmaker/Token-Consumption-Monitoring/blob/v@@VERSION@@/docs/query-coverage.md)。

## 开始使用

1. 运行程序后，点击托盘图标打开配置面板。
2. 点击“新建”，填写名称、服务地址、API 格式和凭据，保存后自动扫描。
3. 首次扫描和重新扫描后的候选方法链只显示可用方法；没有可用方法时，列表为空。
4. 使用“立即刷新”更新全部页面，或在诊断区使用“重新扫描”检测当前页面。
5. 需要完全关闭程序时，在托盘菜单中选择“退出”。

## 配置与升级

配置和日志位于 `%APPDATA%\TokenConsumptionMonitoring`，API Key 保存在当前 Windows 用户的凭据存储中。安装版和免安装版使用相同的数据目录及凭据，因此免安装包不具备将配置随目录携带到其他电脑的能力。

安装、升级和卸载均保留已有用户配置与凭据。程序采用单实例运行，同一 Windows 会话中已有实例运行时，再次启动的实例会退出。配置损坏或版本不兼容时，程序会进入只读恢复状态，请保留原文件并参考 [配置恢复说明](https://github.com/shxtmaker/Token-Consumption-Monitoring/blob/v@@VERSION@@/README.md#配置与恢复)。

版本下载：[GitHub Releases](https://github.com/shxtmaker/Token-Consumption-Monitoring/releases)。
