# 国际官方 API 与订阅查询覆盖复核

核验日期：2026-09-08。范围：官方文档与本机官方 Codex CLI 生成的协议。研究结论不表示已用真实服务凭据验收。旧研究仅用于定位来源；下面链接均在本次实际打开。未找到公开契约标为“未证实”，不推断服务商永远不提供该能力。

## 结论与实施顺序

不宜承诺“一个查询方法适配所有套餐”。可行方案是分别提供历史用量、费用、余额、限额快照、订阅窗口和本地客户端数据。第一批可以实现 OpenRouter key/credits、OpenAI 组织 usage/cost、Anthropic 组织 usage/cost，以及 Codex 官方 app-server 只读查询。管理凭据应显式配置，不能把普通推理 key 的失败解释成零用量。第二批可实施 GitHub 个人与组织账务、Cursor 团队账务；云监控及其他企业管理接口需要各自的权限配置和样本验收。

## 覆盖矩阵

| 平台及账户范围 | 已核实读取路径 | 凭据/条件 | 适配判断与边界 |
| --- | --- | --- | --- |
| OpenAI API 组织 | `/v1/organization/usage/completions`、`/v1/organization/costs` | Admin key | 可立即实施；组织 API 统计，不是 ChatGPT 订阅余额。[Usage](https://developers.openai.com/api/reference/resources/admin/subresources/organization/subresources/usage) |
| ChatGPT/Codex 服务账户 | app-server `account/rateLimits/read`、`account/usage/read` | 已登录 Codex 服务的本地 CLI | 可实施官方客户端数据源；API-key-only 不能查账户 token 活动；不是全部 ChatGPT 产品用量保证。[App Server](https://learn.chatgpt.com/docs/app-server) |
| Anthropic Console 组织 | messages usage report、cost report | Admin API 凭据；workspace key 不可用 | 可立即实施；个人账户不可用，Claude Enterprise 有另一 Analytics API，不能混用。[Usage and Cost](https://platform.claude.com/docs/en/manage-claude/usage-cost-api) |
| Claude Code 订阅 | statusline `rate_limits.five_hour/seven_day` | 本地 Claude Code 运行并输出快照 | 官方采集面可行；上下文 token 与本地估计费用应单列，不能称账单。[Statusline](https://code.claude.com/docs/en/statusline) |
| Gemini Developer API | AI Studio usage/billing；真实请求 usageMetadata | API key；账务另属 Cloud Billing | 普通 key 独立历史/余额 API 未证实；付费账户还区分 Prepay/Postpay。[Billing](https://ai.google.dev/gemini-api/docs/billing) |
| Vertex AI | Cloud Monitoring `projects.timeSeries.list` | GCP OAuth/ADC 与 Monitoring 权限 | 可实施云项目源，不能当 Gemini key 余额。token_count 指标为 Beta。[Metrics](https://docs.cloud.google.com/monitoring/api/metrics_gcp_a_b)、[Query](https://docs.cloud.google.com/monitoring/api/ref_v3/rest/v3/projects.timeSeries/list) |
| Azure OpenAI / Foundry | Azure Monitor；Cost Management Query | Azure 身份、资源/订阅读取权限 | 可实施资源指标与账务双源，API推理key不代替管理身份。[Monitor](https://learn.microsoft.com/en-us/azure/foundry-classic/openai/how-to/monitor-openai)、[Cost Query](https://learn.microsoft.com/en-us/rest/api/cost-management/query/usage?view=rest-cost-management-2025-03-01) |
| AWS Bedrock | CloudWatch metrics；Cost Explorer GetCostAndUsage | AWS IAM、相应查询权限 | 可实施账号/区域源，统计与账务分开。运行时存在 bedrock-runtime 与 bedrock-mantle 两种监控路径。[Metrics](https://docs.aws.amazon.com/bedrock/latest/userguide/monitoring-runtime-metrics.html)、[Cost](https://docs.aws.amazon.com/aws-cost-management/latest/APIReference/API_GetCostAndUsage.html) |
| GitHub Copilot | 用户/组织 AI credit usage 与旧 premium request usage | PAT/App 权限按账户级别 | 可实施；个人付费与组织代付分开，credits、requests、tokens不可相加。[Billing usage](https://docs.github.com/en/rest/billing/usage) |
| Cursor 团队/组织 | `/teams/spend`、`/teams/filtered-usage-events`；组织API另列 | Team/Organization Admin API key | 可实施管理源；普通个人订阅剩余额度读取未在此管理契约证实。[Admin API](https://prod.cursor.com/docs/account/teams/admin-api)、[Organization API](https://prod.cursor.com/docs/account/organizations/organization-admin-api) |
| Windsurf / Devin Desktop | `/Analytics`、`/CascadeAnalytics`、`/GetTeamCreditBalance` | Enterprise service key；权限分端点 | 可实施企业源。旧 Windsurf 官方文档现重定向到 Devin Desktop；不可硬编码旧品牌与个人套餐。[API introduction](https://docs.devin.ai/desktop/accounts/api-reference/api-introduction)、[Balance](https://docs.devin.ai/desktop/accounts/api-reference/get-team-credit-balance) |
| OpenRouter | `/api/v1/key`、`/api/v1/credits` | 普通key / management key | 可立即实施，范围属于 OpenRouter 而非上游模型商账单。[Key](https://openrouter.ai/docs/api/api-reference/api-keys/get-current-api-key)、[Credits](https://openrouter.ai/docs/api/api-reference/credits/get-remaining-credits) |
| xAI API 团队 | Management billing prepaid balance、usage | management key、team_id | 可实施团队源；Grok个人订阅剩余额度公开接口未证实。[Billing](https://docs.x.ai/developers/rest-api-reference/management/billing) |
| Mistral 企业 | `/v1/admin/usage`、`spend-limit`、`rate-limit` | Backoffice Admin key，Enterprise/Preview | 有条件可行，le Chat/Vibe analytics与API账务分开。[Overview](https://docs.mistral.ai/admin/admin-api/overview)、[Usage](https://docs.mistral.ai/admin/admin-api/usage-metrics) |
| Fireworks | `POST /v1/accounts/{account_id}/usageCosts:query` | 账户管理员或本人 SELF scope | 可实施；rated cost排除credits、税等，不保证等于发票。[Usage costs](https://docs.fireworks.ai/accounts/exporting-usage-costs) |
| Together | 官方 billing dashboard | 普通推理key只支持请求遥测 | 独立历史/余额公开查询契约未证实，不能以/models代替。[Usage limits](https://docs.together.ai/docs/billing-usage-limits) |
| Groq | 官方推理响应 usage | 普通key | 已阅API reference未证实独立历史/余额资源；只应采集真实请求。[API reference](https://console.groq.com/docs/api-reference) |

## OpenRouter 最小实现契约

`GET https://openrouter.ai/api/v1/key`，`Authorization: Bearer <key>`，无分页。读取 `data`：`usage`、`usage_daily`、`usage_weekly`、`usage_monthly`、`limit`、`limit_remaining`、`limit_reset`、`include_byok_in_limit`、`byok_usage*`、`is_management_key`。这些是消费金额/credits，不是 token。[Key](https://openrouter.ai/docs/api/api-reference/api-keys/get-current-api-key)

`usage` 是累计；daily 是当前 UTC 日、weekly 是从周一开始的 UTC 周、monthly 是当前 UTC 月。`limit` 与 `limit_remaining` 为 null 表示不限额。`rate_limit` 已弃用。`include_byok_in_limit` 控制外部 BYOK 是否计入 key 上限。[Limits](https://openrouter.ai/docs/api_reference/limits)

实现建议：优先显示服务端 `limit_remaining`；需要已用限额时，在两字段有效时推导 `limit - limit_remaining` 并注明限额口径。不要把累计 `usage` 与月限额配对。如果缺字段则显示未知；若实现降级计算，应按 reset 选择同周期用量，处理 BYOK，明确为推导。未知 reset 值不得当作 monthly。服务端未给出精确重置时间时，不伪造服务器时间。

`GET https://openrouter.ai/api/v1/credits` 使用 management key；无分页：

```json
{"data":{"total_credits":100.5,"total_usage":25.75}}
```

本地差值 74.75 是推导账户余额，区别于单 key 的 limit_remaining。403 可能明确表示非 management key，不能转成余额0。[Credits](https://openrouter.ai/docs/api/api-reference/credits/get-remaining-credits)

## OpenAI Admin 最小实现契约

组织管理使用专门 Admin key；示例为 `Authorization: Bearer <OPENAI_ADMIN_KEY>`。[Administration](https://developers.openai.com/api/reference/administration/overview)

### 用量

`GET https://api.openai.com/v1/organization/usage/completions`：`start_time` 必填，Unix秒且包含；`end_time` 可选且排除；`bucket_width=1d`。日桶 limit 默认7、最大31，小时24/168，分钟60/1440。`page` 传上页 `next_page`。可按 project/user/key/model/batch/service_tier分组。读取 `data[].results[].input_tokens`、`output_tokens`、`num_model_requests`。输入已包含缓存读取与写入，禁止再加 `input_cached_tokens`。[Completions](https://developers.openai.com/api/reference/resources/admin/subresources/organization/subresources/usage/methods/completions)

### 费用

`GET https://api.openai.com/v1/organization/costs`：同样 Unix时间；仅日桶；limit默认7、最大180。可按 `project_id`、`line_item`、`api_key_id`分组。费用读取 `amount.value` 和 `amount.currency`，不要按token单价重算。响应保留币种。[Costs](https://developers.openai.com/api/reference/resources/admin/subresources/organization/subresources/usage/methods/costs)

下例是实现用的合成最小结构，数值不是实际账户数据：

```json
{"object":"page","data":[{"object":"bucket","start_time":1788825600,"end_time":1788912000,"results":[{"object":"organization.costs.result","amount":{"value":0.06,"currency":"usd"},"line_item":null,"project_id":null}]}],"has_more":false,"next_page":null}
```

实现建议：统计锁定同一个起止范围直到翻页结束。当前实现使用最近已完成的 UTC 日，并校验服务端桶边界。`has_more=true` 却无 cursor、cursor重复、页数超限、非数值费用均应报不完整/契约错误；空结果表示没有返回数据，只有显式零值才显示为零；缺 data 为结构错误。查询费用与completions token分开，因为非聊天服务可能产生成本。

## Anthropic Admin 最小实现契约

Console 组织可使用 Admin key、`org:admin` OAuth，或未限制于workspace的personal/service account key；workspace key不可用。Admin API不适用于个人账户。Claude Enterprise 使用不同 Analytics key/API。官方特别说明 Claude Platform on AWS 目前没有本页程序化端点。常规key header：`x-api-key` 与 `anthropic-version: 2023-06-01`；推荐识别集成的User-Agent。费用以美元美分十进制字符串表示。[Guide](https://platform.claude.com/docs/en/manage-claude/usage-cost-api)

用量：`GET https://api.anthropic.com/v1/organizations/usage_report/messages`。`starting_at` RFC3339必填，桶按UTC分钟/小时/日对齐；`ending_at` RFC3339可选。日桶最大31，小时168，分钟1440。数组HTTP参数使用 `group_by[]=model` 等形式；可过滤 `api_key_ids[]`、`workspace_ids[]`。返回 `data[].starting_at/ending_at/results[]`、`has_more`、`next_page`。输入总量应组合下列非重叠字段。[Messages report](https://platform.claude.com/docs/en/api/admin/usage_report/retrieve_messages)

```json
{"data":[{"starting_at":"2026-09-08T00:00:00Z","ending_at":"2026-09-09T00:00:00Z","results":[{"uncached_input_tokens":1500,"cache_read_input_tokens":200,"cache_creation":{"ephemeral_1h_input_tokens":1000,"ephemeral_5m_input_tokens":500},"output_tokens":500}]}],"has_more":false,"next_page":null}
```

费用：`GET https://api.anthropic.com/v1/organizations/cost_report`，同一日期字段，仅日桶，limit默认7、最大31，`group_by[]`支持description/workspace_id。当前查询参考没有workspace_ids过滤参数，不应照抄usage参数。分页使用page=next_page。[Cost report](https://platform.claude.com/docs/en/api/admin/cost_report/retrieve)

```json
{"data":[{"starting_at":"2026-09-08T00:00:00Z","ending_at":"2026-09-09T00:00:00Z","results":[{"amount":"123.78912","currency":"USD","description":null,"workspace_id":null}]}],"has_more":false,"next_page":null}
```

以上合成费用样例是 USD 1.2378912，使用 decimal 除100，不使用整数截断。输入样例总量3200，输出500，总token3700。当前日可能尚未完成，客户端应标明UTC时间桶与抓取时间，不假装账务实时完整。可缺的维度不是错误，核心统计字段缺失则需区分未知与零。

## Codex 官方客户端只读查询

官方推荐 `codex app-server` 默认stdio，逐行JSON；先initialize并收到响应，再发送initialized，然后调用账户方法。没有必要创建任务或开始推理。文档提供版本相关schema生成命令；未标明两个读取方法的最低引入版本。[App Server](https://learn.chatgpt.com/docs/app-server)

```jsonl
{"id":0,"method":"initialize","params":{"clientInfo":{"name":"token_consumption_monitoring","title":"Token Consumption Monitoring","version":"1.2.3"}}}
{"method":"initialized","params":{}}
{"id":1,"method":"account/rateLimits/read"}
{"id":2,"method":"account/usage/read"}
```

本地核验：`codex --version`为`codex-cli 0.153.4`；执行`codex app-server generate-ts --out research-global/codex-schema`成功。默认生成的stable ClientRequest包含上述两读取方法，因此此版本不需experimentalApi。此验证仅生成schema，没有执行账户查询、登录或推理。旧版本应通过“方法不存在/需要能力”的错误降级，而不是声称固定最低版本。

本地生成类型中的可用字段：

```text
GetAccountRateLimitsResponse:
  rateLimits: RateLimitSnapshot
  rateLimitsByLimitId: map<string, RateLimitSnapshot> | null
  rateLimitResetCredits: summary | null
  accountId: string | null
  rateLimitUpsell: JSON | null
RateLimitSnapshot:
  limitId, limitName: string | null
  primary, secondary: RateLimitWindow | null
  credits: { hasCredits: boolean, unlimited: boolean, balance: string | null } | null
  individualLimit: { limit: string, used: string, remainingPercent: number, resetsAt: number } | null
  spendControlReached, planType, rateLimitReachedType: nullable
RateLimitWindow:
  usedPercent: number
  windowDurationMins: number | null
  resetsAt: number | null
GetAccountTokenUsageResponse:
  summary: { lifetimeTokens, peakDailyTokens, longestRunningTurnSec,
             currentStreakDays, longestStreakDays } (values nullable)
  dailyUsageBuckets: [{ startDate: string, tokens: bigint }] | null
  threadUsage?: nullable
```

实现建议：优先非空rateLimitsByLimitId，旧rateLimits仅fallback，避免重复统计；resetsAt为Unix秒；usedPercent是已用不是剩余。不要把不同窗口相加。余额字符串未明确币种时，不加USD标签。usage调用不传threadId；传入会改变为线程估计用量。协议响应需按id路由，忽略非目标通知；关闭本次启动的子进程并处理超时，避免残留后台进程。用本机登录态应先判断可用性，不自动登入或退出用户账户。

## 第二批管理接口精确入口

### GitHub Copilot

`GET https://api.github.com/users/{username}/settings/billing/ai_credit/usage`；旧计费另用`premium_request/usage`。`Authorization: Bearer`、`Accept: application/vnd.github+json`、`X-GitHub-Api-Version: 2026-03-10`。个人fine-grained PAT需要Plan用户read；组织对应`/organizations/{org}/settings/billing/ai_credit/usage`需要Administration组织read。查询year/month/day/model/product；最近24个月。参考未列分页参数。[Billing usage](https://docs.github.com/en/rest/billing/usage)

合成结构：`{timePeriod:{year:2026,month:9},user:"name",usageItems:[{product:"Copilot AI Credits",sku:"AI Credit",model:"model",unitType:"ai-credits",grossQuantity:100,netQuantity:100,grossAmount:1,netAmount:1}]}`。组织代付不计入个人报表。实现应保留unitType和gross/net区分；这不是套餐剩余量。用户credits与旧requests应分别配置，不能猜测转换率。

### Cursor

`POST https://api.cursor.com/teams/spend`，Basic认证用户名为API key、空密码；body可为`{"page":1,"pageSize":100}`，递增到totalPages。返回`teamMemberSpend[]`、`subscriptionCycleStart`（Unix毫秒）、`totalMembers`、`totalPages`。`spendCents`仅超额消费，`overallSpendCents`包含套餐内用量，两者为小数美分；`monthlyLimitDollars`可null，`effectivePerUserLimitDollars`是限额而非余额。事件端点用`chargedCents`与spend对账。[Admin API](https://prod.cursor.com/docs/account/teams/admin-api)

### 企业及云侧后续条件

- xAI：`https://management-api.x.ai/v1/billing/teams/{team_id}/prepaid/balance` 与 `POST .../usage`；usage body是analyticsRequest，包括timeRange/timeUnit/values/groupBy/filters；返回timeSeries与limitReached，需要验证聚合维度截断。[Billing](https://docs.x.ai/developers/rest-api-reference/management/billing)
- Mistral：`GET https://api.mistral.ai/v1/admin/usage?month=9&year=2026`，`x-api-key`，category/currency/period分别保留。Enterprise Preview 的可获得性和变更风险应在配置中明确。[Usage](https://docs.mistral.ai/admin/admin-api/usage-metrics)
- Fireworks：cost查询与CSV导出对应，SELF和账户管理员范围不同；CLI每次最多31天，最早100天前，金额为rated subtotal。[Export](https://docs.fireworks.ai/accounts/exporting-usage-costs)
- Vertex：查询`aiplatform.googleapis.com/publisher/online_serving/token_count`的DELTA点并按时间聚合，不能把distribution误作累计计数。[Metrics](https://docs.cloud.google.com/monitoring/api/metrics_gcp_a_b)
- Azure Cost Query是POST只读查询，需处理properties.columns/rows与nextLink，不能假设固定列序；云账务资源范围应由用户选定。[Query](https://learn.microsoft.com/en-us/rest/api/cost-management/query/usage?view=rest-cost-management-2025-03-01)
- AWS GetCostAndUsage包含TimePeriod、Granularity、Metrics、Filter、GroupBy、NextPageToken；ResultsByTime有Estimated，须保留估算状态。[Cost Explorer](https://docs.aws.amazon.com/aws-cost-management/latest/APIReference/API_GetCostAndUsage.html)

## 验证边界

查询成功只说明对应权限与统计面可用。401/403、未知字段、分页异常应形成明确错误；不能改为“0”。推理usage只覆盖真实捕获的请求；本地token估计、服务端费用、预算上限、订阅百分比分开显示。跨来源汇总须校验账户、币种、时间窗口和统计归属，防止同一消费在客户端、网关和上游账务重复计入。

