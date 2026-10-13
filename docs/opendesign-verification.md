# OpenDesign 接入验证

核验日期：2026-10-13。源码基线：GitHub `main`，`c877996`。本记录针对 1.3.3 新增的 OpenDesign 支持。

## 数据契约

依据为 [OpenDesign 控制台](https://open-design.ai/cloud/dashboard) 当前部署的第一方前端：入口 `index-DmFaigj4.js`、业务模块 `cloud-root-CFRMpZfR.js` 和共享模块 `pricing-refund-policy-Vq0oCQ8H.js`。这些资源仅作为当日私有接口依据，不构成服务方公开 API 稳定性承诺。

| 查询 | 请求与字段 |
|---|---|
| 工作空间身份 | `GET https://amr-api.open-design.ai/api/v1/workspaces/current`；核对 `workspaceId` 与 `workspaceMemberId` |
| 钱包 | `GET /api/v1/workspaces/{workspaceId}/wallet/balance`；读取 `balanceUsd` |
| Design Plan | `GET /api/v1/workspaces/{workspaceId}/billing/coding-plan-usage`；读取 `eligible` 及 `windows` 中的 `policyId`、`durationSeconds`、`limitCredits`、`remainingCredits`、`resetsAt` |

请求携带浏览器会话及 `x-vela-workspace-id`，仅向固定 API 主机执行 GET。工作空间 ID 同时出现在目标路径、请求头及身份检查中。只把已核对的工作空间和成员纳入来源身份。

控制台按剩余比例展示套餐窗口；桌面组件沿用项目既有的已用百分比口径。比值越界时，与前端一致地夹紧为 0–100%。缺失上限、非法数值或非法重置时间不生成额度值。缺失重置时间保持未知，未开始的窗口不会补造倒计时。

## 已执行

| 范围 | 结果 | 证据与限制 |
|---|---|---|
| Release 构建 | PASS | 应用与桌面验收工程均为 0 warnings / 0 errors |
| 完整自动化回归 | PASS | 200/200；包含 41 项 OpenDesign 专项用例 |
| 配置与默认注册表 | PASS | 无密钥保存、文件重载、双能力选源、DeepSeek 来源隔离与通用探测隔离 |
| 工作空间与失败边界 | PASS | 测试数据覆盖跨空间缓存失效、独立浏览器配置键、缺失数据、鉴权失败、权限不足、限流、窗口部分失败与取消传播 |
| 已登录真实网页 | PASS | 可观察到钱包、5 小时及 7 天窗口，与所采用字段的展示语义一致；不等价于桌面查询成功 |
| 桌面 WebView2 真实读取 | PASS | 用户启动独立验收程序并完成登录；2026-10-13 13:52:51（UTC+8）生产查询返回 `Success`，同时取得 USD 余额、5 小时与 7 天窗口；两项查询均为当前来源，快照未过期；已检查面板和浮窗截图 |
| 桌面会话重启恢复 | PASS | 用户关闭首次登录进程后，于 2026-10-13 14:36:23（UTC+8）启动新进程执行 `--verify`；14:36:30 返回 `Success` 且 `restart=true`，无需交互登录，余额及两个窗口均为新取得且未过期的数据；已检查恢复后的面板与浮窗截图，程序按预期自动退出 |
| 本地预览包 | PASS（构建与打包） | 已生成 Windows x64 自包含预览包，产品版本为 `1.3.2-opendesign-local`；已检查包内文件与 SHA-256。该压缩包本身未单独启动验收，真实读取及重启证据来自装配生产查询、会话、协调器和 UI 的独立桌面验收程序 |
| 正式版本打包 | 单独记录 | 1.3.3 打包与发布验证见 [1.3.3 发布验证说明](releases/v1.3.3-verification.md)；1.3.2 下载包不含本扩展 |

自动化测试结果保存在忽略目录 `artifacts/opendesign-verification/opendesign-tests.trx`。桌面真实读取证据位于 `artifacts/opendesign-live/`：`live-result.json`、`live-widget.png`、`live-panel.png` 以及只记录字段名的 `wallet-schema.json`、`windows-schema.json`。重启恢复证据为 `last-start.json`、`restart-result.json`、`restart-pass.txt`、`restart-widget.png` 和 `restart-panel.png`；保留的 `restart-error.txt` 来自首次登录前的失败尝试，其时间早于本次通过记录。真实桌面验收步骤见 [DesktopSmoke 说明](../tests/DesktopSmoke/README.md#opendesign-真实只读验收模式)。凭据、浏览器配置和私人额度截图不提交到仓库。

早期本地预览包位于 `artifacts/opendesign-preview/TokenConsumptionMonitoring-OpenDesign-local.zip`，校验文件为同目录的 `SHA256SUMS`。

## 1.3.4 私有兼容配置

1.3.4 将推理地址与工作空间配置分开：Base URL 和 API Key 保持原值，`openDesignWorkspaceId` 仅用于显式启用的私有兼容查询。控制台会话仍按页面 ID 与工作空间隔离。自动测试覆盖配置保存重载、来源与指纹隔离、工作空间切换、API 凭据保留以及登录分发；WPF 验证覆盖实际编辑控件和宽窄布局。

上述 1.3.3 真实读取与重启结果属于当时的控制台页面模式，不能视为 1.3.4 API 页面模式已经完成真实账户验收。1.3.4 的验证范围见 [发布验证说明](releases/v1.3.4-verification.md)。
