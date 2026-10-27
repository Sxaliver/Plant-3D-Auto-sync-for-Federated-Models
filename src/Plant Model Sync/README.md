# Auto sync · 0.4.15

AutoCAD Plant 3D 2024 / Windows x64；交互命令 `SYNC`，批处理入口 `PMSWEEKLYRUN`。

公开仓库的背景、流程图与使用说明见根目录 [README](../../README.md)。

在仓库根目录执行 `build.ps1 -Install` 后重启 Plant 3D。使用“目录”配置本机 Connector、模型、系统总装及 NWC 输出目录，不包含任何个人路径预设。

两端匹配且已引用的 Xref 绿色置顶；云端存在但本地缺失或未引用的黄色其次。强制刷新重建自动对应，保留人工规则。手动 NWC 导出使用当前本地模型，按新加坡日期创建子目录。NWC 自动 Push 尚未实现。

仓库包含隔离回归；构建成功和隔离测试通过不代表在真实 Plant 会话、原生 NWC 导出或云端上传中完成验证。
