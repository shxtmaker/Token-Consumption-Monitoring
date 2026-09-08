# 国内 API、订阅套餐及相关编码平台查询覆盖复核

核实日期：2026-09-08。范围：公开第一方文档、发布公告和官方仓库当前源码。未使用真实用户密钥调用收费或账户接口；下列示例是文档或官方测试数据，以及明确标识的结构示例，不是账户实测结果。

## 结论与实施顺序

1. 立即实现或保留 DeepSeek 余额、Moonshot/Kimi 国内与国际余额。它们有公开 REST 文档、明确认证及字段。
2. Z.ai/智谱 GLM Coding Plan 可实现有限字段的配额查询。其证据是官方文档推荐的官方插件源码，属于第一方公开源码支持，不能称为版本化 REST API 契约。
3. MiniMax 当前 Token Plan 可依据官方 CLI 实现明确的剩余百分比和时间窗口。旧 `coding_plan/remains` 不应替代当前 `token_plan/remains`。计数语义存在新旧差异，不能仅凭 `usage_count` 名称确定已用量。
4. SiliconFlow `/user/info` 已于 2026-08-14 退役，不能因为旧 OpenAPI 仍可访问而新增正式支持。
5. 阿里云、火山、腾讯云、百度云有独立云账单/余额接口；云账户余额不等于某个模型或订阅的剩余额度。火山 Coding Plan Team 与腾讯 TokenHub Token Plan 另有管控面接口，应分别核实并实施。

## 1. DeepSeek：正式公开余额 API

来源：[余额文档](https://api-docs.deepseek.com/zh-cn/api/get-user-balance/)、[基础地址与认证](https://api-docs.deepseek.com/)。

`GET https://api.deepseek.com/user/balance`，`Authorization: Bearer <API Key>`。无请求体。`is_available` 表示 API 账户可用性；`balance_infos` 可以包含多个币种，必须分别保留，不能相加。金额是十进制字符串。

```json
{"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"110.00","granted_balance":"10.00","topped_up_balance":"100.00"}]}
```

`currency` 为 CNY 或 USD；`total_balance` 为可用总额，`granted_balance` 为未过期赠金，`topped_up_balance` 为充值余额。不能把余额差直接当累计费用，也不代表 DeepSeek 聊天产品订阅。历史用量按 Key 导出是控制台功能，公开余额 API 不提供该时间序列。[官方 FAQ](https://api-docs.deepseek.com/faq)

## 2. Moonshot/Kimi：国内与国际正式余额 API

| 项目 | 国内 | 国际 |
|---|---|---|
| 文档 | [Kimi 国内](https://platform.kimi.com/docs/api/balance) | [Kimi 国际](https://platform.kimi.ai/docs/api/balance) |
| 请求 | GET `https://api.moonshot.cn/v1/users/me/balance` | GET `https://api.moonshot.ai/v1/users/me/balance` |
| 币种 | CNY（人民币元） | USD |
| 认证 | Bearer MOONSHOT_API_KEY | Bearer MOONSHOT_API_KEY |

文档明确两站 API Key 完全独立，跨站混用返回 401。旧 `platform.moonshot.cn/docs/api/balance` 已重定向到国内 Kimi 文档；API 域名仍按上表使用。账户区域应作为显式设置，禁止用同一个密钥依次尝试多个区域。

两站文档正文的成功示例：

```json
{"code":0,"data":{"available_balance":49.58894,"voucher_balance":46.58893,"cash_balance":3.00001},"scode":"0x0","status":true}
```

字段是 number；`code=0` 表示成功。页面自动生成的 schema 示例有 `code=123`，实现应遵循字段表与正文 `code=0`，不复制生成器占位值。`cash_balance` 可负；现金负值时 `available_balance` 等于 voucher，而非现金与券简单相加。因此主余额直接采用 available，分项仅展示。`available_balance<=0` 阻止推理，不说明 Kimi 聊天或 Coding 订阅是否可用。

## 3. SiliconFlow：旧 API 已明确停止服务

旧[官方 OpenAPI](https://github.com/siliconflow/siliconcloud/blob/main/openapi.yaml) 曾定义 `GET https://api.siliconflow.cn/v1/user/info`、Bearer、`code=20000/status=true`，余额字段是 `data.balance/chargeBalance/totalBalance` 字符串。

但[官方更新公告](https://docs.siliconflow.cn/docs/release-notes/overview)于 2026-08-11 明确通知：该接口无法适配新账户体系，2026-08-14 正式停止服务；未来适时提供账户替代 API，上线另行公告。本次核查该公告页未发现替代账户 API 的后续发布。结论以更新公告为准：标记 retired，不添加旧方法，不把失败回填成余额 0。旧 schema 只能用于历史兼容证据。

## 4. Z.ai 与智谱：官方插件支持的查询

第一方文档：[Z.ai 用量插件](https://docs.z.ai/devpack/extension/usage-query-plugin)、[智谱用量插件](https://docs.bigmodel.cn/cn/coding-plan/extension/usage-query-plugin)。两者均直接推荐 `zai-org/zai-coding-plugins`。

源码核实版本：`0446d0bb0bc537d97d3ab3664c4b8b9c4a0e1254`。[query-usage.mjs](https://github.com/zai-org/zai-coding-plugins/blob/0446d0bb0bc537d97d3ab3664c4b8b9c4a0e1254/plugins/glm-plan-usage/skills/usage-query-skill/scripts/query-usage.mjs)

| 方法 | 路径 | 参数 |
|---|---|---|
| 配额 | GET `/api/monitor/usage/quota/limit` | 无 |
| 模型统计 | GET `/api/monitor/usage/model-usage` | startTime、endTime |
| 工具统计 | GET `/api/monitor/usage/tool-usage` | startTime、endTime |

正式产品域为 `https://api.z.ai` 与 `https://open.bigmodel.cn`。脚本也识别 dev.bigmodel.cn，但新产品默认设置不应使用开发域。日期采用本地时间 `yyyy-MM-dd HH:mm:ss` 并 URL 编码；示例脚本查询昨天当前整点至今天当前小时末。未看到时区参数，不能声称它是 UTC 统计。

请求头 `Authorization` 直接填写 `ANTHROPIC_AUTH_TOKEN`，源码不自行添加 Bearer；同时发送 `Content-Type: application/json` 和 `Accept-Language: en-US,en`。不要将这一认证处理套用到其他供应商。

源码只明确解释 `json.data.limits[]`：

- `type=TOKENS_LIMIT`：`percentage`，标注为 5 小时 Token usage。
- `type=TIME_LIMIT`：`percentage`、`currentValue`（currentUsage）、`usage`（总额）、`usageDetails`，标注为 MCP 月用量。
- 其他类型保留原值，没有定义周窗口结构；模型和工具统计整体输出 `json.data`，没有字段 schema。

下面仅是据代码构造的解析测试结构，并非官方 API 完整响应样例：

```json
{"data":{"limits":[{"type":"TOKENS_LIMIT","percentage":25},{"type":"TIME_LIMIT","percentage":10,"currentValue":10,"usage":100,"usageDetails":[]}]}}
```

实现应把证据等级标为 `first-party-source`，遇到缺字段返回不可确定。官方插件未校验 percentage 方向的数学公式；不应无证据生成剩余额度、重置时间或从提示次数推导 Token 总量。官方[套餐概述](https://docs.z.ai/devpack/overview)指出存在 5 小时和周限制，这不等于脚本已经定义了周字段。订阅推理使用范围另见[使用规则](https://docs.z.ai/devpack/usage-policy)，读取配额不能据此宣称允许在任意应用中调用订阅模型。

## 5. MiniMax：当前 Token Plan 官方源码可落地

[中国 FAQ](https://platform.minimaxi.com/docs/token-plan/faq)正式公开：

```text
GET https://www.minimaxi.com/v1/token_plan/remains
Authorization: Bearer <API Key>
Content-Type: application/json
```

[国际套餐页](https://platform.minimax.io/subscribe/token-plan)公布对应 `https://www.minimax.io/v1/token_plan/remains`。文档正文未提供完整响应 schema，但官方 `MiniMax-AI/cli` 提供类型、端点和 fixture。核实源码提交：`bfbb4cb75ec343149eaccfd668c5011aa27bcf2b`。

第一方证据：

- [端点选择](https://github.com/MiniMax-AI/cli/blob/bfbb4cb75ec343149eaccfd668c5011aa27bcf2b/src/client/endpoints.ts)：`quotaEndpoint(baseUrl)` 返回 `/v1/token_plan/remains`。
- [类型](https://github.com/MiniMax-AI/cli/blob/bfbb4cb75ec343149eaccfd668c5011aa27bcf2b/src/types/api.ts)：`QuotaResponse.model_remains[]`，含模型、窗口、计数、可选显式剩余百分比、状态和周加成。
- [显示规则](https://github.com/MiniMax-AI/cli/blob/bfbb4cb75ec343149eaccfd668c5011aa27bcf2b/src/output/quota-table.ts)：时间以毫秒处理；周百分比乘 `weekly_boost_permille/1000`，可能超过 100%。
- [计数解析](https://github.com/MiniMax-AI/cli/blob/bfbb4cb75ec343149eaccfd668c5011aa27bcf2b/src/utils/quota.ts)：明确记载 usage_count 新旧语义差异，依据明确的剩余百分比选择解释，无法一致时不返回计数。
- [官方测试 fixture](https://github.com/MiniMax-AI/cli/blob/bfbb4cb75ec343149eaccfd668c5011aa27bcf2b/test/fixtures/quota-response.json)：提供 base_resp 和 model_remains 结构。

以下是该 fixture 中第一项的缩略版：

```json
{"base_resp":{"status_code":0,"status_msg":"success"},"model_remains":[{"model_name":"MiniMax-M*","start_time":1776355200000,"end_time":1776373200000,"remains_time":7151954,"current_interval_total_count":1500,"current_interval_usage_count":228,"current_weekly_total_count":0,"current_weekly_usage_count":0,"weekly_start_time":1776009600000,"weekly_end_time":1776614400000,"weekly_remains_time":248351954}]}
```

当前类型另含可选 `current_interval_remaining_percent`、`current_weekly_remaining_percent`、`current_interval_status`、`current_weekly_status`、`weekly_boost_permille`。状态源码注释为 1 正常、2 耗尽、3 无限；但官方显示代码专门处理双窗口 status=3 且双 total=0 的“不在套餐中”例外。不能把 0/0 显示为 100% 可用，也不能单凭 status=3 宣称无限额度。

建议首轮只读取显式 remaining percent 和确定的 start/end 时间；缺百分比时返回不可确定，不复刻启发式计数推断。保留模型各自窗口，非文本模型可能是日额度；不能全都硬编码成 5 小时。周加成应显示为独立倍率或沿用第一方公式，同时避免用 100 减去最高 150 的百分比后得到负用量。重置倒计时是时长，不是 Unix 时间。

同一官方端点选择代码对 `sk-api-` API key 使用 `{baseUrl}/account/query_balance`。类型声明余额字段 `available_amount/cash_balance/voucher_balance/credit_balance/owed_amount` 为字符串，包含 `base_resp`。本次没有查明区域币种的明确第一方定义，因此这个余额方法应继续待定，不能按域名猜币种后上线。旧 `coding_plan/remains` 本次未找到当前公开 REST 契约，不与 Token Plan 新 API 混称。

## 6. 云平台及其他平台覆盖矩阵

| 提供商/产品 | 已核实查询能力 | 认证与口径边界 | 实施状态 |
|---|---|---|---|
| 阿里云百炼按量 API | BSS `QueryAccountBalance`、`QueryBillOverview`、`QueryInstanceBill`、`QueryResourcePackageInstances` | 云账户 AccessKey 和 BSS 只读权限；不是 DashScope API key。账单存在延迟，不提供单次调用财务明细 | 可规划独立云账单适配器 |
| 阿里云 Coding Plan/Token Plan | 官方产品文档区分请求次数型 Coding Plan 与 Credits 型 Token Plan，提供控制台用量入口 | 订阅 Key、Base URL、账户余额各自独立，不能用 BSS 余额推算套餐额度 | 本次未核实公开配额查询字段契约，待专项 |
| 火山方舟按量 API | `GetInferenceUsage`，旧 `GetUsage` 待弃用；用量明细导出任务 | 管控面用量是模型服务调用量，不等于费用中心金额 | 有公开入口，需进一步读完整 schema |
| 火山 Coding Plan Team | `ListSeatInfoUsages` 官方文档入口 | 席位信息及用量；不能推定覆盖个人 Coding Plan | 可行候选，待签名/分页/字段核实 |
| 火山费用中心 | `QueryBalanceAcct`、`ListBill`、`ListBillDetail` | 云资金账户和账单；应按产品/账号筛选后才能归属方舟 | 可规划云账单适配器 |
| 腾讯混元/TokenHub | TokenHub 管控面说明支持用量统计、Token Plan 企业套餐；`DescribeTokenPlan`；CAM 列出 `DescribeTokenPlanUsage` | TokenPlan 的 Key、用量独立管理，不能通过 TokenHub 全局接口查询；CAM 列表不是完整请求响应契约 | 可行候选，需专门核实 schema |
| 腾讯费用中心 | `DescribeAccountBalance`、`DescribeBillDetail` | 云 API 3.0 签名和财务权限；国内 host billing.tencentcloudapi.com，国际不同；账单不是实时额度 | 可规划独立适配器 |
| 百度千帆/百度云 | 账户余额 `POST https://billing.baidubce.com/v1/finance/cash/balance`，现金余额字段 cashBalance；Finance 提供资源月账单与计费项账单 | AccessKeyID/SecretAccessKey 签名认证，不是推理 Bearer key；云账户现金不等于千帆套餐额度 | 云账单可行；千帆订阅实时配额未核实 |
| OpenCode Zen/Go | Zen 公开模型推理端点；Go 公布 5 小时、周、月美元额度及控制台用量 | Go 可选择额度耗尽后使用按量余额；本地会话统计不能替代账户汇总 | 未找到公开独立账户余额/订阅配额 REST 契约 |
| Command Code | Provider API 正式公开 OpenAI/Anthropic 兼容推理；Studio 展示用量和账单 | 推理响应 usage 可做该次遥测，不是账户查询；不能把 CLI bundle 私有 billing 路径称为公开 API | 账户查询待定；推理遥测已有官方依据 |

矩阵来源：

- 阿里云：[费用中心 API](https://help.aliyun.com/zh/user-center/bill-view)、[Coding Plan FAQ](https://help.aliyun.com/zh/model-studio/coding-plan-faq)、[计费与套餐边界](https://help.aliyun.com/zh/model-studio/bill-query-and-cost-management)。
- 火山：[费用中心 API 总览](https://www.volcengine.com/docs/6269/1165275?lang=zh)、[方舟用量 API 入口](https://www.volcengine.com/docs/82379/1390291?lang=zh)、[ListSeatInfoUsages](https://www.volcengine.com/docs/82379/2286756?lang=zh)。
- 腾讯：[费用 API 总览](https://cloud.tencent.com/document/product/555/19170)、[DescribeBillDetail](https://cloud.tencent.com/document/api/555/19182)、[TokenHub 管控面](https://cloud.tencent.com/document/product/1823/132280)、[混元权限动作](https://cloud.tencent.com/document/product/598/97722)。
- 百度：[账户余额查询](https://cloud.baidu.com/doc/Finance/s/Skhtyytwu)、[Finance 文档目录](https://cloud.baidu.com/doc/Finance/s/5jwvyt0tf)。
- OpenCode：[Zen](https://opencode.ai/docs/zen)、[Go](https://opencode.ai/v2/docs/console/go)。
- Command Code：[Provider API](https://commandcode.ai/docs/provider)、[Studio](https://commandcode.ai/docs/studio)、[套餐与额度](https://commandcode.ai/docs/resources/pricing-limits)。

## 7. 统一模型要求与验证边界

余额、累计费用、Token、调用次数、剩余百分比、订阅 entitlement 应分别保存；来源还需区分公开 REST、官方源码、控制台、单次推理遥测。成功 HTTP 状态不等于业务成功，缺字段不能变成 0。货币使用 decimal 并明确单位；跨货币不自动相加；负现金余额可有业务意义。

建议契约测试覆盖：DeepSeek 多币种、零余额；Moonshot code 错误、负现金且可用余额为券；Z.ai 缺 limits、未知 type、缺 percentage；MiniMax 缺显式百分比、状态例外、周加成、毫秒窗口；SiliconFlow retired 不发旧请求。真实密钥验证与速率限制验证仍未完成。

“未找到”仅表示在上述官方文档、官方仓库和本次检索范围内没有足够契约，不表示服务端绝对不存在接口。云平台候选因完整 schema 尚未逐一核实，不能把本报告的 API 名称直接当作已完成实现规格。
