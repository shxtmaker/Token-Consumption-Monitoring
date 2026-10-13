# Command Code 剩余额度、模型倍率与共享池

核查日期：2026-10-13。证据范围为官方公开文档、当前 Studio 前端及仓库客户端。本文不代表真实账户扣费实验；本次没有读取凭据或发送推理请求。

## 结论

新 Go、GOAT、新 Pro 可以在账户套餐身份和模型规则确认后，将订阅共享池剩余换算成所选模型的等值剩余。参考套餐的手工选择不能代替账户 `planId`。旧 Pro、Max 的类别额度不能仅用总余额按比例拆分。购入额度、免费额度不得直接混入订阅换算。

## 官方账户字段及桥接边界

当前 [Studio 查询模块](https://commandcode.ai/assets/use-billing-data-C3jbNBE_.js) 将 `credits.monthlyCredits` 作为月度剩余，读取可选 `credits.monthlyCreditsGranted`，并原样传递 `windowLimits`；购入余额是单独的 `credits.purchasedCredits`。[Billing 页面模块](https://commandcode.ai/assets/billing-CJFuf1ip.js) 用订阅 `planId` 精确查找套餐，并要求订阅状态在允许集合内才认作有效套餐。

[月度用量模块](https://commandcode.ai/assets/monthly-usage-meter-uVz22ql0.js) 的计量逻辑为：有效 `monthlyCreditsGranted` 存在时，月总量取它与 `tier.limits.totalCredits` 的较大值；否则个人套餐使用 `tier.limits.totalCredits`，组织套餐另乘席位。剩余量直接使用 `monthlyCredits`，已用量由总量减剩余量得到。此算法证明字段含义，但页面月度进度条总量不是模型倍率的通用分母。

Studio 使用 `/internal/billing/credits` 和 `/internal/billing/subscriptions`，见[官方常量模块](https://commandcode.ai/assets/constants-BTep9Kyp.js)。仓库客户端使用 `/alpha/billing/credits` 与 `/alpha/billing/subscriptions`。两组接口不能声称已经通过网络逐值比对；实际集成需核对 alpha 返回的 `planId/status/monthlyCredits/windowLimits`。缺失 `monthlyCreditsGranted` 不等于零，也不妨碍已明确识别的个人套餐采用官方标准池单位。

## 精确套餐标识

以下值来自当前[官方套餐模块](https://commandcode.ai/assets/plan-tiers-C6rSbAtm.js)与[官方常量模块](https://commandcode.ai/assets/constants-BTep9Kyp.js)。

| planId | 含义 | 标准月池 | 可直接采用当前模型比例 |
| --- | --- | ---: | --- |
| `individual-go-v1` | 新 Go | 10 | 是 |
| `individual-goat` | GOAT | 70 | 是 |
| `individual-pro-v1` | 新 Pro | 80 | 是 |
| `individual-go` | 旧 Go | 10 | 否；旧周期为固定额度 |
| `individual-pro` | 旧 Pro | 30，premium 15 + opensource 15 | 否 |
| `individual-max` | Max 10× | 150，premium 100 + opensource 50 | 否；总剩余不足以证明 premium 剩余 |
| `individual-ultra` | Max 20× | 300，premium 200 + opensource 100 | 否；同上 |
| `individual-provider` | Provider | 模块存在 15 的参考值 | 否；官网定义为按量付费，不可当共享订阅池 |
| `teams-pro` | Teams Pro | 40/席位 | 否；还需席位与组织限制证据 |

旧 Go 在续费时迁移到按模型额度，不能只看当前日期或套餐显示名称认定已迁移。[Go 官方说明](https://commandcode.ai/docs/plans/go#how-go-works)

## 已有依据的公式

[Usage Limits 官方说明](https://commandcode.ai/docs/resources/usage-limits#limits-by-plan)明确：Go、GOAT、Pro 的 credits 是 usage-value 单位；低额度模型每美元用量消耗更多 credits。模型混用共享月池，五小时和周窗口使用相同的 credits 单位。购入额度不计入窗口。

设 `B` 为上表确认的标准共享池，`A(m,t)` 为官网在时间 `t` 对模型 `m` 生效的整月计费价格等值预算，`R` 为账户月度剩余 credits，`Rw` 为服务端某窗口直接返回的剩余 credits：

```text
模型倍率 k = A(m,t) / B
模型月度等值剩余（估算） = max(0,R) × k
模型窗口等值剩余（估算） = max(0,Rw) × k
当前订阅可用等值（估算） = max(0,min(R,R5h,Rweek)) × k
```

最后一式仅在三个字段均可信、适用该账户且没有额外组织限制时成立。窗口重置后可用量会变化，所以窗口结果不能冒充月度余额。窗口缺失时不要假设无限；保留月度估算并明确窗口未知。

官网计算器[价格页面](https://commandcode.ai/docs/resources/pricing-limits#usage-estimates)与其[计算模块](https://commandcode.ai/docs/_next/static/chunks/9761-f98d96885f75751e.js)使用 `planBudgetUsd` 作为除以请求成本的预算，并通过 `planBudgetSteps` 选生效规则。`planAllowanceUsd` 可能表达折扣前宣传等值，不应与已折扣单价再叠加折扣。免费模型不能通过除以零生成无限额度；峰谷、上下文阶梯及 ZDR 也不等同固定请求次数。

## 证据不足与实现限制

1. Max 文档说明 standard 与 premium 的上限不同，且 billing category 与营销分类不同。总剩余乘 `100/150` 无法证明 premium 剩余；需要类别余额或类别已用量。[Max 官方说明](https://commandcode.ai/docs/plans/max#how-credits-work)
2. Usage Limits 同页对 Go 五小时/周上限存在矛盾：表格与 FAQ 写 2/5，正文写 3/6。实现应使用服务端窗口值，不能硬编码或由窗口上限反推套餐。
3. 官方公开页面与脚本会在活动期变化。当前 Studio 表中某些 GOAT 模型额度已不同于上一轮保存文档。应实时同步、精确匹配模型 ID、应用规则时间；不能拼接不同时间快照后声称已确认倍率。
4. 当前公开资料没有给出 alpha 接口稳定契约或所有旧订阅/赠送额度的归一化规则。遇到未知套餐、过期订阅、异常授予量、未知模型及不支持的规则，应停止换算，保留原始账户读数。

## 留存材料

公开脚本保存在 `artifacts/commandcode-remaining-research/`，包括 `plan-tiers-C6rSbAtm.js`、`constants-BTep9Kyp.js`、`use-billing-data-C3jbNBE_.js`、`monthly-usage-meter-uVz22ql0.js` 和 `billing-CJFuf1ip.js`。这些文件仅作为研究证据，不执行其代码，不作为固定 hash 的长期生产依赖。
