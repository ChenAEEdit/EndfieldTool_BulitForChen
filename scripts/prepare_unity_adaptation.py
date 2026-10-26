"""Prepare an isolated Unity package; actual adaptation runs through Unity's native API."""
import json
import math
from pathlib import Path
import shutil
from index_game_resources import ROOT, digest, within, write_json
from extract_plan_bundles import require


def prepare_unity_package(state, selection, model, clips):
    require(model and clips, "生成适配包需要选择模型和至少一条动画。")
    require(not state["result"]["failures"], "导出存在失败，请先解决导出问题。")
    rules = selection.get("adaptation")
    require(isinstance(rules, dict), "缺少明确的动画适配设置。")
    for name in ("modelImportScale", "positionScale"):
        value = rules.get(name)
        require(type(value) in (float, int) and math.isfinite(value) and .0001 <= value <= 10000, "单位倍率无效：" + name)
    for name in ("dropUnmatched", "dropSceneTranslation", "forceLoop"):
        require(type(rules.get(name)) is bool, "适配开关无效：" + name)
    require(type(rules.get("createPreviewScene", False)) is bool, "预览场景开关无效。")
    require(isinstance(rules.get("normalizeRoot"), str) and isinstance(rules.get("excludedDescendants"), list)
            and isinstance(rules.get("excludedNodes"), list)
            and all(isinstance(path, str) and path for path in rules["excludedDescendants"] + rules["excludedNodes"]), "根节点或排除路径无效。")
    run = state["run"]
    model_report = json.loads((run / "model-report.json").read_text(encoding="utf-8-sig"))
    animation_report = json.loads((run / "animation-report.json").read_text(encoding="utf-8-sig"))
    require(model_report.get("fbx_bytes", 0) > 0 and len(animation_report["exports"]) == len(clips), "适配源文件不完整。")
    package = run / "exports/unity-package"
    asset_root = "Assets/EndfieldCharacterExports/" + state["result"]["taskId"].replace("-", "")[:8] + "_" + state["result"]["runId"]
    target = package / asset_root
    (target / "Model").mkdir(parents=True)
    (target / "Sources").mkdir()
    (target / 'Evidence').mkdir()
    inputs = []

    def copy(source, relative):
        source = Path(source).resolve()
        require(within(source, (run / "exports").resolve()) and source.is_file(), "适配源文件必须属于本次导出。")
        sha = digest(source)
        destination = target / relative
        shutil.copy2(source, destination)
        require(digest(destination) == sha, "适配包复制校验失败。")
        inputs.append({"path": relative, "sha256": sha})

    for source in sorted((run / "exports/model").rglob("*")):
        if source.is_file():
            copy(source, "Model/" + source.relative_to(run / "exports/model").as_posix())
    definitions = []
    by_identity = {(row["sourceAsset"], row["sourceBundle"], row["pathId"]): row for row in clips}
    for exported in animation_report["exports"]:
        identity = (exported["source_asset_file"], exported["source_bundle"], str(exported["m_PathID"]))
        require(identity in by_identity, "动画报告来源与选择不一致。")
        row = by_identity[identity]
        relative = "Sources/" + row["key"] + ".anim"
        copy(exported["output"], relative)
        definition = {"id": row["key"], "name": row["name"], "source": relative}
        if exported.get('path_evidence'):
            evidence = json.loads(Path(exported['path_evidence']).read_text(encoding='utf-8-sig'))
            audit_relative = 'Evidence/' + row['key'] + '.paths.json'
            copy(exported['path_evidence'], audit_relative)
            definition['pathEvidence'] = audit_relative
            key = rules.get('sourceRigKey', '')
            if key:
                candidates = [rig for rig in evidence['rigs'] if rig['key'] == key]
                require(len(candidates) == 1, '所选动作原骨架不属于当前解析快照，请重新解析并选择。')
                rig = candidates[0]
                # The user chooses between real candidates. Coverage is reported,
                # not used to pick an arbitrary tied rest pose automatically.
                require(rig['matchedPaths'] > 1, '所选源骨架无法解释本动画的Transform路径。')
                source_relative = 'Evidence/rig-' + key + '.json'
                if not (target / source_relative).exists():
                    copy(rig['file'], source_relative)
                definition['sourceRig'] = source_relative
        elif rules.get('sourceRigKey'):
            require(False, '旧对象解析结果缺少源骨架证据，请重新解析。')
        definitions.append(definition)
    config = {"schema": "endfield-unity-adaptation-config-v1", "taskId": state["result"]["taskId"],
        "runId": state["result"]["runId"], "adapterVersion": "0.12.0", "modelKey": model["key"],
        "model": "Model/" + Path(model_report["fbx"]).relative_to(run / "exports/model").as_posix(),
        "modelImportScale": rules["modelImportScale"],
        "rules": {key: rules[key] for key in ("positionScale", "dropUnmatched", "dropSceneTranslation", "forceLoop", "normalizeRoot", "excludedDescendants", "excludedNodes")},
        "clips": definitions, "inputs": inputs, "preview": {"enabled": rules.get("createPreviewScene", False)}}
    write_json(target / "adaptation.json", config)
    editor = package / "Assets/EndfieldCharacterWorkbench/Editor"
    editor.mkdir(parents=True)
    shutil.copy2(ROOT / "templates/workspace/CharacterAnimationAdapter.cs", editor / "CharacterAnimationAdapter.cs")
    runtime = package / "Assets/EndfieldCharacterWorkbench/Runtime"
    runtime.mkdir(parents=True)
    shutil.copy2(ROOT / "templates/workspace/CharacterPreviewControls.cs", runtime / "CharacterPreviewControls.cs")
    (package / "使用说明.md").write_text(
        "# Unity 原生动画适配包\n\n"
        "推荐：在工作台“动画适配”页点击“导入并生成适配”，工具使用计划中现有的 Unity 项目。"
        "请先保存并关闭该项目的 Unity 编辑器；生成后自动核对本次回执与输出，仍需人工检查播放。\n\n"
        "以下为手动操作方法：\n\n"
        "1. 将本目录的 Assets 内容复制到现有 Unity 项目的 Assets 下。\n"
        "2. 等待 Unity 导入和编译；选择 `" + asset_root + "/adaptation.json`。\n"
        "3. 执行菜单 Endfield Character Tool → 适配所选配置并生成预览 Prefab。\n"
        "4. 在同目录 Generated 下查看独立动画、控制器、Character.prefab 和 adaptation-result.json。\n"
        "5. 回到工作台点击读取 Unity 适配结果。若勾选了生成预览场景，在 Generated 下打开 CharacterPreview.unity，"
        "进入 Play 后可切换、暂停、重播、调速和逐帧查看。也可将 Prefab 放入自己的场景。\n\n"
        "单位倍率、排除规则来自本次明确设置。根据主体 Transform 曲线和实际动态肌肉通道选择 Generic 或 Humanoid；"
        "人形模式要求唯一匹配模型的源人形映射。生成完成后使用工作台“打开并播放预览”，无需再次生成。\n\n"
        "源动画保留，生成结果尚未验证播放。若有未匹配轨道，默认停止并记录；"
        "要忽略轨道请在工具中明确勾选并生成新的包。已生成的文件不会自动覆盖。\n",
        encoding="utf-8")
    state["result"].update(status="unity_prepared", unityPackageDirectory=str(package), unityAssetRoot=asset_root,
        unityConfigSha256=digest(target / "adaptation.json"), expectedAdaptedClips=len(definitions),
        message="Unity 适配包已准备；请在 Unity 中执行生成，再读取适配结果。尚未适配或验证播放。")
    state["result"]["warnings"].append("预览按主体曲线选择播放模式；未重建游戏 IK、膝部修正求解器或布料物理，需要实际播放检查。")
    return package
