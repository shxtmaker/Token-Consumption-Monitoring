# 查询覆盖验证记录

日期：2026-09-08。基线：`ae0c6a9`。分支：`feat/official-query-coverage-20260908`。

## 自动测试

```powershell
dotnet build TokenConsumptionMonitoring.sln -c Release
dotnet test tests/TokenConsumptionMonitoring.Tests/TokenConsumptionMonitoring.Tests.csproj -c Release
```

Release 构建成功，0 警告、0 错误。自动测试 136 项通过，0 失败、0 跳过。

新增验证覆盖：

- 官方 HTTPS 主机、路径和端口限制；不匹配地址不读取凭据、不发送请求。
- 管理密钥不进入普通 Key 查询或模型探测；凭据类别在保存、编辑、文件重载后保持一致。
- 兼容 Anthropic 推理协议的第三方 Key 不被强制要求使用 Anthropic 品牌前缀。
- DeepSeek 多币种、显式零余额、负余额和缺字段；Moonshot 地域币种及报告可用余额。
- OpenRouter 周期额度不使用终身消费，无限额度不冒充零余额。
- OpenAI 分页、缓存 Token 不重复计数、半途失败不发布部分总数、重复游标终止。
- Anthropic 缓存写入类别求和、未知请求数保留 null、美元美分精确转换。
- Z.ai 原样 Authorization 和已知窗口；MiniMax 明确百分比优先、毫秒时间、周加成和旧计数歧义。
- HTTP 401／403／429／503 与重定向分类、Retry-After、错误脱敏。
- Codex 多 limit ID、空窗口、64 位累计 Token、显式本机登录选择、JSON-RPC 通知与错误。
- 官方用量和费用通过真实协调器完成扫描、按能力选源和界面投影。
- 首次扫描只显示可用候选；重新扫描后替换旧列表，覆盖可用与不可用之间的双向变化。全部不可用时列表为空，保留总体鉴权提示；可用来源并列时继续显示全部可用项。
- Codex 扫描会读取对应账户接口并核验响应；未登录、旧接口、权限、网络或结构错误不形成可用候选，成功但暂无数据的接口保留可用状态。

原有配置恢复、并发刷新、活动页隔离、旧值保留及退出生命周期测试继续通过。

## WPF 验证

```powershell
dotnet run --project tests/OfficialQuerySmoke/OfficialQuerySmoke.csproj -c Release -- artifacts/official-query-verification
```

使用正式 `MainPanel` 与 `FloatingWindow` 控件、内存配置和合成数据：

- 在真实下拉框选择 Admin Key，点击保存后确认页面保留 Admin 类别。
- 选择本机 Codex 登录后密钥框不可输入，无密钥保存成功。
- 1080 像素和 720 像素宽度下，配置表单及滚动容器正常。
- Codex 窗口名称、已用百分比、重置倒计时、累计 Token 与范围完整显示。
- 同时显示 CNY 与 USD 余额，不合并币种。
- 首次扫描、重新扫描及全部不可用三种候选列表状态显示正确，数量只统计可用方法。

输出共七张 PNG。原有四张与新增三张候选列表截图均已检查，无文字裁切；截图仅为合成验收数据。测试不会启动已安装应用、写入其配置或打开测试窗口。

## Codex 真实接口

```powershell
dotnet run --project tests/OfficialQuerySmoke/OfficialQuerySmoke.csproj -c Release -- --live-codex
```

本机 Codex CLI 版本 0.153.4，当前用户已登录。验证输出：

```text
codex.rate-limits.local-session: Success, capabilities=3
codex.account-usage.local-session: Success, capabilities=1
```

调用只执行初始化及账户读取，不创建对话、不执行模型推理。验证日志只包含结果状态与条目数量，没有记录账户标识、额度值或登录秘密。

## 发布前试用程序历史记录

通过 README 中的 Windows x64 自包含发布方式生成：

```powershell
dotnet publish TokenConsumptionMonitoring.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/official-query-preview
```

程序：`artifacts/official-query-preview/TokenConsumptionMonitoring.exe`，大小 73,125,077 字节。SHA-256：

```text
0E8A06EAD4B590130ED11168A5F39DD6956BF35966D0511C35027A9339FC5224
```

以上哈希对应 1.3.0 发布前的本地试用构建，不用于校验正式 Release 附件。运行前从托盘退出已安装实例；应用使用同一产品的数据目录和单实例标识。

2026-09-08 08:25（Asia/Shanghai）已使用更新后的试用程序重启本地试用实例，核对进程路径与文件哈希，进程响应正常，日志记录 `app started`。

## 验证限制

OpenRouter、OpenAI Admin、Anthropic Admin、Moonshot、Z.ai、MiniMax 和 DeepSeek 新解析路径没有使用真实服务密钥联调。官方文档、第一方源码、合成响应和模拟 HTTP 测试不能替代所有账户权限、地区差异、服务侧限流及长期稳定性验收。

新增功能随 1.3.0 提供。1.2.3 及更早的已发布安装包不包含本扩展。正式安装、免安装与升级包的说明见 [1.3.0 发布说明](releases/v1.3.0.md)。
