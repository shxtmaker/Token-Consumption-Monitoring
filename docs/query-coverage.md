# 官方 API 与订阅查询覆盖评估

核验日期：2026-09-08。实现基线：GitHub `main` 的 `ae0c6a9`（1.2.3），查询覆盖扩展随 1.3.0 提供。

## 可行性结论

可以在统一查询架构上持续扩大覆盖，但无法保证所有官方 API、个人订阅和企业套餐都能通过同一种密钥、同一个接口查询。推理协议兼容不代表账务接口兼容；账户余额、历史 Token、实际费用、速率上限和订阅窗口是不同数据。

可稳定扩展的方向是按“来源、能力、凭据范围”注册独立方法。提供商公开接口直接接入；官方客户端或插件公开的数据路径标记为有条件来源；控制台私有路径保留来源标识。没有公开契约、已经退役或字段语义未确认的来源不注册为正式方法。

## 已实现覆盖

本次新增 12 个查询方法，默认注册表由 7 个扩展为 19 个。已有 DeepSeek 余额查询同步改为严格官方地址校验，并保留全部币种。以下方法均有可执行实现，没有占位候选。

| 产品与凭据 | 新增方法 | 返回能力 | 统计边界 |
|---|---|---|---|
| OpenRouter 普通 API Key | `openrouter.key-quota.api-key` | 当前 Key 的限额、剩余及已用额度 | 已用由报告的 limit 与 limit_remaining 相减；不把终身 usage 配到月额度 |
| OpenRouter 普通 API Key | `openrouter.daily-cost.api-key` | 当前 Key 的今日报告费用 | UTC 今日；不加入 BYOK 单独估算额 |
| OpenRouter Management Key | `openrouter.credits.management-key` | 账户 credits 余额 | total_credits 减 total_usage；不是上游供应商账户余额 |
| OpenAI Admin Key | `openai.organization-usage.admin-key` | 组织模型生成 Token、请求数及模型明细 | 最近已完成的 UTC 日；Completions 用量接口不覆盖所有音频、图像等产品计量 |
| OpenAI Admin Key | `openai.organization-cost.admin-key` | 组织报告费用 | 最近已完成的 UTC 日；币种独立保留，可能存在账单延迟 |
| Anthropic Admin Key | `anthropic.organization-usage.admin-key` | 组织 Messages Token 与模型明细 | 最近已完成的 UTC 日；输入、输出、缓存读取及两类缓存写入分别计数 |
| Anthropic Admin Key | `anthropic.organization-cost.admin-key` | 组织报告费用 | 报告的美元美分除以 100 转为 USD；不使用 Token 单价估算 |
| Moonshot 普通 API Key | `moonshot.balance.api-key` | available_balance | 国内 CNY、国际 USD，凭据与地域分开；不把现金与券自行相加替代可用余额 |
| Z.ai／智谱 Coding Plan Key | `zai.coding-plan.windows.api-key` | 已公开的 Token 5 小时、MCP 月用量百分比 | 第一方插件字段；不推断周窗口、重置时间或 Token 绝对量 |
| MiniMax Token Plan Key | `minimax.token-plan.windows.api-key` | 模型当前窗口和周窗口已用百分比、重置时间 | 仅使用显式 remaining_percent；不猜旧 usage_count 的方向，周加成单独说明 |
| 本机 Codex CLI 登录 | `codex.rate-limits.local-session` | 服务端报告的全部 limit ID 及其窗口 | 优先多桶结构；缺失窗口保持未知，不按套餐名硬编码额度 |
| 本机 Codex CLI 登录 | `codex.account-usage.local-session` | 账户累计 Token | lifetimeTokens；明确不是今日用量，也不代表全部 ChatGPT 产品用量 |

接口依据：[OpenRouter Key](https://openrouter.ai/docs/api/api-reference/api-keys/get-current-api-key)、[OpenRouter Credits](https://openrouter.ai/docs/api/api-reference/credits/get-remaining-credits)、[OpenAI Usage](https://developers.openai.com/api/reference/resources/admin/subresources/organization/subresources/usage)、[Anthropic Usage & Cost](https://platform.claude.com/docs/en/manage-claude/usage-cost-api)、[Moonshot 国内](https://platform.kimi.com/docs/api/balance)、[Moonshot 国际](https://platform.kimi.ai/docs/api/balance)、[Z.ai 官方插件](https://docs.z.ai/devpack/extension/usage-query-plugin)、[MiniMax Token Plan](https://platform.minimaxi.com/docs/token-plan/faq)、[MiniMax 官方类型](https://github.com/MiniMax-AI/cli/blob/bfbb4cb75ec343149eaccfd668c5011aa27bcf2b/src/types/api.ts)、[Codex App Server](https://learn.chatgpt.com/docs/app-server)。

## 配置入口

新建页面后选择凭据类型。普通 Key、Admin Key 和 Management Key 不互相尝试。查询权限由接口验证，不能根据密钥前缀推断。管理密钥不参与通用模型列表探测。

| 来源 | Base URL | 凭据类型 |
|---|---|---|
| DeepSeek | `https://api.deepseek.com` | 普通 API Key |
| OpenRouter Key | `https://openrouter.ai/api/v1` | 普通 API Key |
| OpenRouter 账户 credits | `https://openrouter.ai/api/v1` | Management Key |
| OpenAI 组织 | `https://api.openai.com` | 组织 Admin Key |
| Anthropic 组织 | `https://api.anthropic.com` | 组织 Admin Key |
| Moonshot 国内／国际 | `https://api.moonshot.cn/v1` ／ `https://api.moonshot.ai/v1` | 对应地域的普通 API Key |
| Z.ai／智谱 | `https://api.z.ai/api/anthropic` ／ `https://open.bigmodel.cn/api/anthropic` | 普通 API Key，填写相应订阅 Key |
| MiniMax 国内／国际 | `https://www.minimaxi.com` ／ `https://www.minimax.io` | 普通 API Key，填写 Token Plan Key |
| Codex 订阅 | `https://chatgpt.com` | 本机 Codex 登录，无需填写 Key |

一个页面保存一种凭据。同一 OpenRouter 账户若需要 Key 限额和管理 credits，请分别建立页面。组织查询默认覆盖管理密钥授权的组织范围；本版本未提供项目、workspace 或账期筛选，不把组织总量冒充某一普通 Key 的消费。

Codex 需要 `PATH` 中存在 `codex.exe`，并在同一 Windows 用户下完成 CLI 登录。查询通过该 CLI 的 stdio 接口读取服务端状态，不读取或复制登录文件，不创建对话。最低兼容版本尚未由官方文档明确；本机已验证 0.153.4。旧版本不支持方法时会显示明确错误。

MiniMax 旧响应若只有计数而没有显式剩余百分比，显示结构不兼容，不能据此判断套餐耗尽。部分模型没有已核实窗口字段时，只显示有明确数据的窗口。周加成百分比不与基础剩余百分比混算。

## 其他平台的实施边界

| 类别 | 平台或产品 | 结论及后续条件 |
|---|---|---|
| 可行，需要独立身份与资源配置 | Vertex AI、Azure OpenAI／Foundry、AWS Bedrock | 官方云监控和账务 API 可查询；需要 OAuth／ADC、Azure 身份或 AWS IAM，并配置项目、订阅、资源及区域。普通推理 Key 不足 |
| 可行，需要账户／团队管理配置 | GitHub Copilot、Cursor 团队、Windsurf／Devin Desktop 企业、xAI、Mistral 企业、Fireworks | 已核实官方查询入口；需要各自管理权限、账户或团队范围、分页和单位映射。不能外推到所有个人套餐 |
| 可行，需要主动采集 | Claude Code statusline | 官方输出可含 5 小时与 7 天订阅窗口；需要用户配置快照输出。上下文 Token 和本地估计费用不能当累计账单 |
| 云管理接口候选，需完整契约复核 | 阿里云百炼、火山方舟／Coding Plan Team、腾讯混元／TokenHub、百度千帆 | 云账单、账户余额或企业用量入口已找到；签名、只读权限、席位及资源归属不同，尚未实现 |
| 普通 Key 的独立账户查询未证实 | Gemini Developer API、Together、Groq | 可以采集本应用实际请求的 usage；不能从 `/models` 或限流配置推导历史消费 |
| 公开账户查询契约未证实 | OpenCode Go、Command Code 的部分现有路径；部分个人聊天套餐 | 控制台可见不等于公开 API。现有兼容方法继续标记为私有或有条件来源 |
| 已退役 | SiliconFlow `/user/info` | 官方公告说明已于 2026-08-14 停服，旧 OpenAPI 尚存不代表仍可接入；不新增该方法 |

以上“未证实”仅限本次第一方资料检索范围，不表示服务端绝对不存在接口。完整的平台、认证、端点和证据分别见[国际覆盖研究](research/official-coverage-refresh-global.md)与[国内覆盖研究](research/official-coverage-refresh-cn.md)。硅基流动退役状态来自[官方更新公告](https://docs.siliconflow.cn/docs/release-notes/overview)。

下一阶段应先增加账户／团队／项目范围配置，再分别实施 GitHub 与 Cursor 管理数据、云身份签名和 Claude Code 主动采集；这些不作为本版本已支持能力。未经完整字段核验的方法不进入注册表。

## 数据与安全边界

- 新增远程方法只接受对应官方 HTTPS 主机和已知基础路径，拒绝用户信息、查询参数、非标准端口及重定向。
- 凭据只通过匹配类型读取。版本化秘密写入与页面配置保持一致，重新编辑不会降级为普通 Key。
- JSON 缺字段、分页游标重复、越界时间桶和业务错误均不能转换为零值。分页失败不发布已累计的部分总数。
- 金额使用 decimal，Token 使用 64 位整数；未报告的请求数保留为 null。不同币种、来源和窗口不相加。
- UI 显示统计范围和时间，余额保留所有币种。DeepSeek 控制台仍属于私有前端来源，未因来自官网而标记为稳定公开 API。

## 验证

验证步骤、测试范围及真实账户边界见[验证记录](query-coverage-verification.md)。真实凭据未包含在代码、测试、截图或研究文档中。
