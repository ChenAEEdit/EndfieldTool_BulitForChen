# 未播放时姿势异常：原因与修正

## 原因

生成器为了检查蒙皮，会在编辑状态中调用 Animator.Play / Animator.Update 采样动画。这些调用会直接修改场景中的骨骼 Transform。

旧逻辑在采样结束后，再次采样动画第 0 帧并立即保存场景。因此，未进入 Play 模式也会看到迈步、弯腿或抬手的动画姿势。它不是原始模型的静止姿势。

这解释了本次反馈中“尚未播放就姿势不对”的现象；它不能单独证明动画中每个姿势与原游戏完全一致。

## 本机比较

将保存的场景和当前导入的原始 FBX 逐个节点比较，排除外层场景摆放位置：

| 场景 | 检查节点数 | 修正前不一致 | 重新生成并加载后不一致 |
| --- | ---: | ---: | ---: |
| ChenReextracted/Chen_UI_Walk | 459 | 164 | 0 |
| ChenReextracted/Chen_Reextracted | 459 | 239 | 0 |
| EndfieldChenTool/Chen_UI_Walk | 459 | 164 | 0 |
| EndfieldChenTool/Chen_Reextracted | 459 | 239 | 0 |

初始比较数据保存在同目录 `initial_pose_repair.json`；修正后的生成和重新加载检查保存在 `saved_pose_verification.json`、`rebuilt`。

## 修正

1. 在采样前记录原始位置、旋转和缩放。
2. 检查动画结束后，先恢复所有原始变换，再保存场景。
3. 重新加载保存的场景，逐个核对变换，并将结果写入 `savedPoseMatchesModel`。该项失败时，生成器不能报告成功。
4. 更新原项目的手工生成器和工具生成器，并将工具升级至 v0.1.1。

现在未播放时显示原始 T 字姿势；进入 Play 后由 Animator 正常播放所选动作。原始姿势不应被当成待机动画。

修改前的两个资源目录已备份到本说明同目录下的 `backup`。
