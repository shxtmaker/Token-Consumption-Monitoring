# 所选模型额度实施与验证

日期：2026-10-13。基线：v1.3.4。状态：本地实现、只读账户验证和本地更新完成，用户已确认本地验收通过。

## 已实现行为

页面编辑器以“额度估算模型”和“参考套餐”替代旧模型列表，按页面保存并恢复选择。Base URL、API Key、协议、凭据引用和已有模型提示保持兼容。选择仅用于显示额度，不更改推理请求的模型。

主面板和浮窗显示官网月度参考及独立的剩余额度估算。OpenDesign 钱包查询已从默认注册和显示中移除；Design Plan 窗口百分比继续显示。估算不写入实际余额，不影响连接状态、来源选择或告警。各模型共用额度，各窗口同时限制使用，不能相加。

公开目录通过固定官网源的无鉴权 HTTPS GET 同步。资源文件名从当前官网引用中发现，不固定构建 hash，不执行下载的 JavaScript。公开缓存最长使用 24 小时；已知定价变更时间提前截断换算有效期。规则缺失时保留月度参考，停止剩余换算。

## 百分比优先规则

已有明确百分比的额度直接沿用原窗口百分比，不再换算对应的美元剩余，也不参与美元可用量汇总。只有没有百分比的额度才进入估算，并继续校验套餐、账期、单位和数据有效期。Command Code 已补齐官网口径的月度限额百分比；取得月度百分比后隐藏月度 credits 金额及模型美元剩余估算。确实无法取得百分比时才进入保守回退；OpenDesign 已有百分比的窗口不再显示美元剩余。官网整周期参考额度与剩余额度分开。

此规则覆盖下文此前验证过的换算展示。此前真实账户金额只作为算法验证记录，不代表当前界面会继续显示这些金额。最新渲染图位于 `artifacts/model-quota-verification/monthly-percent-ui/`。

## Command Code 月度限额修正

用户提供的用量页截图包含每月限额已用 41%、10 月 19 日重置。此前“Command Code 没有月度百分比”的结论不正确：账户 API 适配器只保留了月度剩余 credits，遗漏了官网的月度进度条口径。

当前 `/alpha/billing/credits` 没有直接返回月度百分比字段。官网公开 `monthly-usage-meter` 组件使用账户的 `monthlyCredits`、可选 `monthlyCreditsGranted`、精确套餐的 `limits.totalCredits`、席位数及订阅状态生成百分比。现已按同一规则归一化，直接报告值与规则计算值通过 `PercentIsDirectlyReported` 区分，不将计算结果声称为接口直接报告。

规则不使用所选模型预算或美元价值倍率。有效授予量取其与套餐总量的较大值；没有授予量时，团队套餐按有效席位数计算。百分比使用与官网一致的非负裁剪和四舍五入，重置时间取实际账期结束。未知套餐、缺失账务值、无效账期或规则不可用时不生成伪造百分比。

本次通过真实账户只读验证得到 41%、2026-10-19 重置，与用户截图一致。新月度限额行与 5 小时、周窗口一起显示；选择不同估算模型不会改变月度百分比，也不再额外显示美元剩余。公开套餐规则单独缓存，查询不携带 API 密钥，且不依赖用户是否选择估算模型。

可复验的官网资源入口：[登录页公开资源](https://commandcode.ai/signin)。本次源码证据保存在 `artifacts/commandcode-remaining-research/monthly-usage-meter-current.js` 和 `subscription-card-current.js`。用户提供的登录页面在内置浏览器中未登录；未将该浏览器页面声称为此次实时读取证据，实时数值来自已配置 API 凭据。

## 换算规则及适用边界

### Command Code

当前支持 `individual-go-v1`、`individual-goat`、`individual-pro-v1` 的单席位有效订阅。使用精确套餐 ID，核对账期、窗口限制和可选月度授予量。旧套餐、Max 分池、团队、Provider 和无法核对的组合不换算。

官网计算器的 `planBudgetUsd` 表示模型美元用量预算；`planAllowanceUsd` 用于核对官网展示额度，不作为重复折扣。按 `planBudgetSteps` 选择当前有效预算，下一变更时间作为失效边界。标准共享池取官网套餐资源的 `totalCredits`，并要求其等于 `opensourceCredits` 且 `premiumCredits` 为零。

```text
模型剩余美元用量价值 = 账户共享池剩余 × 当前模型预算 / 标准共享池
当前套餐可用 = min(月度剩余, 5 小时剩余, 周剩余) × 同一模型倍率
```

真实接口的窗口目前返回 `cap`、`used`、`resetAt`，未返回 `remaining` 时使用 `max(0, cap-used)`。只有显式 `used=0` 且 `resetAt=0` 的未开启窗口才不设置倒计时；其他已过期时间仍阻止估算。没有用固定套餐上限替代账户返回的窗口上限。

购买和赠送 credits 不加入套餐池。显示金额是所选模型的美元用量价值估算，不是钱包余额或已发生消费。

依据：[使用限制及共享计费](https://commandcode.ai/docs/resources/usage-limits)、[模型预算计算器](https://commandcode.ai/docs/resources/pricing-limits)、[公开套餐资源入口](https://commandcode.ai/signin)。

### OpenDesign

读取工作空间成员 Design Plan 的 `tier`、`durationSeconds`、`limitCredits` 和 `remainingCredits`。选择估算模型后，额外读取账务摘要中的 `creditsPerUsd` 单位元数据；不读取该摘要中的钱包金额用于估算。

```text
模型窗口剩余美元用量价值 = remainingCredits / creditsPerUsd / 模型扣减倍率
当前套餐可用 = 所有必要窗口换算结果的最小值
```

Go 需要 5 小时及 7 天窗口；其他已核对套餐使用 7 天窗口。公开月度参考与倍率必须能够还原一致的套餐基准额度。工作空间、真实套餐与参考套餐必须一致。没有真实月度计数器，因此不显示 OpenDesign 月度剩余估算。

依据：[Model Credits](https://open-design.ai/model-credits/)、[价格表](https://open-design.ai/pricing/)、[官方窗口契约](https://github.com/nexu-io/open-design/blob/main/specs/current/coding-plan-client-billing.md)。

## 验证结果

| 项目 | 结果 | 证据范围 |
| --- | --- | --- |
| 自动化测试 | PASS，265/265 | 时间边界、套餐不匹配、过期/缺失/零值、单位换算、窗口最小值、配置保存与迟到响应隔离 |
| WPF 渲染及交互 | PASS | 测试数据；模型/套餐保存恢复、凭据保留、620 DIP 表单、290 DIP 浮窗和主面板 |
| 公开目录同步 | PASS | OpenDesign：12 模型、48 参考组合、48 换算规则；Command Code：90 模型、342 参考组合、162 换算规则 |
| Command Code 实际账户 | PASS | 已配置凭据只读查询；真实 GOAT、active、quantity=1；月度百分比 41%、10 月 19 日重置与用户截图一致；两个模型均不再显示美元剩余 |
| OpenDesign 实际账户 | PASS | 既有隔离测试配置中的已登录会话；真实 Go，creditsPerUsd=10000，5 小时剩余 50000，7 天剩余 150000 |
| 正在运行的主配置并发测试 | BLOCKED | 主程序占用 WebView2 配置目录；未终止进程或复制配置。以上 OpenDesign PASS 使用独立测试配置 |
| 付费推理及实际扣费对照实验 | NOT_RUN | 未发起推理、充值或其他账务写入 |
| 本地程序更新 | PASS | 已备份旧程序并更新，安装文件与预览构建哈希一致，页面和设置文件保持不变 |
| 安装后桌面验收 | PASS（用户确认） | 自动界面工具无法定位悬浮窗；随后用户明确确认“本地验收无问题” |
| 版本 Release 发布 | NOT_RUN | 本次未创建版本标签或 Release |

OpenDesign 本次真实满额窗口的换算结果：DeepSeek V4.1 Flash 为 5 小时 $10、7 天 $30、当前套餐可用 $10；GPT-6 Luna 为 $1.25、$3.75、$1.25。这些结果证明读取与换算链路，不能替代实际扣费实验。

Command Code 的诊断输出只保留字段名、套餐状态与验证结果，不记录密钥、账户标识或实际余额。公开目录结果保存于 `artifacts/model-quota-verification/*-live.json`；渲染图保存于 `artifacts/model-quota-verification/monthly-percent-ui/`。渲染图使用测试数据，不能作为真实桌面会话验收证据。

## 复验与本地交付

```powershell
dotnet test tests/TokenConsumptionMonitoring.Tests/TokenConsumptionMonitoring.Tests.csproj -c Release
dotnet run --project tests/OfficialQuerySmoke/OfficialQuerySmoke.csproj -c Release -- artifacts/model-quota-verification/monthly-percent-ui
dotnet run --project tests/OfficialQuerySmoke/OfficialQuerySmoke.csproj -c Release -- --live-quota-reference
dotnet run --project tests/OfficialQuerySmoke/OfficialQuerySmoke.csproj -c Release -- --verify-quota-account
dotnet run --project tests/OfficialQuerySmoke/OfficialQuerySmoke.csproj -c Release -- --verify-opendesign-pool --isolated-live-profile
dotnet publish TokenConsumptionMonitoring.csproj -c Release -o artifacts/model-remaining-preview
```

预览包：`artifacts/TokenConsumptionMonitoring-ModelRemaining-Preview.zip`。散列值保存在同目录的 `.sha256` 文件。预览程序使用产品现有的数据目录和凭据存储；运行前应从托盘退出已安装实例。
