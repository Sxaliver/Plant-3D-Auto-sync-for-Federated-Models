# 自动化配置与边界

## 处理顺序

Total 是上级总装，不替代下级系统总装。完整流程应先处理每个系统的源 Xref 与系统总装，再处理 Total；需要验证实际图纸中的 Attachment / Overlay、卸载状态和跨系统引用，不能只按文件名假定依赖完整。

交付文件是每个系统分别导出的一份 NWC。Total DWG 放置各系统的总装 DWG，不放置 NWC；各系统导出 NWC 与放置到 Total 是两个分支，本项目文件流不包含 Total NWC 导出。

目前提供 `SYNC` 交互流程与 `PMSWEEKLYRUN` 批处理入口。`Run-Weekly.ps1` 不会自行注册 Windows 计划任务，也不会自动上传 NWC；固定依赖顺序的 Total 全流程编排需要结合实际图纸另行集成和验证。

## 批处理

1. 在 Plant 中设置并验证交互目录，退出同步操作。
2. 将 `src/Plant Model Sync/weekly-config.example.json` 复制为同目录的 `weekly-config.json`，填写本机真实绝对路径。不要把个人配置提交到仓库。
3. 先对隔离样例验证，再授权运行真实项目：

```powershell
& '.\src\Plant Model Sync\Run-Weekly.ps1' -ConfigPath '.\src\Plant Model Sync\weekly-config.json' -SystemCode 9424 -DryRun
```

`DryRun` 可为校验读取 / 下载临时快照，但不改写项目 DWG、不挂接或导出。不是无网络的纯内存模拟。`-ForceExport` 只跳过已导出判断，不能绕过占用和引用完整性保护。

如配置每周五 15:00 执行，时区应明确为 **Asia/Singapore**；需由外部调度器调用同一脚本。需要已登录 Desktop Connector、可用的 Plant 许可与桌面会话。配置中的 `ShowWindow` 允许启动器显示它自己创建的独立 Plant 窗口，不得操作或关闭原有用户窗口。

只按本次 `result.json` 中 `Completed`、`HasFailures`、`SuccessfulExports` 和 `ReportPath` 核对结果；进程结束或文件存在都不等于全批成功。失败与待确认项应保留，避免假报交付完成。

## 文件保护

- 源 DWG 的 Pull 使用云端发布版本和修改日期；覆盖本地 Xref 不创建备份，但复制前后仍有校验和占用保护。
- 总装结构、原生引用名及路径修复仍保留对应备份保护；历史目录不当作正式源。
- 自动匹配按规范化路径与系统归属进行；重名歧义须人工确认，不能按最新日期猜源文件。
- 新 Xref 必须确认插入位置、比例、旋转及目标总装。项目坐标不是通用默认坐标。
- 手动导出使用当前本地模型，不要求先与云端完全同步；周更批处理仍使用它自己的严格检查规则。
- NWC 的后续自动 Push 尚未实现。未来通过 Connector 提交后，应区分“已交给 Connector”和“云端完成”，不能把本地复制当成上传回执。

## 公开源码适配

只包含当前插件构建与隔离回归需要的代码及共用控件。个人项目路径、已保存配置、日志、令牌、用户模型和 Autodesk DLL 均不包含。项目特定的旧目录迁移脚本需要显式传入根目录，默认仅预检；执行前务必逐项核对迁移计划。
