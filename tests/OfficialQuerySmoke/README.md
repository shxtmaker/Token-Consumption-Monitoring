# 官方查询验收程序

默认使用内存配置和合成数据，不接触已安装应用的配置、凭据或托盘，也不打开测试窗口。程序实例化正式 WPF 面板与小组件，验证凭据选择及保存，再输出 PNG 供人工检查。

共输出十张 PNG，包括凭据表单、小组件、短窗口标题的进度条宽度、长标题与多币种额度的对齐场景，以及候选方法链在首次扫描、重新扫描和全部不可用时的显示结果。短标题场景会检查进度条的实际布局宽度，防止固定标题占位挤压进度条。

```powershell
dotnet run --project tests/OfficialQuerySmoke/OfficialQuerySmoke.csproj -c Release -- artifacts/official-query-verification
```

指定以下选项后，才会使用当前用户已登录的 Codex CLI，调用 `account/rateLimits/read` 和 `account/usage/read`。不发起推理，不保存账户标识、额度值或登录内容，只输出状态与能力条目数。

```powershell
dotnet run --project tests/OfficialQuerySmoke/OfficialQuerySmoke.csproj -c Release -- --live-codex
```

退出码 0 表示步骤通过；真实接口返回空数据也可能是该账户暂不可提供该能力。PNG 是合成验收数据，不能视作真实账户截图。
