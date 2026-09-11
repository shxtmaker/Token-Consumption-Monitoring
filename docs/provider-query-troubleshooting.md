# 供应商数据查询

连接检测成功只代表 API 地址和凭据可用。用量、额度和余额需要对应的数据查询方法。

## OpenCode Go 与 Command Code

编辑页面时，勾选“启用兼容额度查询（含控制台余额）”，保存后重新扫描。OpenCode Go 使用 API 密钥读取滚动窗口；Command Code 使用 API 密钥读取窗口额度和可用 credits。兼容接口可能随供应商更新而变化。

## Fireworks AI

使用 `https://api.fireworks.ai/inference/v1` 和 API 密钥即可读取账户本月消费。应用通过官方账户列表识别凭据可访问的账户，再读取 `monthly-spend-usd` 配额的 `usage` 字段，以美元显示报告成本。

需要显示真实余额时，勾选兼容额度查询并保存页面，然后点击“登录”。在独立窗口登录与 API Key 对应的 Fireworks 账户，点击“完成并读取余额”。登录会话按页面隔离并保存在本机，后续查询自动恢复。应用读取控制台 Credits 并核对账户归属；未登录、账户不匹配或网页结构变化时不会生成余额值。

月度预算、预算上限和预算余量不是充值余额。应用不会将这些字段显示成余额，也不会在缺失数据或权限不足时显示零消费。多个账户的结果分别保留，不相加。

接口依据：[账户列表](https://docs.fireworks.ai/api-reference/list-accounts)、[配额查询](https://docs.fireworks.ai/api-reference/get-quota)、[账户配额](https://docs.fireworks.ai/guides/quotas_usage/account-quotas)。

## DeepSeek

普通 API 密钥提供账户余额。控制台 token 用量需要独立的控制台会话，不能仅通过余额接口取得。

## 本机验证

从仓库目录运行 `dotnet run --project tests/OfficialQuerySmoke -c Release -- --live-providers`，可使用当前页面配置和受保护凭据执行真实只读查询，并生成桌面组件渲染图。该检查不修改页面配置，诊断状态和图像保存在 `artifacts/live-provider-verification`。图像可能包含账户额度和消费数据，不应公开提交。
