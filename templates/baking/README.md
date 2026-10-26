# Unity 动画烘焙与 FBX 导出脚本

独立的 C# 编辑器脚本，菜单为 **Tools → Endfield → 动画烘焙与 FBX 导出**。

## 安装

1. 把随附的 `CharacterAnimationBaker.cs` 复制到 Unity 项目的 `Assets/Editor/CharacterAnimationBaker.cs`。
2. 等待 Unity 编译完成，打开上述菜单。
3. 只生成 `.anim` 不需要额外插件。需要 FBX 时，在 Package Manager 安装 **FBX Exporter**，包名 `com.unity.formats.fbx`，本次开发对应版本 5.1.6。

不依赖 Recorder、Timeline 或工作台其他脚本。不要把同一脚本重复复制到项目的多个目录。

## 使用现有角色工作台的预览场景

1. 打开已经能正确播放的角色预览场景，退出 Play，并关闭 Animation / Timeline 的动画预览。
2. 在 Hierarchy 选择包含网格和完整骨架的角色总根节点，例如 `Chen_Qianyu`。
3. 打开烘焙窗口，点 **使用 Hierarchy 中选中的角色**。
4. 点 **读取现有工具预览场景的动画列表**，从下拉列表选择待机或其他动作。脚本读取 `CharacterPreviewControls` 的公开配置，每条动画使用其对应的 Avatar。
5. 首次使用默认 60 FPS，`求值 Apply Root Motion` 与原本正常播放时的 Animator 保持一致。
6. 点 **烘焙为 .anim**，或 **烘焙并导出模型 + 动画 FBX**。
7. 完成后，Project 会定位到生成的 `.anim`，窗口显示本次输出目录。

如果没有该预览控制器，手动拖入角色、Animator、动画和 Avatar 即可。角色层级必须恰好包含一个 Animator。

## 使用已经录制的 .anim

把 Recorder 输出的 `.anim` 拖入 **输入动画 / 已录制 .anim**，指定录制时对应的角色及 Animator，再执行烘焙导出。脚本会再次按指定帧率采样；它不会仅根据文件名认定文件已经正确烘焙。

输入动画中的路径必须相对于所指定的 Animator。若 Recorder 录制的是 Animator 上方另一层父对象，路径可能多一段前缀，此时应先把动画调整为对应 Animator 的路径。脚本遇到找不到的路径会停止，不会默默丢弃。

Humanoid 输入需要正确的 Human Avatar。普通 Transform 输入若没有有效 Generic Avatar，或原 Animator 使用 Human Avatar，脚本会在临时副本上建立 Generic Avatar。

## 输出

默认输出父目录为 `Assets/BakedAnimations`，每次生成带时间和随机后缀的新子目录：

```text
角色_时间_后缀/
├─ 动画_Baked.anim
├─ 角色_Model.fbx        # 选择 FBX 导出时生成
├─ 动画.fbx              # 选择 FBX 导出时生成，只有动画
└─ bake-report.json
```

报告保存源动画路径、实际使用的 Avatar 名称、Humanoid 输入标志、帧率、时长、采样点数、Transform 数量、蒙皮骨骼数、移动节点数量、BlendShape 通道数、绑定姿势误差、导出路径和完成状态。`ueVerified` 初始并保持为 `false`；生成文件不代表已在 UE 中验证。

模型和动画使用同一临时角色层级，统一采用 Binary FBX、相同命名规则和蒙皮动画选项。模型导出前恢复绑定姿势，动画通过普通骨骼曲线导出。原始网格、权重与源动画不被修改。

本次新生成的 FBX 在 Unity 中自动设置为 Generic，关闭动画有损压缩与骨架优化。只有本次新文件的导入设置会调整。导出时排除原层级中隐藏或禁用的网格，但保留节点层级，避免重导入后隐藏 LOD 与当前 LOD 重叠。

如果输入时长在所选帧率下有半帧等非整数终点，FBX Exporter 的旋转再采样可能遗漏该终点。脚本会选用所选帧率的整数倍作为 FBX 再采样帧率，将终点纳入网格，并写入 `fbxFramesPerSecond`，不改变 `.anim` 时长或原始采样点。尝试 1、2、3、4、6、8 倍后仍不能对齐时，FBX 导出明确报错；可换用源动画帧率，或先保留 `.anim`。

## 实现方式

- 在临时 Preview Scene 中克隆角色；存在对应 Prefab 时，从 Prefab 获取初始内容。因此本版不导出场景实例上的 Prefab 覆盖修改，应先将需要的改动保存为独立 Prefab。
- 关闭临时副本上的脚本、约束和物理组件，只启用目标 Animator。
- 根据蒙皮的 bindposes 还原骨骼绑定姿势；不同网格的绑定姿势不一致、矩阵不可逆或包含无法表示的剪切时停止。
- 用手动更新的 `PlayableGraph` 求值单条 `AnimationClipPlayable`，关闭额外 Foot IK / Playable IK。
- 逐帧记录所有子节点和总根节点的局部位置、四元数旋转、缩放；可选记录 BlendShape 权重。包含起点和终点，最后一个时间点精确取输入动画长度。
- 四元数采用连续符号，采样键使用线性切线；不执行有损减键。
- FBX Exporter 通过反射访问公开接口，未安装该包时仍可编译并烘焙 `.anim`。
- 正常结束、取消或异常后清理临时场景、图和临时对象。导出失败会保留已经写入的结果，并在报告中标记 `failed`。

## 边界与注意事项

- 这是单条动作烘焙，不是整个场景的运行时录制。Animator 层混合、过渡、Timeline 偏移、Animation Rigging、脚本 IK、游戏布料物理不会随之执行。
- 已经存在于动画或适配结果中的脸部、膝盖等问题会被原样记录。
- BlendShape 权重会录制，但原游戏专用材质、贴图打包与着色器不会自动迁移。
- 根节点曲线保留采样结果；`Apply Root Motion` 只决定 Unity 求值时是否应用根运动，不是自动把位移转换到 UE 指定 Root Bone 的开关。UE 根运动还需要结合实际骨架层级检查。
- 输出 `.anim` 的路径相对于所选角色总根节点，回放时应绑定该总根节点。
- 不支持对象引用动画；路径歧义、外部蒙皮骨骼引用、非有限数值等会停止并提示。
- 单次最多 600 万个采样键，防止长动画耗尽编辑器内存。需要时降低帧率或先裁剪动画。
- 本脚本不负责修复源动画、重绑权重、重命名骨架或重定向到 UE Manny。

## UE 导入顺序

1. 先导入 `角色_Model.fbx`，建立该角色自己的 Skeletal Mesh 和 Skeleton。
2. 再导入动画 FBX，指定上一步建立的 Skeleton。
3. 检查尺寸、朝向、动作时长、面部、膝盖和根运动。
4. 需要其他角色骨架时，再在 UE 中做重定向。

## API 参考

- [Unity AnimationClipPlayable](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Animations.AnimationClipPlayable.html)
- [Unity PlayableGraph.Evaluate](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Playables.PlayableGraph.Evaluate.html)
- [FBX ExportModelOptions](https://docs.unity3d.com/Packages/com.unity.formats.fbx@5.1/api/UnityEditor.Formats.Fbx.Exporter.ExportModelOptions.html)
- [FBX Exporter 绑定姿势限制](https://github.com/Unity-Technologies/com.unity.formats.fbx/blob/main/com.unity.formats.fbx/Documentation~/knownissues.md)

实际运行与编译记录见仓库 `docs/Unity动画烘焙脚本开发记录.md`。
