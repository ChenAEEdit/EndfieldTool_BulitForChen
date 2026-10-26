# Unity 动画烘焙与 FBX 导出脚本开发记录

日期：2026-10-26。

## 本次交付

- 独立脚本：`templates/baking/Editor/CharacterAnimationBaker.cs`。
- 使用说明：`templates/baking/README.md`。
- 安装位置：Unity 项目的 `Assets/Editor/CharacterAnimationBaker.cs`。
- 菜单：`Tools → Endfield → 动画烘焙与 FBX 导出`。
- 运行模式：编辑器中的单条动画离线采样，不需要 Recorder 或 Timeline。
- 依赖：只烘焙 `.anim` 不依赖其他包；FBX 导出使用 `com.unity.formats.fbx` 的公开接口，通过反射加载。

这是独立脚本交付，尚未把按钮加入 Windows 角色工作台，也没有替换已发布的工作台运行包。

## 功能

1. 手工选择角色、源 Animator、动画和 Avatar。
2. 读取现有 `CharacterPreviewControls` 的动画列表、显示名称和逐动作 Avatar。
3. 支持 Humanoid 动画经 Avatar 求值，以及普通 Transform / Generic `.anim` 再采样。
4. 按 15–120 FPS 记录所有 Transform 的位置、四元数旋转和缩放，默认 60 FPS，可选 BlendShape。
5. 从蒙皮 bindposes 还原并检查模型绑定姿势；保留原网格与权重。
6. 独立输出 `.anim`、模型 FBX、动画 FBX 和 `bake-report.json`。
7. 每次写入独立目录，不覆盖之前的导出。
8. 采样时可取消，异常与取消通过 `finally` 清理临时场景、PlayableGraph 和临时对象。

报告包含来源、实际 Avatar、输入动画类型、采样帧率、FBX 再采样帧率、时长、节点与蒙皮骨骼数量、移动节点数、BlendShape 通道数、绑定姿势误差与导出状态。`ueVerified` 保持 `false`。

## 开发中定位并处理的问题

### 隐藏 LOD 重叠

FBX 不能直接保留 Unity 的 `activeSelf` 语义。只按单个网格节点检查 `activeSelf`，会漏掉被父级 LOD 节点隐藏的网格。

脚本检查整个角色内部的祖先链，并在导出副本上移除隐藏或禁用的 Renderer / MeshFilter；保留 Transform 层级以保持模型和动画路径一致。最终陈样例重导入为 10 个启用的蒙皮网格。

### 骨骼动画不能再次由 Human Avatar 回放

检查烘焙 `.anim` 时，如果目标仍带 Human Avatar，禁用 Animator 本身不足以保证普通骨骼曲线按原值回放。

本脚本对普通曲线输入使用 Generic Avatar；导出的新 FBX 也设置为 Generic。核对导出曲线时，核对副本移除原 Animator，直接采样普通 Transform 曲线，避免把 Avatar 二次处理的结果误认为导出误差。

### FBX 回读默认有损压缩

默认 FBX 导入会执行动画减键。初次核对出现毫米级偏差，关闭有损压缩后骨骼位置误差明显下降。

脚本仅对本次新生成的 FBX 设置 `animationCompression = Off`、`optimizeGameObjects = false`，保留 BlendShape 导入，不改源模型的导入设置。

### 非整数末帧遗漏

专属行走时长约 2.0833335 秒，30 FPS 下最后时间点是半帧。FBX Exporter 的旋转转换按统一帧率再采样，直接导出会造成末帧姿势偏差。

脚本选用烘焙帧率的整数倍作为 FBX 再采样帧率，使终点进入采样网格。该样例从 30 提升到 60 FPS 后，末帧核对通过；`.anim` 原始时长与采样点不变。无法在 1、2、3、4、6、8 倍内对齐时明确报错，不默默输出错误末帧。

### FBX 顶点拆分与重排

重新导入时，部分网格会因法线等顶点属性拆分或重排，不能用同序号顶点一一比较。

核对采用双向空间最近点误差，同时检查三角形数量、动画路径、骨骼位置与网格数量。最终三条样例各有 3 个网格发生顶点数量变化，空间比较通过；这不等于证明所有顶点属性完全一致。

## 实际运行证据

环境：Unity 6000.0.84f1、FBX Exporter 5.1.6。

独立工程：`D:/AAAChatGPT/UnityValidation/剩余验收 B 20261026`。没有写入用户的 `My project` 或 `Codex_ZMD` 工程。

证据目录：`D:/AAAChatGPT/2026-10-23/ni/outputs/animation_baker`。

### 编译与菜单

- 使用本机 Unity 程序集编译，0 个错误、0 个警告，输出为 `compile-final/UnityTemplates.dll`。
- Unity 原生编译完成，菜单命令实际执行成功，并创建了唯一的烘焙窗口。

### 三条实际动作

最终证据：`final-receipt-4.json`，`completed = true`，Unity 退出码 0。

| 动作 | 输入类型 | 时长（秒） | .anim 采样点 | FBX FPS | 骨骼位置最大误差（米） | 双向顶点最大误差（米） |
|---|---|---:|---:|---:|---:|---:|
| `A_actor_chen_ui_relax_loop` | Generic | 2.6666667 | 81 | 30 | 0.000003755 | 0.000614405 |
| `female_walk` | Generic | 1.1000000 | 34 | 30 | 0.000001968 | 0.000615344 |
| `A_actor_chen_customized_state_walk_loop` | Humanoid | 2.0833335 | 64 | 60 | 0.000340408 | 0.000632860 |

说明：

- 本轮实际动作核对使用 30 FPS 烘焙，以覆盖非整数末帧；UI 默认仍为 60 FPS。
- 每条动画对起点、中点、终点共 3 个时间点进行 FBX 回读核对；每条输出 10 个启用的蒙皮网格，缺失动画路径为 0。
- 每条记录 460 个 Transform，使用的蒙皮骨骼为 320。三条移动节点数量分别为 207、78、213。
- 绑定姿势最大矩阵误差约 `0.000054493`，小于脚本采用的 `0.002` 阈值。
- 上表是“生成的骨骼 `.anim` 与导出 FBX 回读结果”之间的比较，不是与游戏实时画面的比较，也不是完整逐帧质量鉴定。
- 对已生成的待机 `_Baked.anim` 再次采样成功，输出 81 个采样点，验证已有 `.anim` 输入流程。
- 共享行走原有的脸部或膝盖问题不属于本脚本修复范围，导出会保留输入结果。

最终三个 FBX 样例目录分别为：

```text
Assets/AnimationBakerFinal/Character_20261026_211406_3fa894
Assets/AnimationBakerFinal/Character_20261026_211437_afbfa6
Assets/AnimationBakerFinal/Character_20261026_211512_30f060
```

### 补充功能核对

证据：`features-receipt-2.json`，`completed = true`，Unity 退出码 0。

- 从实际预览场景读取 3 条动画，并逐条核对选中的动画与对应 Avatar，均一致。
- 构造带单骨骼蒙皮、一个 BlendShape 和根位移的最小样例，以 60 FPS 烘焙并导出模型及动画 FBX。
- `.anim` 中点 BlendShape 权重为 50，FBX 重导入中点权重也为 50。
- 根节点 X 位移终点为 1，与输入的 1 米位移一致。
- 烘焙前后源根对象的序列化内容与位置一致；活动场景、场景加载数、场景根节点数、dirty 状态均保持一致。
- 输入引用不存在的 `MissingBone` 时明确拒绝，未静默丢弃。

这里的源对象与场景核对针对该最小样例，不代替对任意编辑器插件或 ExecuteAlways 脚本副作用的穷举验证。窗口菜单与读取方法已执行，未做完整鼠标点击回归。

## 尚未覆盖的范围

- UE 实际导入与运行；脚本的完成提示不声称 UE 已验证。
- 完整鼠标操作回归、所有角色与所有版本的适配。
- 源动画本身的视觉质量修复。
- 控制器多层混合、过渡、Timeline 偏移、原场景脚本、布料物理、脚本 IK、游戏专用材质与着色器。
- 任意长动画、任意骨架规模和任意非标准帧率的穷举验证。

## Git 范围

本次只提交本脚本、使用说明与本开发记录；已有共享骨架审计相关工作区修改不并入本次提交。
