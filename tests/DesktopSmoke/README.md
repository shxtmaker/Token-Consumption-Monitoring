# 桌面引擎验收程序

本程序装配生产 MainPanel、PageConfigurationCommands、PageCatalog、PageEngine、PageRuntimeCoordinator、MethodExecutor 与运行时存储。仅查询方法和托盘输出使用测试适配器。程序不会调用供应商查询接口。

构建命令：

```powershell
dotnet build tests/DesktopSmoke/DesktopSmoke.csproj -c Release -p:OutputPath=bin/EngineCheck/
```

运行 `tests/DesktopSmoke/bin/EngineCheck/TCMArchitectureDesktopSmoke.exe`。配置与运行时记录位于该程序目录内的 `engine-smoke-data`，不读取正式应用配置。初次运行创建测试页面 A 和 B，模拟余额分别为 100 USD 与 200 USD。编辑表单不要填写真实密钥；凭据仅保留在进程内，重启不恢复密钥。

工具栏操作：

- 模拟正常：恢复成功结果并手动刷新。
- 模拟断网：注入查询网络失败并手动刷新，检查历史余额、获取时间和 stale 状态。
- 模拟登录完成：发出控制台会话变化通知，验证代际失效和重新查询；不代表真实登录已验收。
- 连续刷新两次：同时发起两次全页手动刷新，检查展示一致性。合并次数由自动化队列测试验证。

页面下拉、新建、编辑、保存、删除及候选操作均使用生产面板。关闭窗口会先等待引擎停止，再释放资源；成功后写入 `last-stop.txt`。窗口重新启动时恢复已保存的活动页。

本程序不覆盖真实 HTTP、WebView2、OAuth 登录、系统托盘图标绘制、浮窗和正式 App 退出装配。这些项目应单独验收。

2026-09-07 已在桌面观察到：启动展示 A 页 100 USD；断网后保留余额及原获取时间并显示 stale、托盘输出 Warn / None；会话变化后指纹改变且恢复正常；切到 B 页显示 200 USD；连续刷新后保持 B 页；关闭完成 StopAsync 且进程退出；重启恢复 B 页 200 USD。
