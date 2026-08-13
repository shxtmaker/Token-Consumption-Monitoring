# Token Consumption Monitoring

**V1.0.0** — Win11 桌面小部件：实时监控 API Token 用量与额度窗口。

托盘常驻 + 悬浮窗小组件 + 配置面板，支持 DeepSeek 官方 API、opencode 网关与 OpenAI/Anthropic 兼容端点。

## 功能

- **页面模型**：任意多个 API 配置页面，每个页面独立配置（名称 / Base URL / API Key / API 格式 / 模型列表）
- **DeepSeek 官方用量**：内嵌 WebView2 登录控制台 → 今日 Token 消耗与预计金额（flash / pro 分模型显示），官方 CNY 单价
- **opencode 网关**：窗口限额（滚动 5h / 周 / 月 用量百分比 + 下次重置时间）
- **通用探测**：Chat Completions / Responses / Anthropic 三种协议——连接状态 + 模型列表自动拉取
- **安全存储**：API Key / 会话凭据存 Windows 凭据管理器
- **告警**：连接状态报警（页面金额/token 告警在后续版本重新制定）

## 数据来源说明

| 数据来源 | 数据 |
|---|---|
| 本地 | 仅支持读取 zcode 数据获取日 token 消耗量 |
| DeepSeek 官方（控制台 platform.deepseek.com） | 官方用量接口（需登录会话，cookie 持久化，重启免登录） |
| opencode go 套餐（opencode.ai） | 窗口限额；API-key 模式官方不提供 token 统计 |
| OpenAI / Anthropic 兼容端点 | 无官方用量端点，仅连接探测 + 模型列表 |

## 构建

要求 .NET 8 SDK：

```bash
dotnet publish -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

产物为单文件 `TokenUsageMonitorV3.exe`（免安装，双击运行）。

安装包（Inno Setup 6）脚本见 `packaging/TokenUsage-Setup.iss`，先执行上述 publish，再编译该脚本即可生成安装程序。

## 使用

1. 托盘图标 / 悬浮窗 → 打开配置面板
2. 「添加模型供应商」：填写名称、Base URL、API Key、API 格式（自动识别），可手动或自动拉取模型列表
3. 「编辑」载入当前选中页面信息修改；「保存」保存当前配置
4. 「登录」：按页面 API 类型自动分发登录方式（DeepSeek 控制台 → WebView2 登录；opencode → OAuth 设备码；API Key 页面无需登录）

## 下载

发布版见 [GitHub Releases](https://github.com/shxtmaker/Token-Consumption-Monitoring/releases)：安装程序（`TokenUsage-Setup-1.0.0.exe`）与免安装版（`TokenUsage-V1.0.0-win-x64-portable.zip`）。

## 作者

[shxtmaker](https://github.com/shxtmaker)
