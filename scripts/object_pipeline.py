"""Inspect verified task Bundles and export explicitly selected objects. No Unity adaptation."""
from __future__ import annotations
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import uuid
from index_game_resources import ROOT, digest, emit, within, write_json
from extract_plan_bundles import require
from prepare_unity_adaptation import prepare_unity_package

STATE = {}


def read(path):
    path = Path(path)
    require(path.stat().st_size <= 256 * 1024 * 1024, "对象操作文件过大。")
    return json.loads(path.read_text(encoding="utf-8-sig"))


def md5(path):
    checksum = hashlib.md5()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(4 * 1024 * 1024), b""):
            checksum.update(block)
    return checksum.hexdigest().upper()


def publish():
    STATE["result"]["updatedAt"] = datetime.now(timezone.utc).isoformat()
    write_json(STATE["run"] / "operation.json", STATE["result"])
    write_json(STATE["task"] / "objects/result.json", STATE["result"])
    if STATE["result"]["mode"] == "prepare":
        write_json(STATE["task"] / "objects/preparation.json", STATE["result"])


def run_tool(relative, args):
    executable = ROOT / relative
    require(executable.is_file(), "对象解析组件缺失：" + str(executable))
    process = subprocess.Popen([str(executable), *map(str, args)], cwd=executable.parent,
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, encoding="utf-8", errors="replace",
        creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
    for line in process.stdout:
        emit(line.rstrip())
    return process.wait()


def checked_path(path, root):
    path = Path(path).resolve()
    require(within(path, root.resolve()), "对象操作路径超出了当前任务。")
    return path


def checked_inputs(inventory):
    base = checked_path(inventory["input"], STATE["task"])
    expected = set()
    for row in inventory["inputFiles"]:
        path = checked_path(row["path"], base)
        require(path.is_file() and path.stat().st_size == row["length"] and md5(path) == row["md5"],
                "解析快照已变化，请重新解析 Bundle。")
        expected.add(path)
    require(set(base.glob("*.ab")) == expected, "解析快照出现额外或缺失 Bundle，请重新解析。")
    return base


def inspect_bundles(bundle_result, plan_bytes, reuse_inventory=None, fork_snapshot=False):
    run = STATE["run"]
    prior = None
    prior_digest = None
    if reuse_inventory:
        prior = read(checked_path(reuse_inventory, STATE['task'] / 'objects'))
        prior_digest = digest(Path(reuse_inventory))
        require(prior.get('schema') == 'endfield-object-inventory-v1' and prior.get('taskId') == STATE['result']['taskId']
                and prior.get('bundleRunId') == bundle_result['runId']
                and prior.get('planSha256') == hashlib.sha256(plan_bytes).hexdigest().upper(), '旧解析快照与当前任务、提取或计划不符。')
        inputs = checked_inputs(prior)
        copied = {Path(row['path']).name.casefold(): row for row in prior['inputFiles']}
        if fork_snapshot:
            inputs = run / 'input'; inputs.mkdir()
            forked = {}
            for name, row in copied.items():
                target = inputs / Path(row['path']).name
                try:
                    os.link(row['path'], target)
                except OSError:
                    shutil.copy2(row['path'], target)
                forked[name] = dict(row, path=str(target))
            copied = forked
            emit(f'从 {len(copied)} 个已核对输入创建独立附件调查快照；保留旧快照。', 30)
        else:
            emit(f'复用 {len(copied)} 个已核对的解析输入；生成新证据记录，不扩展旧快照。', 30)
    else:
        inputs = run / "input"
        inputs.mkdir()
        copied = {}
        for index, row in enumerate(bundle_result["files"]):
            source = checked_path(row["path"], STATE["task"] / "bundles")
            require(source.is_file() and source.stat().st_size == row["length"] and md5(source) == row["md5"],
                    "Bundle 输出已变化，请先继续／重试 Bundle 提取：" + str(source))
            name = source.name
            if name.casefold() in copied:
                require(copied[name.casefold()]["md5"] == row["md5"], "不同 Bundle 使用相同文件名，无法可靠解析。")
                continue
            target = inputs / name
            shutil.copy2(source, target)
            require(md5(target) == row["md5"], "复制解析快照失败。")
            copied[name.casefold()] = {"path": str(target), "md5": row["md5"], "length": row["length"]}
            emit(f"准备解析快照 {index + 1}/{len(bundle_result['files'])}", 5 + int(25 * (index + 1) / len(bundle_result["files"])))
    report = run / "inventory.json"
    emit("读取实际模型、网格、Avatar 和动画对象…", 35)
    code = run_tool("runtime/object-probe/BundleObjectProbe.exe", ["--input", inputs, "--report", report])
    require(code == 0 and report.is_file(), "Bundle 对象解析失败，请查看本次日志。")
    inventory = read(report)
    require(inventory.get("schema") == "endfield-object-inventory-v1" and inventory.get("objects"), "没有解析到可列出的对象。")
    expand = not prior or fork_snapshot
    from role_bundle_discovery import add_role_model_inputs
    model_discovery = add_role_model_inputs(json.loads(plan_bytes.decode('utf-8-sig')), bundle_result, inventory, run, inputs, copied) if expand else prior.get('roleModelDiscovery', {'addedFiles': [], 'reason': 'reused-verified-snapshot'})
    if expand and model_discovery['addedFiles']:
        emit('重新解析包含模型入口候选的快照…', 40)
        code = run_tool("runtime/object-probe/BundleObjectProbe.exe", ["--input", inputs, "--report", report])
        require(code == 0, '模型入口候选解析失败。')
        inventory = read(report)
    from prop_reference_discovery import add_prop_reference_inputs
    prop_discovery = add_prop_reference_inputs(json.loads(plan_bytes.decode('utf-8-sig')), bundle_result, inventory, run, inputs, copied) if expand else prior.get('propReferenceDiscovery', {'addedFiles': [], 'reason': 'reused-verified-snapshot'})
    if expand and prop_discovery['addedFiles']:
        emit('重新解析包含附件实际反向引用的快照…', 42)
        code = run_tool("runtime/object-probe/BundleObjectProbe.exe", ["--input", inputs, "--report", report])
        require(code == 0, '附件引用候选解析失败。')
        inventory = read(report)
    from source_rig_discovery import add_source_rig_inputs
    discovery = add_source_rig_inputs(json.loads(plan_bytes.decode('utf-8-sig')), bundle_result, inventory, run, inputs, copied, read) if expand else prior.get('sourceRigDiscovery', {'addedFiles': [], 'reason': 'reused-verified-snapshot'})
    if expand and discovery['addedFiles']:
        emit('重新解析包含源骨架证据的完整快照…', 38)
        code = run_tool("runtime/object-probe/BundleObjectProbe.exe", ["--input", inputs, "--report", report])
        require(code == 0, '源骨架补充快照解析失败。')
        inventory = read(report)
    inventory['sourceRigDiscovery'] = discovery
    inventory['roleModelDiscovery'] = model_discovery
    inventory['propReferenceDiscovery'] = prop_discovery
    if prior:
        inventory['reusedInventoryPath'] = str(Path(reuse_inventory).resolve())
        inventory['reusedInventorySha256'] = prior_digest
        require(digest(Path(reuse_inventory)) == prior_digest, '调查期间原对象列表发生变化。')
        require(set(Path(prior['input']).glob('*.ab')) == {Path(row['path']) for row in prior['inputFiles']}, '调查期间原快照文件集合发生变化。')
    keys = [row["key"] for row in inventory["objects"]]
    require(len(keys) == len(set(keys)), "对象标识重复，无法精确选择。")
    inventory.update(taskId=STATE["result"]["taskId"], runId=STATE["result"]["runId"],
        bundleRunId=bundle_result["runId"], planSha256=hashlib.sha256(plan_bytes).hexdigest().upper(), inputFiles=list(copied.values()))
    for row in inventory["objects"]:
        if row.get("avatarTosPath"):
            row["avatarTosSha256"] = digest(checked_path(row["avatarTosPath"], run))
        if row.get('rigEvidencePath'):
            row['rigEvidenceSha256'] = digest(checked_path(row['rigEvidencePath'], run))
    checked_inputs(inventory)
    write_json(report, inventory)
    STATE["result"].update(status="inspected", inventoryPath=str(report),
        message=f"已列出 {len(keys)} 个实际对象，尚未导出或验证播放。")


def export_objects(inventory_path, selection_path, bundle_result, plan_bytes):
    inventory_path = checked_path(inventory_path, STATE["task"] / "objects")
    inventory = read(inventory_path)
    require(inventory["schema"] == "endfield-object-inventory-v1" and inventory["taskId"] == STATE["result"]["taskId"], "对象列表不属于当前任务。")
    require(inventory["bundleRunId"] == bundle_result["runId"]
            and inventory["planSha256"] == hashlib.sha256(plan_bytes).hexdigest().upper(), "提取记录或计划已变化，请重新解析对象。")
    STATE["result"]["inventoryPath"] = str(inventory_path)
    inputs = checked_inputs(inventory)
    selection = read(checked_path(selection_path, STATE["run"]))
    objects = {row["key"]: row for row in inventory["objects"]}
    root = objects.get(selection.get("modelKey"))
    avatar = objects.get(selection.get("avatarKey"))
    clips = [objects.get(key) for key in selection.get("clipKeys", [])]
    require(not selection.get("modelKey") or (root and root["kind"] == "GameObject" and root["isModelCandidate"]), "所选模型根节点无效。")
    require(not selection.get("avatarKey") or (avatar and avatar["kind"] == "Avatar"), "所选 Avatar 无效。")
    if root and root.get("linkedAvatarKey") and avatar:
        require(root["linkedAvatarKey"] == avatar["key"], "所选 Avatar 与模型实际 Animator 引用不一致。")
    elif avatar:
        STATE["result"]["warnings"].append("所选 Avatar 未由模型 Animator 引用确认；请核对骨架路径和单位。")
    require(all(clip and clip["kind"] == "AnimationClip" for clip in clips), "所选动画对象无效。")
    require(root or clips, "请选择模型或动画对象。")
    if STATE["result"]["mode"] == "prepare":
        require(root and clips and avatar, "生成适配包需要选择模型、Avatar 和动画。")
    require(len(clips) == len(set(row["key"] for row in clips)), "动画对象重复。")
    output = STATE["run"] / "exports"
    output.mkdir()
    failures = STATE["result"]["failures"]
    related = selection.get('relatedModelKeys') or []
    require(isinstance(related, list) and all(isinstance(key, str) for key in related), '关联模型选择无效。')
    assembly = None
    if root and inventory.get('serializedEvidencePath'):
        from model_assembly import resolve_assembly
        assembly = resolve_assembly(inventory, inventory_path, root['key'], related, [clip['key'] for clip in clips])
    else:
        require(not related, '旧对象解析缺少关联模型证据，请重新解析。')
        if root:
            STATE['result']['warnings'].append('旧快照没有模型组成及初始显隐证据，不能认定待机道具完整。')
    if root:
        emit("导出精确选择的模型根节点为 FBX…", 30)
        report = STATE["run"] / "model-report.json"
        arguments = ["--input", inputs, "--output", output / "model", "--root-name", root["name"],
            "--path-id", root["pathId"], "--root-source-asset", root["sourceAsset"], "--root-source-bundle", root["sourceBundle"],
            "--strict-selection", "true", "--report", report]
        if avatar:
            arguments += ["--avatar-path-id", avatar["pathId"], "--avatar-source-asset", avatar["sourceAsset"],
                          "--avatar-source-bundle", avatar["sourceBundle"]]
        from character_model_cache import model_revision, locked_cache, restore, publish as cache_model
        revision = model_revision(json.loads(plan_bytes.decode('utf-8-sig')), bundle_result, inventory, root, avatar)
        STATE['result']['modelRevision'] = revision
        workspace = json.loads(plan_bytes.decode('utf-8-sig'))['options']['workspaceDirectory']
        with locked_cache(workspace, revision) as cache:
            reused = restore(cache, output / 'model', report)
            code = 0 if reused else run_tool("runtime/object-model/ModelExportCompat.exe", arguments)
            exported_model = read(report) if report.is_file() else {}
            if not reused and code == 0 and exported_model.get('fbx_bytes', 0) > 0 and not exported_model.get('export_error'):
                cache_model(cache, output / 'model', exported_model)
        STATE['result']['modelExportReused'] = reused
        model = read(report) if report.is_file() else {}
        if code == 0 and model.get("fbx_bytes", 0) > 0 and (model.get("selected") or {}).get("mesh_count", 0) > 0 and not model.get("export_error"):
            register_output(model["fbx"], "FBX")
            for texture in model.get("textures", []):
                register_output(texture["path"], "Texture")
            for field in ("material_report", "material_properties_report", "avatar_retarget", "avatar_human_candidates"):
                if model.get(field):
                    register_output(model[field], "ModelMetadata")
            if model.get("unresolved_external_file_count", 0):
                STATE["result"]["warnings"].append("模型存在未解析的外部引用，详见 model-report.json。")
        else:
            failures.append("所选模型导出失败，详见 model-report.json 和运行日志。")
        if assembly is not None:
            write_json(output / 'model/model-assembly.json', assembly)
            register_output(output / 'model/model-assembly.json', 'ModelAssemblyEvidence')
    if clips:
        emit("解码精确选择的 AnimationClip 对象…", 65)
        report = STATE["run"] / "animation-report.json"
        clip_selection = STATE["run"] / "clips.json"
        write_json(clip_selection, clips)
        arguments = ["--input", inputs, "--output", output / "animations", "--name-contains", "",
                     "--selection", clip_selection, "--report", report, "--rig-catalog", inventory_path]
        if avatar:
            tos = checked_path(avatar["avatarTosPath"], inventory_path.parent)
            require(digest(tos) == avatar["avatarTosSha256"], "Avatar 路径表已变化，请重新解析对象。")
            arguments += ["--avatar-tos", tos]
        code = run_tool("runtime/object-animation/AnimationExportCompat.exe", arguments)
        animations = read(report) if report.is_file() else {}
        exports = animations.get("exports", [])
        if code != 0 or len(exports) != len(clips) or animations.get("failed_clip_count", 0):
            failures.append("部分或全部动画未能解码，详见 animation-report.json。")
        for row in exports:
            if row.get("curves", 0) <= 0 or row.get("keys", 0) <= 0:
                failures.append("动画为空：" + row.get("m_Name", "")); continue
            path = checked_path(row["output"], STATE["run"])
            yaml = path.read_text(encoding="utf-8-sig")
            if re.search(r"(?m)^        value:.*(?:NaN|Infinity|∞|\.nan|\.inf)", yaml):
                failures.append("动画包含非有限关键帧值：" + row["m_Name"]); continue
            register_output(path, "AnimationSource", curves=row["curves"], keys=row["keys"], unknownPaths=row["unknown_paths"])
            if row.get('path_evidence'):
                register_output(row['path_evidence'], 'AnimationPathEvidence')
            if row["unknown_paths"]:
                STATE["result"]["warnings"].append(row["m_Name"] + " 尚有未解析路径，不能直接用于播放。")
        for path in (output / 'animations').glob('rig-*.json'):
            register_output(path, 'SourceRigEvidence')
    checked_inputs(inventory)
    STATE["result"].update(status="partial" if failures else "exported_unverified", inventoryPath=str(inventory_path),
        message="导出结束；模型与原始动画尚未进行 Unity 适配或播放验证。")
    STATE["result"]["warnings"].append("动画为原始解码结果；未进行单位、播放模式、共享面部轨道或附加层适配。")
    if STATE["result"]["mode"] == "prepare":
        emit("准备独立 Unity 原生适配包…", 90)
        package = prepare_unity_package(STATE, selection, root, clips)
        for path in sorted(package.rglob("*")):
            if path.is_file():
                register_output(path, "UnityPackage")


def register_output(path, kind, **extra):
    path = checked_path(path, STATE["run"] / "exports")
    require(path.is_file() and path.stat().st_size > 0, "导出文件缺失或为空。")
    STATE["result"]["files"].append({"kind": kind, "path": str(path), "bytes": path.stat().st_size, "sha256": digest(path), **extra})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plan", required=True, type=Path)
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--mode", required=True, choices=("inspect", "export", "prepare"))
    parser.add_argument("--inventory", type=Path)
    parser.add_argument("--selection", type=Path)
    parser.add_argument("--reuse-snapshot", action='store_true')
    parser.add_argument("--fork-snapshot", action='store_true')
    args = parser.parse_args()
    plan_path = args.plan.resolve()
    plan = read(plan_path)
    plan_bytes = plan_path.read_bytes()
    require(json.loads(plan_bytes.decode("utf-8-sig")) == plan, "读取期间计划发生变化。")
    require(plan.get("schema") == "endfield-character-plan-v1", "计划格式不兼容。")
    task_id, run_id = uuid.UUID(plan["id"]), uuid.UUID(args.run_id).hex
    workspace = Path(plan["options"]["workspaceDirectory"]).resolve()
    task = (workspace / "tasks" / task_id.hex).resolve()
    require(within(task, workspace) and plan_path == task / "plan.json", "计划不在对应任务目录。")
    require(read(workspace / "character-workspace.json").get("schema") == "endfield-character-workspace-v1", "工作区标识不兼容。")
    require(not (within(workspace, ROOT) or within(ROOT, workspace)), "工作区必须独立于工具目录。")
    lock = (task / "extraction.lock").open("a+b")
    if os.name == "nt":
        import msvcrt
        if lock.seek(0, 2) == 0:
            lock.write(b"0"); lock.flush()
        lock.seek(0)
        try:
            msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError as error:
            raise ValueError("当前任务正在提取或解析，请稍后再试。") from error
    bundle_result = read(task / "result.json")
    require(bundle_result["schema"] == "endfield-bundle-extraction-v1" and bundle_result["taskId"] == str(task_id)
            and bundle_result["status"] == "bundles_extracted", "请先完成该任务的 Bundle 提取。")
    require(bundle_result.get("planSha256") == hashlib.sha256(plan_bytes).hexdigest().upper(), "计划已变化，请重新提取 Bundle。")
    require(bundle_result.get("files"), "任务没有已提取 Bundle。")
    run = task / "objects" / run_id
    checked_path(run, task)
    run.mkdir(parents=True, exist_ok=True)
    STATE.update(task=task, run=run, result={"schema": "endfield-object-operation-v1", "taskId": str(task_id),
        "runId": run_id, "bundleRunId": bundle_result["runId"], "mode": args.mode, "status": "running", "message": "正在读取对象。", "files": [],
        "warnings": [], "failures": [], "inventoryPath": None, "outputDirectory": str(run / "exports"),
        "adapted": False, "playbackVerified": False, "startedAt": datetime.now(timezone.utc).isoformat()})
    publish()
    if args.mode == "inspect":
        require(not args.reuse_snapshot or args.inventory, '复用解析快照需要明确的对象列表。')
        require(not args.fork_snapshot or args.reuse_snapshot, '附件调查需要复用已核对的旧快照。')
        inspect_bundles(bundle_result, plan_bytes, args.inventory if args.reuse_snapshot else None, args.fork_snapshot)
    else:
        require(not args.reuse_snapshot and not args.fork_snapshot, '只有重新解析操作可以复用已有快照。')
        require(args.inventory and args.selection, "导出需要对象列表和明确选择。")
        export_objects(args.inventory, args.selection, bundle_result, plan_bytes)
    require(plan_path.read_bytes() == plan_bytes, "执行期间计划已变化，结果未标为完成。")
    publish()
    emit(STATE["result"]["message"], 100, "completed", report=str(task / "objects/result.json"))
    lock.close()


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    try:
        main()
    except Exception as error:
        if STATE:
            STATE["result"].update(status="failed", message=str(error))
            try:
                publish()
            except OSError:
                pass
        emit(str(error), kind="error")
        raise SystemExit(1)
