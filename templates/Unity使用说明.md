# 陈千语工具生成的预览

生成工具：EndfieldChenTool 0.1.0。

在 Unity 的 **Endfield Chen Tool** 菜单中选择 UI 或 ACL 行走预览，即可打开对应场景并开始播放。也可以打开本目录下的场景，再点击顶部 Play。

- `Chen_UI_Walk.unity`：原 UI 的 female_walk，使用 Generic。
- `Chen_Reextracted.unity`：ACL 行走片段，使用实际骨架建立的 Humanoid Avatar。
- `Animations/Chen_UI_Walk.anim` 与 `Animations/Chen_Walk.anim`：经过 Unity 处理的动画。
- 带 `_Source` 的文件：解析器输出，供追溯；运行预览应使用处理后的动画。

只有在工具中勾选并成功生成的场景才属于本次结果。工作区中的 `结果说明.md` 列出了实际完成的项目及检查状态。

生成场景包括单位与路径修复、无效切线的原生插值恢复，以及原地预览所需的场景偏移处理。材质为基础贴图，未完整复原游戏专用着色器、IK、布料动力学和特效。

再次导入会重新生成工具目录中的场景、动画和控制器。工具会备份已有场景、控制器、材质和 Avatar；手工编辑请另存到其他目录。
