# OpenDesign 订阅剩余额度可行性评估

评估日期：2026-10-13。代码基线：v1.3.4，`595951ade4bb571b4d5a90bfd7fdb0f9cd47a855`。本文仅记录评估，不代表功能已经实现或发布。

补充：本文的“订阅剩余额度”指钱包订阅资金，不能代替 Design Plan 按模型倍率计算的等值额度。关于模型对应月度参考额度和可用量估算，参见 [按选定模型估算额度评估](model-aware-quota-estimation-assessment.md)。

## 结论

可以参照 Command Code 的私有兼容结构，将 OpenDesign 的订阅剩余额度与 5 小时、7 天窗口并列展示，并停止读取和展示账户钱包合计余额。Base URL、API Key、协议和模型配置无需改变。

建议名称为“订阅剩余额度”。当前证据可以确认订阅资金与充值资金分别存在，但不足以确认该余额属于独立的月度调用限额。因此不建议直接命名为“月度调用额度”，也不应据此生成月度进度条。

## 一手证据

1. 当前 [OpenDesign 官方控制台](https://open-design.ai/cloud/dashboard) 中，Design Plan 显示 5 小时与 7 天额度；钱包明细另外列出“订阅额度”和“充值额度”。已在登录状态下查看当前个人工作空间，确认两类数值独立展示。评估文档不保存账户标识或具体账户金额。
2. 官方部署的 [cloud-root-X9i-hM9W.js](https://open-design.ai/cloud/assets/cloud-root-X9i-hM9W.js) 定义 `GET /v1/billing/summary`。请求使用控制台会话 `credentials: include`，并可附加 `x-vela-workspace-id`。现有钱包合计请求为 `/v1/workspaces/{workspaceId}/wallet/balance`；两者不是同一数据接口。
3. 官方部署的 [pricing-refund-policy-8aOZ9lOO.js](https://open-design.ai/cloud/assets/pricing-refund-policy-8aOZ9lOO.js) 定义摘要字段 `subscriptionCreditsUsd`、`rechargeCreditsUsd`、`balances.subscriptionCredits`、`creditsPerUsd`，以及订阅计费周期字段。其 API 基址为 `https://amr-api.open-design.ai/api`。建议优先读取 `subscriptionCreditsUsd`，避免另行推测 credits 的金额换算。
4. 官方前端用 `subscriptionCreditsUsd` 构建订阅余额明细。旧钱包视图对个人工作空间才允许账户余额分解；团队工作空间不得直接套用个人摘要。因此实现必须检查工作空间类型、成员身份及数据范围。
5. 当前窗口接口 `/v1/workspaces/{workspaceId}/billing/coding-plan-usage` 提供独立窗口数组。现有个人工作空间页面仅观察到 5 小时与 7 天窗口，没有观察到独立月度窗口。摘要中的订阅周期终点不等于已证实的月度窗口重置时间，尤其不能将年付订阅结束时间当作每月重置时间。

公开源码下载快照位于本地忽略目录 `artifacts/opendesign-research/`。SHA-256：

| 文件 | SHA-256 |
| --- | --- |
| current-cloud-root.js | `5f91084261f45738362895fdaf5e95dcc6359b0e5b3eb2a8db9c43763ae09128` |
| current-shared.js | `9231802b34f2381b1c9e46972da0a27a1f9777af62a5dd0a76db8d1950db1daf` |

## 与 Command Code 的对应关系

| 事项 | Command Code 当前实现 | OpenDesign 建议 |
| --- | --- | --- |
| 启用方式 | 显式开启私有兼容 | 保持现有私有兼容入口 |
| 推理配置 | 与私有控制面查询分离 | 保留原 Base URL 与 API Key |
| 鉴权 | API Key；可发现本地 CLI 凭据 | 继续使用独立控制台会话；没有证据证明推理 API Key 能调用账务摘要 |
| 剩余额度 | 服务端 `credits.monthlyCredits` | 服务端 `subscriptionCreditsUsd` |
| 时间窗口 | 5 小时、周窗口 | 保留 5 小时、7 天窗口 |
| 月度百分比 | 无上限时不推算 | 不从套餐价格、钱包合计或周期日期推算 |

本地依据：[CommandCodeUsageClient.cs](../Services/CommandCodeUsageClient.cs)、[CommandCodeAllowanceWindowMethod.cs](../Services/QueryMethods/CommandCodeAllowanceWindowMethod.cs)、[OpenDesignConsoleMethod.cs](../Services/QueryMethods/OpenDesignConsoleMethod.cs)。

## 建议改动范围

1. 将 `opendesign.balance.console` 的钱包能力替换为语义独立的订阅额度方法，例如 `opendesign.subscription-quota.console`。保留 `opendesign.windows.console`，停止请求钱包合计接口。不能仅更换旧余额标题。
2. 为读取器引入明确的资源类型，替换当前仅区分钱包与窗口的 `bool windows`。账务摘要仍在原控制台会话中只读查询。
3. 为余额/额度展示模型增加明确的额度名称，显示“订阅剩余额度”，并保留 USD 单位。当前 Command Code 虽然读取月度剩余，但填入通用 `Balance` 字段后仍由 `BalanceRowViewModel` 显示为“余额”；不能直接复用这一标题。
4. 修改登录完成校验。当前 [App.xaml.cs](../App.xaml.cs) 通过旧钱包方法确认登录；删除钱包方法后必须改为身份/工作空间验证与可用额度读取。不存在订阅额度不应误判成登录失败，也不应阻止窗口额度展示。
5. 同步迁移启用方法配置、已选择来源和缓存。升级后不得把缓存中的钱包总额显示成订阅剩余。
6. 字段缺失、权限不足、身份范围不匹配均按不可用处理；只有服务端明确返回零时才显示零。单项失败应保留其他有效窗口，不得跨账户或工作空间合并。

代码检查还确认，[MainPanel.xaml.cs](../UI/MainPanel.xaml.cs) 的登录入口、[PageEditorViewModel.cs](../UI/PageEditorViewModel.cs) 的兼容方法保存以及 [PageEngine.cs](../Services/PageEngine.cs) 的会话刷新均引用旧方法 ID，应随新方法一并迁移。内存结果缓存键包含方法 ID、实现版本和页面指纹；持久化状态保存候选及来源选择。应使用新方法 ID 并迁移启用列表，不能只修改响应解析而沿用旧语义。

## 实施前仍需验证

- 新账务摘要请求在 Desktop WebView2 中的真实响应、身份范围以及与控制台订阅明细的一致性。本次完成的是官方源码与真实页面核查，没有运行新查询方法。
- 个人工作空间与团队成员的权限差异；团队不得回退为个人额度。
- 月付、年付、无订阅及字段缺失情形。只有服务端直接提供且含义明确的周期字段才用于额度重置展示。
- 旧钱包配置和快照迁移、登录失效、部分数据失败及冷启动后的结果。

本次未修改产品代码、账户配置或发布版本，也未运行新增功能测试。
