# 开发与架构说明

## 架构

页面只保存名称、端点、协议、凭据引用和配置提示。运行时按以下边界工作：

```text
MainPanel / PageEditorViewModel
      │
      ▼
PageConfigurationCommands ──> 凭据存储 + PageConfigStore
      │ 提交成功后发布
      ▼
PageCatalog ──> PageEngine / PageRefreshQueue
                        │
                        ▼
              PageRuntimeCoordinator
                ├─ CapabilitySourcePlan：按能力选择来源
                ├─ MethodExecutor ──> IQueryMethod：查询、重试与缓存
                └─ SnapshotPolicy：状态与历史值决策
                        │
                        ▼
              活动页投影到面板、桌面组件和托盘
```

- `IQueryMethod` 统一描述、扫描和查询阶段。方法不绑定套餐或固定页面布局。
- `CapabilitySourcePlan` 按能力槽选择来源。同一能力槽只保留一个来源，选中来源返回的多个窗口、币种或统计条目全部保留。
- 普通轮询只查询当前计划中的来源。来源失败时保留最近成功值并标记为过期，同时为受影响能力尝试候选回退。
- `PageCatalog` 统一管理页面集合并提供独立副本。配置修订或删除后，即使旧请求忽略取消并迟到返回，也不能覆盖新页面的运行时结果。
- `PageRefreshQueue` 合并相同页面、修订和刷新原因的在途请求；手动重扫不会被普通轮询吞掉。退出时取消并等待引擎任务完成后再释放资源。
- 手动重扫绕过普通结果缓存，但保留同一数据身份下的最近成功值。限流冷却仍生效：使用已传递的 `Retry-After`，未提供时默认冷却一分钟。
- `PageRuntimeStateStore` 按页面保存扫描报告、来源计划、快照、失败状态和进程内临时覆盖。非活动页面不会改写活动页面的界面、托盘或桌面组件。
- 报告用量和报告成本直接展示；估算成本、模型目录和 token 细分只保留在领域接口中。

## 目录

| 目录 | 职责 |
|---|---|
| `Models/Usage/` | 能力、来源、候选、快照和凭据引用契约 |
| `Models/PageConfig.cs` | 方法无关的页面配置和版本化文档 |
| `Services/QueryMethods/` | 查询方法、错误分类和注册表 |
| `Services/Scanning/` | 指纹、扫描上下文、凭据解析和候选选择 |
| `Services/Runtime/` | 页面协调、来源计划、缓存、重试和运行时状态 |
| `Services/Persistence/` | 页面扫描状态和候选选择持久化 |
| `Services/PageCatalog.cs` | 页面集合所有权、修订检查与提交后发布 |
| `Services/PageConfigurationCommands.cs` | 配置与版本化凭据的一致提交 |
| `UI/PageEditorViewModel.cs` | 编辑草稿、校验与保存状态 |
| `UI/MonitorState.cs` | 活动页面的展示状态 |
| `UI/Diagnostics/` | 能力快照和扫描诊断视图模型 |
| `tests/TokenConsumptionMonitoring.Tests/` | 配置恢复、凭据提交、能力契约、来源选择及生命周期回归测试 |
| `tests/DesktopSmoke/` | 使用模拟查询源的独立桌面引擎验收程序 |


