# 第三方来源

本工具把本机已验证的流程组织为图形界面和流水线。相关第三方代码及二进制保留原许可证。

- ZMD_unpack：<https://github.com/HollowAmeChan/ZMD_unpack>，本地基准提交 `8a22068be65b8ddcdae7583eb2d48d8abca9ddfb`。VFS 读取器、导出器和 ACL 补丁来源于该项目。VFS 读取器使用已验证的 `29047701` 标记。
- AnimeStudio / EndfieldStudio：<https://github.com/EIHRTeam/EndfieldStudio>。模型导出使用本机已有的 1.1.0 库；动画导出使用 ZMD_unpack 锁定源码及 ACL 补丁编译的库。许可证与第三方声明见 `licenses`。
- Ruri.RipperHook / Ruri.ACL：ZMD_unpack 锁定的源码，许可证为 AGPL-3.0，见 `licenses/Ruri.RipperHook-AGPL-3.0.txt`。相关 ACL 源文件及使用的补丁在 `sources`。

模型与动画导出器分别保留独立的运行目录，避免混用两个版本的解析库。构建时使用了本机缓存的依赖版本；本地验证记录保存在原任务的修复记录中。运行工具不需要读取原任务目录。

`sources/gui` 保存图形界面源码，`pipeline.py` 保存流水线源码，`sources/exporters` 保存本机适配的导出器源码，`sources/patches` 保存上游集成补丁。

0.3.0 的 `scripts/endfield_manifest.py` 复用本机 ZMD_unpack 工作副本中的严格 Bundle 表／依赖解析器；`scripts/endfield_vfs.py` 仍为原 VFS 读取器。新增发现脚本、界面索引服务及 Brotli 辅助入口保存在本地 Git 仓库与运行包源码中，沿用现有第三方来源和许可证文件。

依赖源码的版本信息、原项目地址和许可证用于追溯。本地工具包仅含工具与依赖，游戏资产在运行后写入用户指定的独立工作区。

0.6.0 新增的 `BundleObjectProbe` 使用上述带 ACL 补丁的解析库读取实际对象。模型／动画导出入口增加按来源与 PathID 精确选择的分支。新工作台使用独立 `runtime/object-probe`、`runtime/object-model`、`runtime/object-animation`，保持不同库版本隔离；来源和许可证沿用以上声明，新增入口源码随包保存。
