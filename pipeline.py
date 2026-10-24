"""Local, version-locked Chen extraction and Unity preparation pipeline."""
from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import struct
import subprocess
import sys
import threading
import time
import traceback
from datetime import datetime

ROOT = Path(__file__).resolve().parent
sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.stderr.reconfigure(encoding="utf-8", errors="replace")
sys.path.insert(0, str(ROOT / "scripts"))
from endfield_vfs import chacha20, parse_blc_v4

VERSION = "0.1.3"
ASSET_ROOT = "Assets/EndfieldChenTool"
UNITY_TYPE = "EndfieldChenTool.Generated.SceneBuilder"
log_file = None


def emit(message, progress=None, kind="log", **extra):
    event = {"type": kind, "message": str(message), **extra}
    if progress is not None:
        event["progress"] = progress
    print("@event " + json.dumps(event, ensure_ascii=False), flush=True)
    if log_file:
        log_file.write(datetime.now().strftime("%H:%M:%S ") + str(message) + "\n")
        log_file.flush()


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def write_json(path, document):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_name(path.name + ".tmp")
    temp.write_text(json.dumps(document, ensure_ascii=False, indent=2), encoding="utf-8")
    temp.replace(path)


def digest(path, algorithm="sha256"):
    h = hashlib.new(algorithm)
    with Path(path).open("rb") as stream:
        for data in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(data)
    return h.hexdigest().upper()


def within(path, parent):
    try:
        Path(path).resolve().relative_to(Path(parent).resolve())
        return True
    except ValueError:
        return False


def run(command, label, cwd=None, timeout=600, env=None):
    emit(label)
    options = {"cwd": str(cwd or ROOT), "stdout": subprocess.PIPE, "stderr": subprocess.STDOUT,
               "text": True, "encoding": "utf-8", "errors": "replace", "env": env}
    if os.name == "nt":
        options["creationflags"] = subprocess.CREATE_NO_WINDOW
    process = subprocess.Popen([str(x) for x in command], **options)
    expired = threading.Event()

    def terminate():
        expired.set()
        if os.name == "nt":
            subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                           creationflags=subprocess.CREATE_NO_WINDOW)
        else:
            process.kill()

    timer = threading.Timer(timeout, terminate)
    timer.daemon = True
    timer.start()
    tail = []
    try:
        for line in process.stdout:
            line = line.rstrip()
            if line:
                tail.append(line)
                tail = tail[-12:]
                emit(line)
        code = process.wait()
    finally:
        timer.cancel()
    if expired.is_set():
        raise RuntimeError(f"{label}超过 {timeout} 秒，已停止。请查看日志。")
    if code:
        raise RuntimeError(f"{label}失败，退出码 {code}。\n" + "\n".join(tail))


def normalize_game(path):
    path = Path(path).expanduser().resolve()
    candidates = [path, path / "Endfield_Data", path / "games/Arknights Endfield/Endfield_Data"]
    for candidate in candidates:
        if (candidate / "StreamingAssets/VFS").is_dir():
            return candidate
    raise ValueError("所选目录中找不到 Endfield_Data/StreamingAssets/VFS，请选择游戏目录或 Endfield_Data。")


def unity_locked(project):
    lock = Path(project) / "Temp/UnityLockfile"
    if os.name != "nt" or not lock.exists():
        return False
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateFileW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32,
                                  ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p]
    kernel.CreateFileW.restype = ctypes.c_void_p
    kernel.CloseHandle.argtypes = [ctypes.c_void_p]
    handle = kernel.CreateFileW(str(lock), 0x80000000, 0, None, 3, 0x80, None)
    if handle == ctypes.c_void_p(-1).value:
        return ctypes.get_last_error() in (32, 33)
    kernel.CloseHandle(handle)
    return False


def check_environment(args, profile):
    game = normalize_game(args.game)
    workspace = Path(args.workspace).expanduser().resolve()
    if within(workspace, game.parent) or within(game, workspace):
        raise ValueError("输出目录必须独立于游戏安装目录。")
    if within(workspace, ROOT) or within(ROOT, workspace):
        raise ValueError("请选择工具安装目录之外的输出目录，例如 D:\\AAAChatGPT\\EndfieldChenWorkspace。")
    if ";" in str(workspace):
        raise ValueError("输出目录不能含分号；底层导出器用分号分隔依赖目录。")
    assembly = game.parent / "GameAssembly.dll"
    if not assembly.is_file():
        raise ValueError("找不到游戏根目录中的 GameAssembly.dll，无法确认游戏版本。")
    emit("确认游戏版本…", 2)
    fingerprint = digest(assembly)
    if fingerprint != profile["game_assembly_sha256"]:
        raise ValueError("游戏版本与本工具已验证的配置不同。\n"
                         f"支持：{profile['build']}\n当前 GameAssembly SHA256：{fingerprint}\n"
                         "请更新角色配置后再提取；本次未开始导出。")
    required = [ROOT / "runtime/model/ModelExportCompat.exe",
                ROOT / "runtime/animation/AnimationExportCompat.exe",
                ROOT / "runtime/animation/Ruri.ACL.dll", ROOT / "runtime/crypto/VfsCrypto.exe", ROOT / "templates/SceneBuilder.cs",
                ROOT / "templates/PlaybackControls.cs"]
    for path in required:
        if not path.is_file():
            raise ValueError(f"工具文件不完整：{path}。请完整解压工具文件夹。")
    dotnet = shutil.which("dotnet")
    if not dotnet:
        raise ValueError("找不到 .NET 10 运行时，请安装 .NET 10 后重试。")
    result = subprocess.run([dotnet, "--list-runtimes"], capture_output=True, text=True,
                            encoding="utf-8", errors="replace", timeout=15,
                            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
    if not re.search(r"Microsoft.NETCore.App 10\.", result.stdout):
        raise ValueError("底层导出器需要 .NET 10 运行时。")
    if args.weights and not Path(args.blender).is_file():
        raise ValueError("已选择权重检查，请选择有效的 blender.exe，或取消该选项。")
    project = None
    if args.unity_project:
        project = Path(args.unity_project).resolve()
        if not (project / "Assets").is_dir() or not (project / "ProjectSettings/ProjectVersion.txt").is_file():
            raise ValueError("Unity 项目目录无效，需要包含 Assets 和 ProjectSettings。")
        if not Path(args.unity).is_file():
            raise ValueError("请选择有效的 Unity.exe。")
        if within(workspace, project) or within(project, workspace):
            raise ValueError("输出工作区与 Unity 项目需要使用独立目录。")
        version_text = (project / "ProjectSettings/ProjectVersion.txt").read_text(encoding="utf-8-sig")
        if "6000.0.84f1" not in version_text:
            raise ValueError("本版 Unity 导入器已验证的项目版本为 6000.0.84f1，请用对应版本的项目。")
        if unity_locked(project):
            raise ValueError("该 Unity 项目正在打开。请先关闭它的编辑器窗口，再运行导入；工具会使用同一个项目。")
        asset_dir = project / ASSET_ROOT
        marker = asset_dir / "tool-generated.json"
        if asset_dir.exists() and any(asset_dir.iterdir()) and not marker.exists():
            raise ValueError(f"{asset_dir} 已有文件且不是本工具生成的目录，请先改名保存该目录。")
        if marker.exists() and read_json(marker).get("tool") != "EndfieldChenTool":
            raise ValueError("Unity 目标目录的生成标记不匹配，已停止覆盖。")
    emit(f"环境通过：陈千语 / {profile['build']} / .NET 10", 5)
    return game, workspace, project


class Workspace:
    def __init__(self, root, fingerprint):
        self.root = root
        self.fingerprint = fingerprint
        marker = root / "workspace.json"
        if marker.exists():
            document = read_json(marker)
            if document.get("schema") != "endfield-chen-tool-workspace-v1":
                raise ValueError("输出目录包含其他类型的工作区，请选择一个新目录。")
            if document.get("game_assembly_sha256") != fingerprint:
                raise ValueError("该工作区来自其他游戏版本，请使用新的输出目录。")
        elif root.exists() and any(root.iterdir()):
            raise ValueError("输出目录已有文件且没有本工具的工作区标记，请选择空目录。")
        root.mkdir(parents=True, exist_ok=True)
        self.lock = (root / ".run.lock").open("a+b")
        if os.name == "nt":
            import msvcrt
            self.lock.seek(0)
            if not self.lock.read(1):
                self.lock.write(b"0")
                self.lock.flush()
            self.lock.seek(0)
            try:
                msvcrt.locking(self.lock.fileno(), msvcrt.LK_NBLCK, 1)
            except OSError:
                raise ValueError("同一个工作区已有任务在运行。")
        self.state_path = root / "cache.json"
        self.state = read_json(self.state_path) if self.state_path.exists() else {}
        write_json(marker, {"schema": "endfield-chen-tool-workspace-v1", "tool_version": VERSION,
                            "game_assembly_sha256": fingerprint, "character": "chen"})

    def cached(self, name, signature, force=False):
        entry = self.state.get(name, {})
        if force or entry.get("signature") != signature:
            return False
        for file, expected in entry.get("artifacts", {}).items():
            path = self.root / file
            if not path.is_file() or digest(path) != expected:
                return False
        return bool(entry.get("artifacts"))

    def complete(self, name, signature, files):
        artifacts = {str(Path(p).relative_to(self.root)): digest(p) for p in files}
        self.state[name] = {"signature": signature, "artifacts": artifacts}
        write_json(self.state_path, self.state)


def extract_bundles(game, workspace, profile, selected):
    roles = {"model", *selected}
    records = [b for b in profile["bundles"] if roles.intersection(b["roles"])]
    vfs = game / "StreamingAssets/VFS"
    target = workspace.root / "source/bundles"
    target.mkdir(parents=True, exist_ok=True)
    indexes = {}
    for group in sorted({b["group"] for b in records}):
        emit(f"读取资源索引 {group}…", 7)
        source = vfs / group / f"{group}.blc"
        if not source.is_file():
            raise RuntimeError(f"缺少 VFS 索引：{source}")
        raw = source.read_bytes()
        index_dir = workspace.root / "source/indexes"
        index_dir.mkdir(parents=True, exist_ok=True)
        decoded = index_dir / (group + ".bin")
        run([ROOT / "runtime/crypto/VfsCrypto.exe", source, decoded], "解码资源索引 " + group)
        entries = {}
        wanted = {item["name"] for item in records if item["group"] == group}
        def collect(record):
            name = Path(record["name"]).name
            if name in wanted:
                entries[name] = record
        parse_blc_v4(decoded.read_bytes(), sample_limit=0, file_visitor=collect)
        indexes[group] = (struct.unpack_from("<I", raw, 0)[0], entries)
    report = []
    reused = 0
    for index, item in enumerate(records):
        version, entries = indexes[item["group"]]
        record = entries.get(item["name"])
        if not record or record["data_md5"] != item["md5"]:
            raise RuntimeError(f"资源与已验证配置不一致：{item['name']}。已停止，请更新配置。")
        output = target / item["name"]
        reuse = output.is_file() and digest(output, "md5") == record["data_md5"]
        if reuse:
            reused += 1
        else:
            chunk = vfs / item["group"] / f"{record['chunk_md5']}.chk"
            with chunk.open("rb") as stream:
                stream.seek(record["offset"])
                data = stream.read(record["length"])
            if record["use_encrypt"]:
                data = chacha20(data, struct.pack("<IQ", version, record["iv_seed"]))
            if hashlib.md5(data).hexdigest().upper() != record["data_md5"]:
                raise RuntimeError(f"资源 MD5 检查失败：{item['name']}")
            temporary = output.with_suffix(".ab.partial")
            temporary.write_bytes(data)
            temporary.replace(output)
        report.append({"name": item["name"], "md5": item["md5"], "roles": item["roles"], "cached": reuse})
        if index % 8 == 0 or index == len(records) - 1:
            emit(f"资源检查 {index + 1}/{len(records)}", 8 + int(20 * (index + 1) / len(records)))
    write_json(workspace.root / "reports/extraction.json", {"passed": True, "count": len(report), "reused": reused, "files": report})
    emit(f"资源全部通过 MD5 检查，复用 {reused}/{len(records)} 个 Bundle。", 28)
    return target


def resolve_animation(source, avatar_path, target, report_path):
    tos = read_json(avatar_path)["avatar"]["m_TOS"]
    by_hash = {int(k): v for k, v in tos.items()}
    allowed = set(tos.values()) | {""}
    excluded = []
    resolved = set()

    def block(match):
        text = match[0]
        path = re.search(r"^    path: ?(.*)$", text, re.M)
        if not path:
            return text
        value = path[1]
        hashed = re.fullmatch(r"path_(\d+)", value)
        if hashed and int(hashed[1]) in by_hash:
            value = by_hash[int(hashed[1])]
            text = text[:path.start()] + "    path: " + value + text[path.end():]
        if value not in allowed:
            excluded.append(value)
            return ""
        resolved.add(value)
        return text

    original = Path(source).read_text(encoding="utf-8-sig")
    result = re.sub(r"^  - curve:\n.*?(?=^  - curve:|^  m_|\Z)", block, original, flags=re.M | re.S)
    result = re.sub(r"^(  m_\w+Curves):\n(?=  m_)", r"\1: []\n", result, flags=re.M)
    unknown = re.findall(r"^    path: path_\d+$", result, re.M)
    if unknown or len(resolved - {""}) < 30:
        raise RuntimeError("动画没有正确映射到陈千语骨架，已停止导入。")
    if re.search(r"^        value:.*(?:NaN|Infinity|∞|\.nan|\.inf)", result, re.M):
        raise RuntimeError("动画包含非有限关键帧值，已停止导入。")
    Path(target).parent.mkdir(parents=True, exist_ok=True)
    Path(target).write_text(result, encoding="utf-8")
    write_json(report_path, {"passed": True, "source": str(source), "retained_path_count": len(resolved),
                            "excluded_path_count": len(set(excluded)), "excluded_curve_count": len(excluded),
                            "unknown_paths": len(unknown)})


def export_assets(workspace, profile, bundles, animations, signature, force):
    root = workspace.root
    model = root / "assets/model"
    model.mkdir(parents=True, exist_ok=True)
    report = model / "model_export_report.json"
    if workspace.cached("model", signature, force):
        emit("复用已校验的模型导出。", 40)
    else:
        run([ROOT / "runtime/model/ModelExportCompat.exe", "--input", bundles, "--output", model,
             "--root-name", profile["root_name"], "--path-id", profile["root_path_id"], "--report", report], "导出模型与贴图")
        result = read_json(report)
        if result.get("export_error") or not result.get("avatar_retarget_available"):
            raise RuntimeError("模型或 Avatar 导出不完整，请查看 model_export_report.json。")
        selected = result.get("selected", {})
        if selected.get("mesh_count") != profile["mesh_count"] or selected.get("vertex_count") != profile["vertex_count"]:
            raise RuntimeError("导出模型的网格或顶点数量与配置不符，已停止。")
        workspace.complete("model", signature, [p for p in model.iterdir() if p.is_file()])
    result = read_json(report)
    fbx = model / Path(result["fbx"]).name
    avatar = model / "avatar_retarget.json"
    if not fbx.is_file() or not avatar.is_file():
        raise RuntimeError("缓存中的模型文件不存在，请勾选重新导出后重试。")
    emit("模型、贴图和 Avatar 就绪。", 42)
    files = {}
    for index, mode in enumerate(animations):
        entry = profile["animations"][mode]
        target = root / "assets/animations" / entry["source_asset"]
        path_report = root / "reports" / f"{mode}_paths.json"
        stage = f"animation_{mode}"
        if workspace.cached(stage, signature, force):
            emit(f"复用已校验的动画：{entry['label']}")
        else:
            raw_dir = root / "source/decoded" / mode
            export_report = raw_dir / "export_report.json"
            run([ROOT / "runtime/animation/AnimationExportCompat.exe", "--input", bundles,
                 "--output", raw_dir, "--name-contains", entry["clip_name"], "--report", export_report], f"解码动画：{entry['label']}")
            exported = read_json(export_report)
            clips = [c for c in exported.get("exports", []) if c["m_Name"] == entry["clip_name"]]
            if exported.get("failed_clip_count") or len(clips) != 1 or clips[0]["curves"] <= 0 or clips[0]["keys"] <= 0:
                raise RuntimeError("导出器没有生成有效动画曲线，请查看动画导出报告。")
            resolve_animation(clips[0]["output"], avatar, target, path_report)
            workspace.complete(stage, signature, [target, path_report, export_report])
        files[mode] = target
        emit(f"动画准备完成：{entry['label']}", 48 + int(12 * (index + 1) / len(animations)))
    return fbx, avatar, files


def audit_weights(args, workspace, fbx, signature):
    if not args.weights:
        emit("未选择 Blender 权重检查；不会将其标记为通过。", 65)
        return {"status": "not_requested"}
    report = workspace.root / "reports/weights.json"
    blend = workspace.root / "assets/Chen.blend"
    if not workspace.cached("weights", signature, args.force):
        run([args.blender, "--background", "--factory-startup", "--python", ROOT / "scripts/audit_model.py",
             "--", "--fbx", fbx, "--report", report, "--blend", blend], "检查模型与骨骼权重")
        result = read_json(report)
        if not result.get("passed"):
            raise RuntimeError("模型权重检查未通过，请查看 reports/weights.json。")
        workspace.complete("weights", signature, [report, blend])
    emit("模型与权重检查通过。", 65)
    return {"status": "passed", "report": str(report)}


def prepare_unity(workspace, fbx, avatar, files):
    stage = workspace.root / "unity/EndfieldChenTool"
    for folder in ("Model", "Animations", "Textures", "Materials", "Editor"):
        (stage / folder).mkdir(parents=True, exist_ok=True)
    shutil.copy2(fbx, stage / "Model/Chen.fbx")
    shutil.copy2(avatar, stage / "Model/avatar_retarget.json")
    for path in fbx.parent.glob("T_actor_chen_*_D.png"):
        shutil.copy2(path, stage / "Textures" / path.name)
    for path in files.values():
        shutil.copy2(path, stage / "Animations" / path.name)
    for source, target in [("SceneBuilder.cs", "Editor/SceneBuilder.cs"), ("PlaybackControls.cs", "PlaybackControls.cs")]:
        shutil.copy2(ROOT / "templates" / source, stage / target)
    shutil.copy2(ROOT / "templates/Unity使用说明.md", stage / "使用说明.md")
    write_json(stage / "tool-generated.json", {"tool": "EndfieldChenTool", "version": VERSION, "workspace": str(workspace.root)})
    emit("Unity 导入包准备完成。", 70)
    return stage


def run_unity(args, project, method, report_dir):
    report_dir.mkdir(parents=True, exist_ok=True)
    expected = report_dir / ("runtime_verification.json" if "Runtime" in method else "verification.json")
    if expected.exists():
        history = report_dir / "history"
        history.mkdir(exist_ok=True)
        expected.replace(history / (str(time.time_ns()) + "-" + expected.name))
    env = os.environ.copy()
    env["CHEN_REPORT_DIR"] = str(report_dir)
    log = report_dir / ("runtime.log" if "Runtime" in method else "unity.log")
    try:
        run([args.unity, "-batchmode", "-projectPath", project, "-executeMethod", UNITY_TYPE + "." + method,
             "-logFile", log], f"Unity：{method}", cwd=project, env=env, timeout=600)
    except Exception as error:
        if log.exists():
            text = log.read_text(encoding="utf-8-sig", errors="replace")
            important = [line for line in text.splitlines() if re.search(r"error CS|Exception|aborting batchmode|error :", line, re.I)]
            for line in important[-10:]:
                emit(line)
        raise RuntimeError(str(error) + f"\nUnity 完整日志：{log}") from error
    if not expected.is_file():
        raise RuntimeError(f"Unity 没有生成本次检查报告，不能将旧结果当作成功：{expected}")


def import_unity(args, workspace, project, stage, animations):
    if project is None:
        emit("已导出 Unity 导入包；本次未运行 Unity。", 96)
        return {"status": "not_requested", "staging": str(stage)}
    if unity_locked(project):
        raise RuntimeError("Unity 项目在执行过程中被打开，请关闭编辑器后重试。已提取的文件可继续复用。")
    target = project / ASSET_ROOT
    if target.exists():
        backup = workspace.root / "backups" / datetime.now().strftime("%Y%m%d-%H%M%S")
        for path in target.rglob("*"):
            if path.is_file() and path.suffix in (".unity", ".controller", ".mat", ".asset"):
                destination = backup / path.relative_to(target)
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(path, destination)
    target.mkdir(parents=True, exist_ok=True)
    for path in stage.rglob("*"):
        if path.is_dir():
            (target / path.relative_to(stage)).mkdir(parents=True, exist_ok=True)
        elif path.is_file():
            destination = target / path.relative_to(stage)
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(path, destination)
    results = {}
    for index, mode in enumerate(animations):
        report_dir = workspace.root / "reports/unity" / mode
        build_method = "BuildUiBatch" if mode == "ui" else "BuildBatch"
        run_unity(args, project, build_method, report_dir)
        mesh = read_json(report_dir / "verification.json")
        if not mesh.get("passed"):
            raise RuntimeError(f"{mode} 动画网格检查未通过。")
        runtime_status = "not_requested"
        if args.verify_play:
            method = "CheckUiRuntimeBatch" if mode == "ui" else "CheckRuntimeBatch"
            run_unity(args, project, method, report_dir)
            if not read_json(report_dir / "runtime_verification.json").get("passed"):
                raise RuntimeError(f"{mode} 动画实际播放检查未通过。")
            runtime_status = "passed"
        scene = target / ("Chen_UI_Walk.unity" if mode == "ui" else "Chen_Reextracted.unity")
        if not scene.is_file():
            raise RuntimeError("Unity 未生成预期的场景文件。")
        results[mode] = {"mesh": "passed", "play_mode": runtime_status, "scene": str(scene), "reports": str(report_dir)}
        emit(f"{mode} 场景已生成，网格检查通过；实际播放：{runtime_status}", 73 + int(23 * (index + 1) / len(animations)))
    return {"status": "completed", "project": str(project), "scenes": results}


def tool_signature(profile):
    h = hashlib.sha256(json.dumps(profile, sort_keys=True).encode())
    for folder in ("runtime", "scripts", "templates"):
        for path in sorted((ROOT / folder).rglob("*")):
            if path.is_file() and "__pycache__" not in path.parts:
                h.update(str(path.relative_to(ROOT)).encode())
                h.update(digest(path).encode())
    h.update(Path(__file__).read_bytes())
    return h.hexdigest()


def main():
    global log_file
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game", required=True)
    parser.add_argument("--workspace", required=True)
    parser.add_argument("--blender", default="")
    parser.add_argument("--unity", default="")
    parser.add_argument("--unity-project", default="")
    parser.add_argument("--animations", default="ui,acl")
    parser.add_argument("--weights", action="store_true")
    parser.add_argument("--verify-play", action="store_true")
    parser.add_argument("--force", action="store_true")
    parser.add_argument("--check-only", action="store_true")
    args = parser.parse_args()
    started = time.monotonic()
    workspace = None
    try:
        profile = read_json(ROOT / "profiles/chen.json")
        animations = list(dict.fromkeys(x.strip() for x in args.animations.split(",") if x.strip()))
        if not animations or any(x not in profile["animations"] for x in animations):
            raise ValueError("请至少选择一条支持的动画。")
        game, output, project = check_environment(args, profile)
        if args.check_only:
            emit("环境检查完成，可以开始提取。", 100, kind="completed", check_only=True)
            return 0
        workspace = Workspace(output, profile["game_assembly_sha256"])
        logs = output / "logs"
        logs.mkdir(exist_ok=True)
        log_file = (logs / (datetime.now().strftime("%Y%m%d-%H%M%S") + ".log")).open("w", encoding="utf-8")
        write_json(output / "last_run.json", {"status": "running", "started": datetime.now().isoformat(), "version": VERSION})
        signature = tool_signature(profile)
        bundles = extract_bundles(game, workspace, profile, animations)
        fbx, avatar, files = export_assets(workspace, profile, bundles, animations, signature + ",".join(animations), args.force)
        weights = audit_weights(args, workspace, fbx, signature)
        stage = prepare_unity(workspace, fbx, avatar, files)
        unity = import_unity(args, workspace, project, stage, animations)
        result = {"schema": "endfield-chen-tool-result-v1", "status": "completed", "version": VERSION,
                  "build": profile["build"], "workspace": str(output), "model": str(fbx),
                  "animations": {mode: str(path) for mode, path in files.items()}, "weights": weights,
                  "unity": unity, "elapsed_seconds": round(time.monotonic() - started, 2)}
        write_json(output / "last_run.json", result)
        scenes = unity.get("scenes", {})
        instructions = ["# 陈千语工具处理结果", "", f"游戏版本：`{profile['build']}`", "",
                        f"模型：`{fbx}`", "", f"权重检查：`{weights['status']}`", ""]
        for mode, entry in scenes.items():
            instructions += [f"## {profile['animations'][mode]['label']}", "", f"场景：`{entry['scene']}`", "",
                             f"网格检查：`{entry['mesh']}`；实际 Play 检查：`{entry['play_mode']}`。", "",
                             f"检查记录及截图：`{entry['reports']}`。", ""]
        if not scenes:
            instructions += ["本次完成资源导出，尚未运行 Unity。选择 Unity 项目后再次运行可生成可播放场景。", "",
                             f"待导入的文件位于：`{stage}`。其中 Source 动画需经随附的 Unity 脚本修复，不能当作最终动画直接使用。", ""]
        instructions += ["## 使用", "", "在工具中点击“打开 Unity 预览”，或在项目的 Assets/EndfieldChenTool 中打开对应场景并按 Play。", "",
                         "模型材质为基础贴图。专用着色器、IK、MC1 布料动力学及特效尚未完整复原。", "",
                         "本版支持陈千语的 female_walk 与 A_actor_chen_customized_state_walk_loop，其他动画尚未纳入验证。", ""]
        (output / "结果说明.md").write_text("\n".join(instructions), encoding="utf-8")
        emit("完成。已导出的资源和检查报告保存在输出目录。", 100, kind="completed", result=str(output / "last_run.json"))
        return 0
    except Exception as error:
        emit(str(error), kind="error")
        if workspace:
            write_json(workspace.root / "last_run.json", {"status": "failed", "error": str(error), "version": VERSION})
        if log_file:
            log_file.write(traceback.format_exc())
        return 1
    finally:
        if workspace:
            workspace.lock.close()
        if log_file:
            log_file.close()


if __name__ == "__main__":
    raise SystemExit(main())
