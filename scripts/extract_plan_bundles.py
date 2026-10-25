"""Execute a saved plan's Bundle extraction stage. No model or animation decoding."""
from __future__ import annotations

import argparse
from collections import defaultdict
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path, PureWindowsPath
import re
import subprocess
import sys
import uuid

from index_game_resources import (ROOT, assert_unchanged, digest, emit, normalize_game,
                                 read_indexes, read_manifest, within, write_json)

CONTEXT = {}


def timestamp():
    return datetime.now(timezone.utc).isoformat()


def publish():
    result = CONTEXT["result"]
    result["updatedAt"] = timestamp()
    write_json(CONTEXT["run_dir"] / "result.json", result)
    write_json(CONTEXT["task_dir"] / "result.json", result)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def resolve_plan(plan, records, manifest):
    by_exact, by_basename, live_paths = {}, defaultdict(list), defaultdict(list)
    for row in records:
        name = row["name"].replace("\\", "/")
        if not name.lower().endswith(".ab"):
            continue
        key = (row["group"].casefold(), name.casefold())
        by_exact[key] = row
        by_basename[(row["group"].casefold(), name.rsplit("/", 1)[-1].casefold())].append(row)
        live_paths[name.casefold().split("/windows/", 1)[-1]].append(row)
    refs = {}
    for bundle in plan["bundles"]:
        require(isinstance(bundle, dict) and isinstance(bundle.get("id"), str), "计划 Bundle 记录无效。")
        require(bundle["id"] not in refs, "计划 Bundle 标识重复。")
        require(isinstance(bundle.get("group"), str) and isinstance(bundle.get("md5"), str)
                and re.fullmatch(r"[0-9a-fA-F]{32}", bundle["md5"]), "计划 Bundle 分组或 MD5 无效。")
        group = bundle["group"].casefold()
        virtual = bundle.get("virtualPath", "")
        if virtual:
            require(isinstance(virtual, str), "计划虚拟路径无效。")
            row = by_exact.get((group, virtual.replace("\\", "/").casefold()))
            found = [row] if row else []
        else:
            found = by_basename.get((group, bundle["id"].casefold()), [])
        require(len(found) == 1, "计划资源无法唯一定位，请重新建立计划：" + bundle["id"])
        require(found[0]["data_md5"].upper() == bundle["md5"].upper(), "计划资源摘要已变化，请重新建立索引和计划：" + bundle["id"])
        refs[bundle["id"]] = found[0]
    seed_ids = set()
    resource_ids = set()
    for resource in plan["resources"]:
        require(isinstance(resource, dict) and isinstance(resource.get("id"), str)
                and resource["id"] not in resource_ids, "计划资源标识无效或重复。")
        resource_ids.add(resource["id"])
        require(isinstance(resource.get("bundleIds"), list), "计划资源缺少 Bundle 引用。")
        for bundle_id in resource["bundleIds"]:
            require(isinstance(bundle_id, str) and bundle_id in refs, "资源引用未记录在计划中。")
            seed_ids.add(bundle_id)
    require(set(plan["requestedResourceIds"]).issubset(resource_ids), "所选资源不在计划中。")
    require(seed_ids, "计划没有可提取的 Bundle。")
    manifest_paths = defaultdict(list)
    for bundle in manifest.bundles:
        manifest_paths[bundle.name.casefold()].append(bundle)
    selected, pending = {}, [refs[key] for key in sorted(seed_ids)]
    while pending:
        row = pending.pop()
        identity = (row["group"], row["name"])
        if identity in selected:
            continue
        selected[identity] = row
        name = row["name"].replace("\\", "/").casefold().split("/windows/", 1)[-1]
        matches = manifest_paths.get(name, [])
        require(len(matches) == 1, "清单中无法唯一定位所选资源：" + row["name"])
        for dependency in matches[0].direct_dependencies:
            name = manifest.bundles[dependency].name.casefold()
            available = live_paths.get(name, [])
            require(len(available) == 1, "清单依赖缺失或不唯一，停止提取：" + name)
            pending.append(available[0])
    return sorted(selected.values(), key=lambda row: (row["group"], row["name"]))


def output_for(task_dir, row):
    group = row["group"]
    basename = row["name"].replace("\\", "/").rsplit("/", 1)[-1]
    require(re.fullmatch(r"[a-zA-Z0-9_-]{1,64}", group), "无法安全输出此分组名称。")
    require(re.fullmatch(r"[a-zA-Z0-9][a-zA-Z0-9_.-]{0,160}\.ab", basename, re.IGNORECASE)
            and ".." not in basename and not PureWindowsPath(basename).is_reserved(), "无法安全输出此 Bundle 名称。")
    target = task_dir / "bundles" / group / basename
    require(within(target.resolve(), task_dir.resolve()), "输出路径超出了当前任务。")
    return target


def valid_existing(path, row):
    if not path.is_file() or path.stat().st_size != row["length"]:
        return False
    checksum = hashlib.md5()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(4 * 1024 * 1024), b""):
            checksum.update(block)
    return checksum.hexdigest().upper() == row["data_md5"]


def extract_one(chunk, row, target, on_progress):
    target.parent.mkdir(parents=True, exist_ok=True)
    temporary = target.with_name(target.name + ".partial-" + uuid.uuid4().hex)
    version, seed = (str(row["vfs_version"]), str(row["iv_seed"])) if row["use_encrypt"] else ("-", "-")
    command = [str(ROOT / "runtime/crypto/VfsCrypto.exe"), "--extract", str(chunk), str(row["offset"]),
               str(row["length"]), version, seed, row["data_md5"], str(temporary)]
    try:
        process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            text=True, encoding="utf-8", errors="replace",
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        tail = []
        for line in process.stdout:
            if line.startswith("@bytes "):
                on_progress(int(line[7:].strip()))
            else:
                tail.append(line.strip()); tail = tail[-10:]
        if process.wait() != 0:
            raise ValueError("Bundle 提取／MD5 校验失败：" + row["name"] + "\n" + "\n".join(tail))
        require(temporary.is_file() and temporary.stat().st_size == row["length"], "提取输出长度错误。")
        temporary.replace(target)
    finally:
        # Only the temporary file created by this attempt is removed. Existing final files are retained.
        temporary.unlink(missing_ok=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plan", required=True, type=Path)
    parser.add_argument("--run-id", required=True)
    args = parser.parse_args()
    plan_path = args.plan.resolve()
    require(plan_path.stat().st_size <= 256 * 1024 * 1024, "计划文件过大。")
    plan_bytes = plan_path.read_bytes()
    plan = json.loads(plan_bytes.decode("utf-8-sig"))
    require(plan.get("schema") == "endfield-character-plan-v1", "不支持这个计划格式。")
    task_id, run_id = uuid.UUID(plan["id"]).hex, uuid.UUID(args.run_id).hex
    options = plan["options"]
    workspace = Path(options["workspaceDirectory"]).resolve()
    task_dir = workspace / "tasks" / task_id
    require(plan_path == (task_dir / "plan.json").resolve() and within(task_dir.resolve(), workspace), "计划文件不在对应工作区任务中。")
    marker = workspace / "character-workspace.json"
    require(json.loads(marker.read_text(encoding="utf-8-sig")).get("schema") == "endfield-character-workspace-v1", "工作区标识不兼容。")
    game = normalize_game(Path(options["gameDirectory"]).resolve())
    require(not (within(workspace, game.parent) or within(game, workspace) or within(workspace, ROOT)
                 or within(ROOT, workspace)), "提取工作区必须独立于游戏和工具目录。")
    require(isinstance(plan.get("bundles"), list) and isinstance(plan.get("resources"), list)
            and isinstance(plan.get("requestedResourceIds"), list) and plan["requestedResourceIds"], "计划资源列表为空或无效。")
    lock = (task_dir / "extraction.lock").open("a+b")
    if os.name == "nt":
        import msvcrt
        if lock.seek(0, 2) == 0:
            lock.write(b"0"); lock.flush()
        lock.seek(0)
        try:
            msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError as error:
            raise ValueError("这个任务正在被另一个提取进程使用。") from error
    run_dir = task_dir / "runs" / run_id
    require(within(run_dir.resolve(), task_dir.resolve()), "运行记录目录超出了当前任务。")
    run_dir.mkdir(parents=True, exist_ok=True)
    CONTEXT.update(task_dir=task_dir, run_dir=run_dir, result={
        "schema": "endfield-bundle-extraction-v1", "taskId": str(uuid.UUID(task_id)), "runId": run_id,
        "status": "running", "message": "正在核对版本与依赖。", "startedAt": timestamp(),
        "outputDirectory": str(task_dir / "bundles"), "totalBundles": 0, "files": [],
        "reusedFiles": 0, "extractedFiles": 0, "bundleExtraction": "running",
        "objectDecoding": "not_implemented", "animationAdaptation": "not_implemented",
        "weights": "not_run", "unityPreview": "not_run", "playbackVerification": "not_run"})
    publish()
    emit("核对提取计划与当前游戏版本…", 1)
    fingerprint = digest(game.parent / "GameAssembly.dll")
    require(fingerprint == plan["gameAssemblySha256"].upper(), "计划对应的游戏版本已变化，请重新建立索引和计划。")
    metadata = run_dir / "metadata"
    metadata.mkdir(exist_ok=True)
    vfs = game / "StreamingAssets/VFS"
    records, signatures, formats = read_indexes(vfs, metadata)
    manifest, manifest_md5, manifest_format = read_manifest(records, vfs, metadata)
    selected = resolve_plan(plan, records, manifest)
    targets = {}
    for row in selected:
        target = output_for(task_dir, row)
        require(str(target).casefold() not in targets, "两个虚拟路径对应相同输出文件名，无法可靠输出。")
        targets[str(target).casefold()] = row
        chunk = vfs / row["group"] / (row["chunk_md5"] + ".chk")
        require(chunk.is_file() and chunk.stat().st_size >= row["offset"] + row["length"],
                "所需 CHK 未下载完整或缺失：" + str(chunk))
    require(plan_path.read_bytes() == plan_bytes, "执行期间计划文件已变化，请重新执行。")
    result = CONTEXT["result"]
    result.update(totalBundles=len(selected), gameAssemblySha256=fingerprint, manifestMd5=manifest_md5,
                  manifestVersion=manifest.version, blcFormats=formats, manifestFormat=manifest_format,
                  planSha256=hashlib.sha256(plan_bytes).hexdigest().upper())
    publish()
    total_bytes = sum(row["length"] for row in selected)
    completed = 0
    for index, row in enumerate(selected):
        target = output_for(task_dir, row)
        reused = valid_existing(target, row)
        emit(f"校验／提取 Bundle {index + 1}/{len(selected)}：{target.name}", 15 + int(80 * completed / max(1, total_bytes)))
        if not reused:
            def progress(amount):
                emit(f"提取 {index + 1}/{len(selected)}：{amount // (1024 * 1024)} MiB", 15 + int(80 * (completed + amount) / max(1, total_bytes)))
            extract_one(vfs / row["group"] / (row["chunk_md5"] + ".chk"), row, target, progress)
        result["files"].append({"group": row["group"], "virtualPath": row["name"], "path": str(target),
            "md5": row["data_md5"], "length": row["length"], "cached": reused})
        result["reusedFiles" if reused else "extractedFiles"] += 1
        completed += row["length"]
        result["message"] = f"已校验 {len(result['files'])}/{len(selected)} 个 Bundle。"
        publish()
    emit("核对提取期间的游戏与计划是否发生变化…", 97)
    assert_unchanged(vfs, signatures, game, fingerprint)
    require(plan_path.read_bytes() == plan_bytes, "执行期间计划文件已变化，本次结果未标为完成。")
    result.update(status="bundles_extracted", bundleExtraction="completed", finishedAt=timestamp(),
                  message=f"{len(selected)} 个 Bundle 已提取并通过 MD5 校验；模型／动画对象解析尚未执行。")
    publish()
    emit(result["message"], 100, "completed", report=str(task_dir / "result.json"))
    lock.close()


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    try:
        main()
    except Exception as error:
        if CONTEXT:
            CONTEXT["result"].update(status="failed", bundleExtraction="failed", message=str(error), finishedAt=timestamp())
            try:
                publish()
            except OSError:
                pass
        emit(str(error), kind="error")
        raise SystemExit(1)
