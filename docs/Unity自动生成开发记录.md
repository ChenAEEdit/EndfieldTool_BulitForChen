# Unity 自动生成开发记录：0.8.0

日期：2026-10-25。范围：将工作台的独立适配包导入现有 Unity 项目，调用原生生成，再严格回读结果。

**交付状态：实现、编译、打包完成；完整自动流程尚未实测，实际播放未验证。** 没有用旧版导出、六条待机或膝部检查报告代替本版证据。本阶段没有启动 Unity、Blender、提取器或交互测试，也没有写入现有 Unity 项目。

## 1. 使用入口

运行 `D:/AAAChatGPT/EndfieldCharacterTool-Framework/EndfieldChenTool.exe`。

1. 从任务载入实际对象列表，选择模型根节点、Avatar 和动画。
2. 在“动画适配”确认单位、路径与排除规则，点击“生成 Unity 适配包”。
3. 检查该页显示的计划目标项目与编辑器，保存并关闭对应 Unity 编辑器。
4. 点击“导入并生成适配”。工具显示阶段、等待时间和日志入口。
5. 完成后点“打开生成目录”。需要把 `Character.prefab` 放进场景检查画面和动作。

使用现有项目 `D:/Unity 3D/Projects/Codex_ZMD`，编辑器 `D:/Unity 3D/Editor/6000.0.84f1/Editor/Unity.exe`。自动流程不创建新项目。已保存计划记录了自己的项目路径；环境页修改不会回写旧计划。

0.8.0 自动入口要求 0.8.0 新准备的包；0.7.0 旧包仍可手动复制、菜单生成与回读。

## 2. 自动流程

| 阶段 | 具体处理 |
| --- | --- |
| 检查任务 | 核对计划位置、任务 ID、适配包运行 ID、Bundle 记录与计划 SHA256 |
| 检查项目 | 现有 Assets／Packages／ProjectSettings、项目版本与 Unity.exe 产品版本一致；拒绝通过目录联接或符号链接导入 |
| 互斥 | 整个流程持有本任务字节范围锁；同一项目使用独立导入锁；检查 UnityLockfile 占用 |
| 检查源包 | 只复制本次报告列出的文件；逐个核对长度和 SHA256；模型、源动画、配置及编辑器入口必须属于本包 |
| 复制资源 | 先复制到项目 Library 下的本次暂存目录并核对，再移动到独立的 Assets 包目录 |
| 编辑器脚本 | 相同摘要直接复用；已记录或已知 0.7.0 原脚本备份后更新；未知或手工修改版本保留并停止 |
| Unity 生成 | 隐藏窗口批处理启动已有编辑器，只传本次配置和调用编号；原生创建 Generic 动画、控制器及 Prefab |
| 本次回执 | 必须有本次调用编号、配置路径和摘要一致的成功回执；Unity 退出码为 0 仍不足以确认完成 |
| 输出回读 | 核对配置、所有输入、任务／运行编号、每条所选动画、文件位置、长度和 SHA256，再记录已适配、未验证播放 |

Unity 编辑器目录和项目版本不一致时停止，避免自动升级项目。项目正被打开时停止并说明；工具不会结束用户已有的 Unity 编辑器。

## 3. 生成、复用和失败

每次准备包使用新目录：

`Assets/EndfieldCharacterExports/<任务前8位>_<准备运行ID>`

包目录的输入存在且完全一致时可以复用；输入变化或有额外普通文件时停止。Unity 自动产生的 `.meta` 可保留；`Generated` 是独立输出目录。

已有完整原生报告且所有文件核对通过时，明确记录 `reused=true`，不重新运行 Unity。没有把旧结果伪装成本次新生成。已有部分输出、修改过的输出或不完整报告时，保留内容并提示重新准备独立包。

导入取消、失败和超时会保留记录和已经完成的独立文件，不执行递归清空。重试可以复用完整输入；已有不完整生成内容时需要新包。取消只结束此次启动的 Unity 及其子进程。Unity 运行上限为 60 分钟；这与会话约定的停止工作时间无关，开发期间未启动该运行流程。

自动生成记录和原始导出、包准备记录分开。失败记录不会把成功导出的 FBX 和源动画改成不存在；任务页面会显示本次 Unity 失败／取消及来源包。

## 4. 文件位置

```text
任务目录/objects/
  preparation.json                      最近一次适配包记录
  unity-last-run.json                    最近一次自动导入／生成记录
  <准备运行ID>/
    operation.json                      适配包阶段结果
    exports/unity-package/              不修改的原始准备包
    unity-runs/<调用ID>/
      run.json                          阶段、状态、项目、时间、退出码、复用标记
      unity.log                         本次 Unity 日志；未启动则可能不存在
      native-completion.json            原生入口实际执行后才出现
      project-backup/
        CharacterAnimationAdapter.cs    更新共享编辑器脚本时的备份

现有 Unity 项目/
  Assets/EndfieldCharacterWorkbench/Editor/CharacterAnimationAdapter.cs
  Assets/EndfieldCharacterExports/<任务短ID>_<准备运行ID>/
    adaptation.json
    Model/
    Sources/
    Generated/
      Clip_<对象ID>.anim
      Character.controller
      Character.prefab
      adaptation-result.json
  Library/EndfieldCharacterWorkbench/
    import.lock                         项目导入互斥文件
    adapter-install.json                已记录共享脚本版本的摘要
    staging/<调用ID>/                   暂存；失败时可能保留
```

正常成功后源包仍保留在任务目录，项目中为独立副本。共享脚本的 `.meta` 保留；备份放在任务目录，避免重复脚本参与 Unity 编译。当前没有自动清理历史包、备份和暂存目录。

## 5. 代码入口

| 文件 | 职责 |
| --- | --- |
| `Framework/Services/UnityAdaptationRunner.cs` | 自动导入、互斥、文件校验、备份、子进程、取消和运行记录 |
| `Framework/Services/UnityAdaptationResults.cs` | 手动／自动共用的结果回读；持锁内部入口避免重复获取同一任务锁 |
| `Framework/AnimationPage.cs` | 新按钮、目标路径、等待进度、日志与生成目录入口 |
| `Framework/WorkspaceForm.cs` | 新版本标题、任务中的 Unity 运行状态与细节 |
| `templates/workspace/CharacterAnimationAdapter.cs` | 新增 `BuildBatch`、本次回执与调用编号；菜单入口保留 |
| `scripts/prepare_unity_adaptation.py` | 0.8.0 配置和包内操作说明 |
| `scripts/package_framework.ps1` | 构建及更新运行目录，保留旧专用工具 |

回读现已额外核对项目中实际配置及源文件；每条报告的输出必须对应本次选中的对象 ID 和名称。原生生成失败时清除 `adapted` 标记；新建 Generated 目录后显式同步刷新 Unity 资产库。

## 6. 编译与后续验证范围

本次主工作台及 Unity 模板均使用本机依赖编译，零警告、零错误。打包脚本仅构建和复制，不运行工具链。Python 准备脚本只做语法编译。

待用户要求验证时，建议按顺序执行：

1. 以一条专属待机生成新包，检查完整自动流程和全部本次回执／文件。
2. 检查再次执行是否明确复用完整结果，未知脚本与修改输出是否被保留。
3. 检查项目打开、错误版本、编译失败、取消与超时分支。
4. 在独立场景检查动画实际播放与面部／膝部画面；不能仅依赖数值或 Prefab 存在。
5. 获得有效基础证据后，再考虑自动创建播放场景、片段切换与更多角色。

这些是尚未执行的后续事项，本阶段没有新增或运行测试。

## 7. 当前限制

当前原生适配仍为 Generic 直接骨骼曲线。没有新增 Humanoid 重定向、游戏膝部修正求解器、IK、实时布料、专用着色器或跨引擎动画导出。专属行走不能自动套用共享行走的膝部候选处理；两条行走的历史诊断见 `docs/膝部诊断记录.md`。

本版重点是自动执行与结果追踪，不代表所有角色、游戏版本和动作类型均已兼容。发布目录中不加入本次游戏模型或动画资源。
