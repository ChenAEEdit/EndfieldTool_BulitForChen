"""Read-only game discovery: VFS records, manifest dependencies and plaintext name candidates.

No model/clip decoding is performed. Cache commits are per CHK, so killing the process
discards only its current chunk. The only extracted payload is the resource manifest.
"""
from __future__ import annotations

import argparse
import bisect
from collections import defaultdict
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import sqlite3
import struct
import subprocess
import sys
import uuid

from index_formats import ADAPTER_REVISION, UnsupportedIndexFormat, compatibility, parse_blc, parse_manifest

ROOT = Path(__file__).resolve().parent.parent
SCANNER_VERSION = "plaintext-candidates-v2"
SIGNATURE = re.compile(
    rb"(?<![A-Za-z0-9_])(?:chr_[0-9]{3,6}_[a-z0-9]+_(?:uimodel|model)(?![a-z0-9_])"
    rb"|a_actor_[a-z0-9]+_[a-z0-9_]{1,180})(?![a-z0-9_])", re.IGNORECASE)
BLOCK = 4 * 1024 * 1024
OVERLAP = 512
FAILURE_CONTEXT = {}


def emit(message, progress=None, kind="log", **extra):
    row = {"type": kind, "message": message, **extra}
    if progress is not None:
        row["progress"] = progress
    print("@event " + json.dumps(row, ensure_ascii=False), flush=True)


def digest(path):
    result = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(BLOCK), b""):
            result.update(block)
    return result.hexdigest().upper()


def write_json(path, document):
    temporary = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    try:
        temporary.write_text(json.dumps(document, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)


def run_crypto(*args):
    result = subprocess.run([str(ROOT / "runtime/crypto/VfsCrypto.exe"), *map(str, args)],
                            capture_output=True, text=True, encoding="utf-8", errors="replace",
                            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
    if result.returncode:
        raise ValueError("资源索引解码失败：" + result.stderr[-1500:])


def normalize_game(path):
    for candidate in (path, path / "Endfield_Data", path / "games/Arknights Endfield/Endfield_Data"):
        if (candidate / "StreamingAssets/VFS").is_dir():
            return candidate.resolve()
    raise ValueError("找不到 Endfield_Data/StreamingAssets/VFS。")


def within(path, parent):
    return path == parent or parent in path.parents


def read_indexes(vfs, cache):
    groups = sorted(path for path in vfs.iterdir() if path.is_dir() and not path.is_symlink())
    groups = [(path, path / (path.name + ".blc")) for path in groups]
    groups = [(path, blc) for path, blc in groups if blc.is_file()]
    if not groups:
        raise ValueError("没有发现可读取的 VFS BLC 索引。")
    records = []
    signatures = {}
    formats = {}
    for position, (group, blc) in enumerate(groups):
        emit(f"读取 VFS 分组 {position + 1}/{len(groups)}：{group.name}", 3 + int(9 * position / len(groups)))
        before = blc.stat()
        signature = digest(blc)
        decoded = cache / (group.name + ".blc.bin")
        run_crypto(blc, decoded)
        version = struct.unpack("<I", blc.read_bytes()[:4])[0]
        group_records = []
        _, format_id = parse_blc(decoded.read_bytes(), group_records.append)
        after = blc.stat()
        if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
            raise ValueError("游戏索引在读取期间发生变化，请等游戏更新完成后重新扫描。")
        signatures[group.name] = signature
        formats[group.name] = format_id
        records.extend(dict(row, group=group.name, vfs_version=version) for row in group_records)
    unique = {}
    for row in records:
        key = (row["group"], row["name"])
        if key in unique and unique[key] != row:
            raise ValueError("同一 VFS 分组出现不一致的重复路径，无法可靠索引：" + row["name"])
        unique[key] = row
    return list(unique.values()), signatures, formats


def read_manifest(records, vfs, cache):
    targets = [row for row in records if row["name"].replace("\\", "/").lower().endswith("/manifest.hgmmap")]
    if len(targets) != 1:
        raise ValueError(f"资源清单不唯一或缺失（{len(targets)} 条），无法建立可靠的依赖索引。")
    row = targets[0]
    chunk = vfs / row["group"] / (row["chunk_md5"] + ".chk")
    with chunk.open("rb") as stream:
        stream.seek(row["offset"])
        payload = stream.read(row["length"])
    if len(payload) != row["length"]:
        raise ValueError("资源清单数据不完整。")
    if row["use_encrypt"]:
        emit("解码资源清单（只处理清单，不导出模型或动画）…", 13)
        # Use the same native helper to avoid a Python byte-by-byte 50 MB decryption.
        encrypted = cache / "manifest.encrypted"
        encrypted.write_bytes(struct.pack("<IQ", row["vfs_version"], row["iv_seed"]) + payload)
        decoded = cache / "manifest.hgmmap"
        run_crypto(encrypted, decoded)
        payload = decoded.read_bytes()
    if hashlib.md5(payload).hexdigest().upper() != row["data_md5"]:
        raise ValueError("资源清单 MD5 不符。")
    compressed = cache / "manifest.hgmmap"
    compressed.write_bytes(payload)
    binary = cache / "manifest.bin"
    run_crypto("--brotli", compressed, binary)
    manifest, format_id = parse_manifest(binary.read_bytes())
    return manifest, row["data_md5"], format_id


def file_signature(path, blc_signature):
    stat = path.stat()
    return f"{SCANNER_VERSION}:{blc_signature}:{stat.st_size}:{stat.st_mtime_ns}"


def scan_chunk(path, records, on_progress):
    ordered = sorted(records, key=lambda row: row["offset"])
    starts = [row["offset"] for row in ordered]
    matches = {}
    size = path.stat().st_size
    before = path.stat()
    checksum = hashlib.sha256()
    consumed, previous = 0, b""
    with path.open("rb") as stream:
        while block := stream.read(BLOCK):
            checksum.update(block)
            data = previous + block
            base = consumed - len(previous)
            consumed += len(block)
            cutoff = len(data) if consumed == size else max(0, len(data) - OVERLAP)
            for match in SIGNATURE.finditer(data):
                # Delay matches near a block end until the next read, avoiding truncated names.
                if match.start() >= cutoff:
                    continue
                absolute = base + match.start()
                index = bisect.bisect_right(starts, absolute) - 1
                if index < 0:
                    continue
                row = ordered[index]
                if row["use_encrypt"] or absolute + len(match[0]) > row["offset"] + row["length"]:
                    continue
                if not row["name"].lower().endswith(".ab"):
                    continue
                name = match[0].decode("ascii")
                model = name.lower().startswith("chr_")
                code = name.split("_")[2].lower()
                key = (row["group"], row["name"], name.lower())
                matches.setdefault(key, {"character": code, "name": name, "kind": "Model" if model else "Animation",
                    "group": row["group"], "bundle": row["name"], "chunk": path.name,
                    "offset": absolute - row["offset"]})
            previous = data[max(0, cutoff - 1):]  # Preserve the preceding character for the left boundary check.
            on_progress(consumed)
    after = path.stat()
    if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
        raise ValueError("游戏 CHK 在扫描期间发生变化，请更新结束后重试。")
    return list(matches.values()), checksum.hexdigest().upper()


def make_catalog(hits, records, manifest, fingerprint, build, manifest_md5, coverage):
    available = defaultdict(list)
    for row in records:
        path = row["name"].replace("\\", "/")
        if path.lower().endswith(".ab"):
            # The manifest names include initial/main; never join on basename alone.
            marker = "/windows/"
            key = path.lower().split(marker, 1)[-1]
            available[key].append(row)
    by_name = defaultdict(list)
    for bundle in manifest.bundles:
        by_name[bundle.name.lower()].append(bundle)
    selected_rows, bundle_refs = {}, {}
    missing_names = set()

    def identity(row):
        return row["group"] + ":" + row["name"]

    def include(row):
        key = identity(row)
        if key in selected_rows:
            return key
        selected_rows[key] = row
        bundle_refs[key] = {"id": key, "group": row["group"], "md5": row["data_md5"],
                            "virtualPath": row["name"], "dependencyIds": [], "missingDependencyNames": []}
        return key

    # Process iteratively because the manifest graph may contain cycles.
    for hit in hits:
        key = hit["bundle"].replace("\\", "/").lower().split("/windows/", 1)[-1]
        for row in available.get(key, []):
            if row["group"] == hit["group"]:
                include(row)
    pending = list(selected_rows)
    processed = set()
    while pending:
        key = pending.pop()
        if key in processed:
            continue
        processed.add(key)
        row = selected_rows[key]
        virtual = row["name"].replace("\\", "/").lower().split("/windows/", 1)[-1]
        matches = by_name.get(virtual, [])
        bundle = matches[0] if len(matches) == 1 else None
        dependencies = [] if bundle is None else bundle.direct_dependencies
        if bundle is None:
            bundle_refs[key]["missingDependencyNames"].append("manifest entry: " + virtual)
            missing_names.add(virtual)
        for index in dependencies:
            name = manifest.bundles[index].name.lower()
            found = available.get(name, [])
            if len(found) != 1:
                bundle_refs[key]["missingDependencyNames"].append(name)
                missing_names.add(name)
                continue
            dependency_id = include(found[0])
            bundle_refs[key]["dependencyIds"].append(dependency_id)
            if dependency_id not in processed:
                pending.append(dependency_id)

    characters = defaultdict(list)
    unique = set()
    for hit in hits:
        bundle_id = hit["group"] + ":" + hit["bundle"]
        resource_key = (hit["character"], hit["name"].lower(), bundle_id)
        if resource_key in unique:
            continue
        unique.add(resource_key)
        resource_id = hashlib.sha256("|".join(resource_key).encode()).hexdigest()[:24]
        characters[hit["character"]].append({"id": "candidate:" + resource_id, "name": hit["name"],
            "kind": hit["kind"], "sourcePath": hit["bundle"], "rig": "Unknown", "backendKey": "",
            "association": "NameCandidate", "bundleIds": [bundle_id],
            "evidence": f"未加密 Bundle 内明文名称；{hit['group']}/{hit['chunk']}；Bundle 内偏移 {hit['offset']}。未解析对象类型或骨架。"})
    catalog = {"schema": "endfield-character-catalog-v1", "gameBuild": build,
        "gameAssemblySha256": fingerprint, "createdAt": datetime.now(timezone.utc).isoformat(),
        "sourceDescription": f"本机 VFS 明文名称候选；manifest={manifest_md5}", "coverageNote": coverage,
        "bundles": sorted(bundle_refs.values(), key=lambda item: item["id"]),
        "characters": [{"id": code, "displayName": code + " · 名称候选", "rootName": "", "rootPathId": "",
            "resources": sorted(rows, key=lambda item: (item["kind"], item["name"]))}
            for code, rows in sorted(characters.items())]}
    return catalog, len(missing_names)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game", required=True, type=Path)
    parser.add_argument("--workspace", required=True, type=Path)
    parser.add_argument("--force", action="store_true")
    parser.add_argument("--probe-only", action="store_true", help="read and validate index formats without scanning CHK names")
    args = parser.parse_args()
    game = normalize_game(args.game.resolve())
    workspace = args.workspace.resolve()
    if within(workspace, game.parent) or within(game, workspace) or within(workspace, ROOT) or within(ROOT, workspace):
        raise ValueError("索引工作区必须独立于游戏和工具目录。")
    marker = workspace / "character-workspace.json"
    if not marker.is_file() or json.loads(marker.read_text(encoding="utf-8-sig")).get("schema") != "endfield-character-workspace-v1":
        raise ValueError("请先通过角色工作台建立兼容工作区。")
    emit("识别程序版本及索引格式…", 1)
    fingerprint = digest(game.parent / "GameAssembly.dll")
    namespace = workspace / "index" / fingerprint / SCANNER_VERSION / ADAPTER_REVISION
    namespace.mkdir(parents=True, exist_ok=True)
    check_path = namespace / ("compatibility-" + uuid.uuid4().hex + ".json")
    FAILURE_CONTEXT.update(report_path=check_path, fingerprint=fingerprint, phase="VFS formats")
    # Serialize all versions of the scanner using the same workspace, including probes.
    lock = (workspace / "index/scan.lock").open("a+b")
    if os.name == "nt":
        import msvcrt
        if lock.seek(0, 2) == 0:
            lock.write(b"0"); lock.flush()
        lock.seek(0)
        try:
            msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError as error:
            raise ValueError("这个工作区已有索引任务在运行。") from error
    vfs = game / "StreamingAssets/VFS"
    metadata = namespace / "metadata"
    metadata.mkdir(exist_ok=True)
    records, signatures, blc_formats = read_indexes(vfs, metadata)
    FAILURE_CONTEXT["phase"] = "manifest format and references"
    manifest, manifest_md5, manifest_format = read_manifest(records, vfs, metadata)
    compatible = compatibility(fingerprint, manifest_md5, manifest.version, ROOT / "profiles/known_builds.json")
    generation = hashlib.sha256(json.dumps({"blc": signatures, "manifest": manifest_md5,
        "blcFormats": blc_formats, "manifestFormat": manifest_format}, sort_keys=True).encode()).hexdigest()
    cache = namespace / "generations" / generation
    cache.mkdir(parents=True, exist_ok=True)
    probe_report = {"schema": "endfield-index-compatibility-v1", **compatible,
        "gameAssemblySha256": fingerprint, "manifestMd5": manifest_md5, "manifestVersion": manifest.version,
        "blcFormats": blc_formats, "manifestFormat": manifest_format, "adapterRevision": ADAPTER_REVISION,
        "vfsGroups": len(signatures), "vfsRecords": len(records), "manifestBundles": manifest.bundle_count,
        "cacheGeneration": generation, "decoded": False, "adapted": False, "verified": False,
        "completedAt": datetime.now(timezone.utc).isoformat()}
    # A successful probe must also represent a stable snapshot of current installation metadata.
    assert_unchanged(vfs, signatures, game, fingerprint)
    write_json(check_path, probe_report)
    FAILURE_CONTEXT.update(phase="name discovery", report_path=namespace / ("failure-" + uuid.uuid4().hex + ".json"))
    emit(compatible["note"], 16)
    if args.probe_only:
        emit("索引格式检查完成：" + compatible["note"], 100, "completed", catalog=None,
             report=str(check_path), compatibilityStatus=compatible["status"])
        lock.close()
        return
    emit("资源清单已解析，准备扫描 Bundle 内的可见名称…", 16)
    grouped = defaultdict(list)
    for row in records:
        if row["name"].lower().endswith(".ab") and not row["use_encrypt"]:
            grouped[(row["group"], row["chunk_md5"])].append(row)
    paths = [(group, chunk, vfs / group / (chunk + ".chk")) for group, chunk in sorted(grouped)]
    missing = [str(path) for _, _, path in paths if not path.is_file()]
    paths = [(group, chunk, path) for group, chunk, path in paths if path.is_file()]
    total = sum(path.stat().st_size for _, _, path in paths)
    completed, reused, hits = 0, 0, []
    db = sqlite3.connect(cache / "chunks.sqlite")
    db.execute("PRAGMA journal_mode=WAL")
    db.execute("CREATE TABLE IF NOT EXISTS chunks (id TEXT PRIMARY KEY, signature TEXT NOT NULL, sha256 TEXT NOT NULL, hits TEXT NOT NULL)")
    for position, (group, chunk, path) in enumerate(paths):
        key = group + ":" + chunk
        signature = file_signature(path, signatures[group])
        cached = db.execute("SELECT hits FROM chunks WHERE id=? AND signature=?", (key, signature)).fetchone()
        emit(f"扫描名称 {position + 1}/{len(paths)}：{group}/{chunk}", 17 + int(78 * completed / max(1, total)))
        if cached is not None and not args.force:
            found = json.loads(cached[0]); reused += 1
        else:
            last_report = [-1]
            def progress(amount):
                percent = 17 + int(78 * (completed + amount) / max(1, total))
                if percent != last_report[0]:
                    emit(f"扫描名称 {position + 1}/{len(paths)}，已读取 {(completed + amount) // (1024 ** 2)} MiB", percent)
                    last_report[0] = percent
            found, sha256 = scan_chunk(path, grouped[(group, chunk)], progress)
            with db:
                db.execute("INSERT OR REPLACE INTO chunks VALUES (?,?,?,?)", (key, signature, sha256, json.dumps(found)))
        hits.extend(found)
        completed += path.stat().st_size
    db.close()
    emit("合并角色名称候选与清单依赖…", 96)
    encrypted = sum(1 for row in records if row["use_encrypt"] and row["name"].lower().endswith(".ab"))
    coverage = ("仅发现未加密 Bundle 中可见的 ASCII 模型／动作名称；压缩、加密、其他命名形式可能漏项。"
                f"本次跳过 {encrypted} 条加密 Bundle 记录，{len(missing)} 个所需 CHK 不在本机。"
                "名称关联未确认，不代表已解码或可播放。")
    catalog, missing_deps = make_catalog(hits, records, manifest, fingerprint, compatible["gameBuild"], manifest_md5, coverage)
    catalog.update(compatibilityStatus=compatible["status"], compatibilityNote=compatible["note"])
    # Catch concurrent game updates before publishing a new index.
    assert_unchanged(vfs, signatures, game, fingerprint)
    catalog_path, report_path = cache / "catalog.json", cache / "report.json"
    report = {"schema": "endfield-resource-discovery-report-v1", "status": "discovered",
        "scannerVersion": SCANNER_VERSION, "gameBuild": compatible["gameBuild"], "gameAssemblySha256": fingerprint,
        "compatibility": compatible, "compatibilityReport": str(check_path), "cacheGeneration": generation,
        "blcFormats": blc_formats, "manifestFormat": manifest_format, "adapterRevision": ADAPTER_REVISION,
        "manifestMd5": manifest_md5, "manifestVersion": manifest.version, "vfsGroups": len(signatures),
        "vfsRecords": len(records), "scannedChunks": len(paths), "reusedChunks": reused,
        "scannedBytes": completed, "encryptedBundleRecordsSkipped": encrypted,
        "missingChunks": missing, "missingOrAmbiguousDependencyNames": missing_deps,
        "candidateCharacters": len(catalog["characters"]), "candidateResources": sum(len(c["resources"]) for c in catalog["characters"]),
        "coverageNote": coverage, "decoded": False, "adapted": False, "verified": False,
        "cacheReuse": "GameAssembly SHA256 + scanner version + BLC SHA256 + CHK size/mtime; force bypasses CHK reuse.",
        "completedAt": datetime.now(timezone.utc).isoformat()}
    write_json(report_path, report)
    write_json(catalog_path, catalog)
    emit(f"索引完成：{report['candidateCharacters']} 个角色名称候选，{report['candidateResources']} 项资源。",
         100, "completed", catalog=str(catalog_path), report=str(report_path), compatibilityStatus=compatible["status"])
    lock.close()


def assert_unchanged(vfs, signatures, game, fingerprint):
    current_groups = {path.name for path in vfs.iterdir() if path.is_dir()
                      and not path.is_symlink()
                      and (path / (path.name + ".blc")).is_file()}
    if current_groups != set(signatures) or any(
        digest(vfs / group / (group + ".blc")) != sha for group, sha in signatures.items()
    ):
        raise ValueError("读取期间游戏索引有变化，未发布本次结果。")
    if digest(game.parent / "GameAssembly.dll") != fingerprint:
        raise ValueError("读取期间游戏程序有变化，未发布本次结果。")


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    try:
        main()
    except Exception as error:
        status = "Unsupported" if isinstance(error, UnsupportedIndexFormat) else "Unreadable"
        report_path = FAILURE_CONTEXT.get("report_path")
        if report_path:
            try:
                write_json(report_path, {"schema": "endfield-index-compatibility-v1", "status": status,
                    "phase": FAILURE_CONTEXT.get("phase"), "message": str(error),
                    "gameAssemblySha256": FAILURE_CONTEXT.get("fingerprint"),
                    "verified": False, "completedAt": datetime.now(timezone.utc).isoformat()})
            except OSError:
                report_path = None
        emit(str(error), kind="error", report=str(report_path) if report_path else None, compatibilityStatus=status)
        raise SystemExit(1)
