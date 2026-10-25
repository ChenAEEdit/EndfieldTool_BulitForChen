# 终末地角色工作台与陈千语提取工具

2026-10-25：源码现为 **0.8.0 Unity 自动生成预览**。在独立适配包和原生结果回读基础上，新增“导入并生成适配”，使用现有项目完成复制、后台 Unity 原生生成、调用回执和文件核对，并保留取消、日志及原输出。详见[动画适配说明](docs/动画适配说明.md)及[开发记录](docs/Unity自动生成开发记录.md)。本阶段完成实现、编译及打包；完整自动流程尚未实测、播放未验证。0.6.1 的真实原始导出证据见[对象导出验证记录](docs/对象导出验证记录.md)，此前两条行走的独立检查见[膝部诊断记录](docs/膝部诊断记录.md)。旧陈千语 v0.1.3 保留在 `D:/AAAChatGPT/EndfieldChenTool`；新工作台运行目录为 `D:/AAAChatGPT/EndfieldCharacterTool-Framework`。

以下为保留的陈千语 v0.1.3 提取流程说明，可从工作台的“环境与已有工具”页面或 `--chen` 参数进入该窗口。

本阶段完成情况与下次接续：[2026-10-25 晚间工作总结](docs/2026-10-25_晚间工作总结.md)。

Windows 本地工具，将已验证的陈千语提取和 Unity 动画修复流程做成图形界面。

## 使用

1. 完整解压文件夹，双击 `EndfieldChenTool.exe`。
2. 选择游戏目录或 `Endfield_Data`，再选择一个独立的空输出目录。
3. 选择需要的动画。需要 Unity 预览时填写现有 Unity 项目和编辑器路径。
4. 先点“检查环境”，再点“开始提取与导入”。导入前关闭该项目的 Unity 编辑器。
5. 完成后点“打开 Unity 预览”，或查看输出目录的 `结果说明.md`。

程序提供进度、日志、取消和结果入口。再次运行会按 MD5 校验 Bundle，并按文件摘要复用已有的有效导出；未完成的阶段重新执行。

## 当前支持

- 角色：陈千语，LOD0 模型、贴图、骨架与权重。
- 游戏版本：`20260821_c24495e5`，会核对 GameAssembly SHA256 和资源 MD5。
- 动画：`female_walk`、`A_actor_chen_customized_state_walk_loop`。
- Unity：项目与编辑器 6000.0.84f1。
- Blender：实际验证版本 5.0.1，用于 Python 环境与可选的模型权重检查。

运行环境需要 .NET 10 Desktop Runtime，以及 Python 3.10 或以上。已经安装 Blender 时会自动使用它自带的 Python。工具运行不需要重新编译导出器或从 NuGet 下载包。

本版没有把“任意角色、任意版本、全部动画”作为已支持功能。版本不一致时会停止，不会靠猜测修改资源格式。

## 输出

工作区独立于游戏、工具目录和 Unity 项目：

```text
workspace.json            工作区标识
last_run.json             本次真实完成状态
结果说明.md               场景路径与检查结果
source/bundles/           经 MD5 检查的资源
source/decoded/           解析器原始动画及导出报告
assets/model/             FBX、贴图和 Avatar 元数据
assets/animations/        已按角色路径筛选的源动画
assets/Chen.blend         勾选权重检查时生成
unity/EndfieldChenTool/   Unity 待导入文件
reports/                  模型、路径、网格及实际播放报告
logs/                     运行日志
backups/                  重建前的已有场景等文件
```

Unity 内容写入现有项目的 `Assets/EndfieldChenTool`。这是工具生成目录，再次导入会重新生成其中的场景、控制器和动画；手工编辑请另存。已有的场景、控制器、材质和 Avatar 会在工作区备份。

仅导出时，Source 动画还没有经过 Unity 原生曲线处理。需要再次勾选 Unity 导入，或将待导入目录复制到项目后通过随附菜单生成场景。

## 检查

- 导出前检查游戏版本和每个 Bundle 的 MD5。
- 检查模型网格数、顶点数、Avatar 是否导出。
- 动画必须实际包含曲线和关键帧；按角色路径表筛选并记录排除项。
- 可选 Blender 权重检查：缺失权重、非法引用、非有限权重、权重归一化。
- Unity 生成场景时采样 17 个时刻检查网格。
- 勾选“检查实际 Play 模式”后，再检查 3 个运行时刻并保存实际画面。
- 每次 Unity 检查必须生成新的报告；旧报告移入历史目录，不会用于认定本次成功。

没有请求或没有执行的检查会标为 `not_requested`，不会标为通过。模型使用基础贴图；游戏专用着色器、IK、MC1 布料动力学及特效未完整复原。

## 故障与继续

- 游戏版本不同：需要针对新版本更新角色配置。
- 项目正在打开：关闭对应 Unity 编辑器后重试，不需要换项目。
- 提取中断：使用同一个工作区重新运行，已完成的有效文件会复用。
- Unity 失败：查看工作区 `reports/unity` 下的日志；提取结果仍可保留。
- 想强制重新生成资源：勾选“重新导出，忽略已有缓存”。

## 命令行

可以使用 Blender 自带的 Python 运行工具文件夹中的 `pipeline.py`：

```powershell
& 'D:\Blender\5.0.1\5.0\python\bin\python.exe' '.\pipeline.py' `
  --game 'F:\AAA_ZMD\Hypergryph Launcher\games\Arknights Endfield\Endfield_Data' `
  --workspace 'D:\AAAChatGPT\EndfieldChenWorkspace' `
  --blender 'D:\Blender\5.0.1\blender.exe' --weights `
  --unity 'D:\Unity 3D\Editor\6000.0.84f1\Editor\Unity.exe' `
  --unity-project 'D:\Unity 3D\Projects\Codex_ZMD' --verify-play
```

附加 `--check-only` 只检查环境，`--animations ui` 或 `--animations acl` 选择一条，`--force` 忽略导出缓存。省略 `--unity-project` 时只导出待导入文件。

第三方来源与许可证见 `THIRD_PARTY.md` 和 `licenses`。工具包不包含游戏模型、贴图或动画，资源由用户选择的本机游戏目录生成。

## v0.1.1 修正

采样检查结束后恢复 FBX 的原始姿势，再保存场景；重新加载后逐个核对骨骼变换，避免未播放时就保存成动画姿势。按播放后仍由 Animator 正常驱动动画。


## v0.1.2 面部修正

共享 female_walk 含有与陈千语不一致的面部常量。预览保留陈千语原始面部姿势，身体和头部仍正常行走；这条预览不包含独立表情动画。原始 Source 动画完整保留，修正只应用于生成的预览片段。

面部范围由脸、虹膜、眉毛和眼部辅助网格实际引用的蒙皮骨骼及其祖先确定，不按骨骼名称猜测。生成和 Play 检查新增逐节点位置、旋转、缩放核对，以及第20帧和运行时面部近景。

眼白辅助阴影采用半透明基础材质，避免将衣服贴图误贴到眼睛上。此材质是预览替代，不是原游戏着色器的完整复原。


## v0.1.3 待机预览适配

新增六条角色界面待机的独立 Unity 预览与切换面板。当前图形界面的两个选项仍负责两条行走；待机使用项目中的 Assets/ChenIdle/Chen_Idle.unity 或 Endfield Chen Tool 菜单。详见 docs/IDLE_PREVIEW.md。
