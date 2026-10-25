"""Strict format adapters for discovery, independent of character export profiles.

Executable hashes identify reference builds and cache namespaces. They are not
permissions to parse data: the active adapter must recognize and validate it.
"""
from __future__ import annotations

import json
from pathlib import Path
import struct

from endfield_vfs import parse_blc_v4
from endfield_manifest import EndfieldManifest


class UnsupportedIndexFormat(ValueError):
    pass


class BlcV4Adapter:
    format_id = "blc-v4-29047701"

    @staticmethod
    def accepts(plain):
        return len(plain) >= 8 and struct.unpack_from("<I", plain)[0] == 4 and plain[4:8] == bytes.fromhex("29047701")

    @staticmethod
    def parse(plain, visitor):
        return parse_blc_v4(plain, sample_limit=0, file_visitor=visitor)


class ManifestRefValueAdapter:
    format_id = "manifest-refvalue-bundle48-v1"

    @staticmethod
    def accepts(data):
        return len(data) >= 4 and struct.unpack_from("<I", data)[0] == 0xFF11FF11

    @staticmethod
    def parse(data):
        manifest = EndfieldManifest(data)
        # Force validation of every bundle record, shared reference and dependency.
        _ = manifest.bundles
        return manifest


BLC_ADAPTERS = (BlcV4Adapter(),)
MANIFEST_ADAPTERS = (ManifestRefValueAdapter(),)
ADAPTER_REVISION = "strict-index-adapters-v1"


def parse_blc(plain, visitor):
    for adapter in BLC_ADAPTERS:
        if adapter.accepts(plain):
            return adapter.parse(plain, visitor), adapter.format_id
    version = struct.unpack_from("<I", plain)[0] if len(plain) >= 4 else "short"
    raise UnsupportedIndexFormat(f"尚无适用的 BLC 格式适配器：版本={version}，头部={plain[:8].hex()}。")


def parse_manifest(data):
    for adapter in MANIFEST_ADAPTERS:
        if adapter.accepts(data):
            return adapter.parse(data), adapter.format_id
    raise UnsupportedIndexFormat("尚无适用的资源清单格式适配器。")


def compatibility(fingerprint, manifest_md5, manifest_version, references_path: Path):
    references = []
    if references_path.is_file():
        document = json.loads(references_path.read_text(encoding="utf-8-sig"))
        if document.get("schema") != "endfield-known-builds-v1":
            raise ValueError("参考版本列表的格式不支持。")
        references = document.get("builds", [])
    matching = [entry for entry in references if
        entry["gameAssemblySha256"].upper() == fingerprint and entry["manifestMd5"].upper() == manifest_md5]
    if len(matching) > 1:
        raise ValueError("参考版本列表存在重复的程序／清单组合。")
    if matching:
        return {"status": "KnownReference", "gameBuild": matching[0]["id"],
                "note": "程序与清单摘要匹配历史参考版本；本次索引格式可读取，不代表新任务的模型或动画已验证。"}
    return {"status": "FormatCompatibleUnverified", "gameBuild": "manifest:" + manifest_version,
            "note": "索引格式可读取，但此程序／清单组合尚未验证；不能据此认定模型、动画或旧角色提取配置兼容。"}
