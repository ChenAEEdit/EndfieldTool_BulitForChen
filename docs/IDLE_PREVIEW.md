# 陈千语待机预览

## 在当前项目中使用

项目：`D:/Unity 3D/Projects/Codex_ZMD`

1. 打开 `Assets/ChenIdle/Chen_Idle.unity`。
2. 切到 Game（游戏）窗口并点击播放。
3. 用左上角按钮切换动作；Pause 暂停，Restart 从头播放。

也可以通过 Unity 菜单 **Endfield Chen Tool → 打开待机预览** 打开并播放。

| 按钮 | 源片段 | 时长 |
| --- | --- | ---: |
| Overview idle | A_actor_chen_ui_overview_loop | 2 秒 |
| Relax idle | A_actor_chen_ui_relax_loop | 约 2.67 秒 |
| Team idle 01 | A_actor_chen_uiteam_idle_01 | 2.5 秒 |
| Team idle 02 | A_actor_chen_uiteam_idle_02 | 2.5 秒 |
| Team idle 03 | A_actor_chen_uiteam_idle_03 | 2.5 秒 |
| Team idle 04 | A_actor_chen_uiteam_idle_04 | 2.5 秒 |

每条动作还有独立的 `Chen_Idle_Overview`、`Chen_Idle_Relax`、`Chen_Idle_Team01～04` 场景。

未播放时为原始 T 字姿势。动画、控制器和场景都在 `Assets/ChenIdle`，模型和基础材质引用 `Assets/EndfieldChenTool`。单独搬动待机目录时，需要一起保留它引用的资源。

## 提取与适配

- 本次复核了游戏 GameAssembly SHA256、源 Bundle MD5，并记录源动画文件 SHA256。
- 六条动画由原始解包结果映射到陈千语 Avatar 的路径表，再匹配当前 FBX 导入层级。
- 总览待机的 Humanoid 根位置曲线为零，同时存在完整的骨骼 Transform 曲线。对照中，按 Humanoid 播放会将身体压低并改变姿势。
- 待机预览采用 Generic，使用实际骨骼曲线，排除 Animator 根／肌肉通道，保留角色专属面部与头发轨道。原始 Source 文件保留。
- 现有共享行走的面部过滤规则不应用于这些专属待机；微笑和表情本来就可以偏离原始中性姿势。

## 验证范围

- 六条动画各采样 17 个时刻检查蒙皮；缺失骨骼路径为 0，检查通过。
- 每条额外查看开始、中间和循环末尾的全身与面部近景。
- 循环接缝检查局部位置和旋转：阈值为 5 毫米／2 度，六条通过；本次最大位置差约 1.77 毫米、最大角度差约 0.46 度。
- 记录脚部位移，未加入脚部 IK 或人为锁脚；总览片段的右脚在三个对照时刻中最多位移约 1.13 厘米。
- 实际 Play 模式逐条切换，每条检查 3 个时刻，包含超过一个循环之后的状态。验证实际状态名、时间推进、网格尺寸和非有限顶点，并输出运行近景。
- 另外检查保存后的原始姿势、默认 Overview 状态、六个切换入口和两条原行走的播放。

这确认了本机预览的上述检查结果，没有与原游戏逐帧对照；游戏着色器、独立 IK 和实时布料求解尚未完整复原。

## 两条附加片段

`A_actor_chen_idle_loop_additive` 与 `A_actor_chen_idle_loop_additive_cloth` 已解码、校验并保留。本次未确认它们对应的基础层、参考姿势及混合／布料设置，因此不加入六个独立动作按钮。

## 本机文件位置

- 原始解码结果：`D:/AAAChatGPT/2026-10-23/ni/outputs/chen_idle_search`
- 来源与摘要：`D:/AAAChatGPT/2026-10-23/ni/outputs/chen_idle_preview/provenance.json`
- 路径映射、采样、循环、实际播放和截图：同一工作区的各 JSON 文件及 `verification` 目录
- 原行走复查：同一工作区的 `walk_regression` 目录

## 工具与源码

待机适配器与切换面板位于源码 `templates/idle`，暂存脚本为 `scripts/prepare_idle_preview.py`。当前图形工具的两个勾选项仍负责两条行走；本次待机通过项目中的独立场景与菜单使用。

生成器依赖当前项目中的 `EndfieldChenTool.Generated.SceneBuilder`。v0.1.3 的核心模板已加入专属待机的直接骨骼模式，以便后续重新导入核心工具时继续兼容。
