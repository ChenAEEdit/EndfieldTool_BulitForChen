#!/usr/bin/env python3
"""Read-only Endfield VFS/BLC evidence collector.

This deliberately stops before parsing the version-specific BLC index.  It
records decrypted prefixes and CHK magic values so a parser can be developed
against reproducible local evidence without touching the game installation.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import struct
from collections import Counter
from pathlib import Path, PurePosixPath
from typing import Callable, Iterable


COMMON_KEY = bytes.fromhex(
    "E9 5B 31 7A C4 F8 28 56 9D 23 A8 6B F2 71 DC B5 "
    "3E 84 6F A7 5C 92 4D 67 1D BA 8E 38 F4 CA 52 E1"
)


def _rotl32(value: int, amount: int) -> int:
    return ((value << amount) | (value >> (32 - amount))) & 0xFFFFFFFF


def _quarter_round(state: list[int], a: int, b: int, c: int, d: int) -> None:
    state[a] = (state[a] + state[b]) & 0xFFFFFFFF
    state[d] ^= state[a]
    state[d] = _rotl32(state[d], 16)
    state[c] = (state[c] + state[d]) & 0xFFFFFFFF
    state[b] ^= state[c]
    state[b] = _rotl32(state[b], 12)
    state[a] = (state[a] + state[b]) & 0xFFFFFFFF
    state[d] ^= state[a]
    state[d] = _rotl32(state[d], 8)
    state[c] = (state[c] + state[d]) & 0xFFFFFFFF
    state[b] ^= state[c]
    state[b] = _rotl32(state[b], 7)


def chacha20(data: bytes, nonce: bytes, counter: int = 1, key: bytes = COMMON_KEY) -> bytes:
    """ChaCha20 as used by the public Endfield VFS notes.

    The game uses the original 64-bit counter + 64-bit nonce layout.  The
    public article's PyCryptodome workaround starts at byte offset 64, which
    is equivalent to counter=1 here.
    """
    if len(key) != 32 or len(nonce) != 12:
        raise ValueError("ChaCha20 requires a 32-byte key and 12-byte nonce")
    constants = (0x61707865, 0x3320646E, 0x79622D32, 0x6B206574)
    key_words = struct.unpack("<8I", key)
    nonce_words = struct.unpack("<3I", nonce)
    output = bytearray()
    for block_index in range((len(data) + 63) // 64):
        state = [*constants, *key_words, counter + block_index, *nonce_words]
        working = state.copy()
        for _ in range(10):
            _quarter_round(working, 0, 4, 8, 12)
            _quarter_round(working, 1, 5, 9, 13)
            _quarter_round(working, 2, 6, 10, 14)
            _quarter_round(working, 3, 7, 11, 15)
            _quarter_round(working, 0, 5, 10, 15)
            _quarter_round(working, 1, 6, 11, 12)
            _quarter_round(working, 2, 7, 8, 13)
            _quarter_round(working, 3, 4, 9, 14)
        stream = struct.pack("<16I", *[(a + b) & 0xFFFFFFFF for a, b in zip(state, working)])
        output.extend(bytes(a ^ b for a, b in zip(data[block_index * 64 : block_index * 64 + 64], stream)))
    return bytes(output)


def ascii_runs(data: bytes, minimum: int = 4) -> list[str]:
    return [match.decode("ascii", "replace") for match in re.findall(rb"[ -~]{%d,}" % minimum, data)]


class ByteReader:
    def __init__(self, data: bytes) -> None:
        self.data = data
        self.offset = 0

    @property
    def remaining(self) -> int:
        return len(self.data) - self.offset

    def read(self, size: int) -> bytes:
        end = self.offset + size
        if size < 0 or end > len(self.data):
            raise ValueError(
                f"read beyond buffer at 0x{self.offset:x}: requested {size}, "
                f"remaining {self.remaining}"
            )
        value = self.data[self.offset : end]
        self.offset = end
        return value

    def unpack(self, fmt: str) -> tuple:
        size = struct.calcsize(fmt)
        return struct.unpack(fmt, self.read(size))

    def u8(self) -> int:
        return self.unpack("<B")[0]

    def u16(self) -> int:
        return self.unpack("<H")[0]

    def u32(self) -> int:
        return self.unpack("<I")[0]

    def i64(self) -> int:
        return self.unpack("<q")[0]

    def u64(self) -> int:
        return self.unpack("<Q")[0]

    def utf8(self) -> str:
        size = self.u16()
        return self.read(size).decode("utf-8")


def parse_blc_v4(
    plain: bytes,
    sample_limit: int = 5,
    file_visitor: Callable[[dict], None] | None = None,
) -> dict:
    """Parse the observed release-client v4 index layout.

    Several v4-only fields are intentionally named ``unknown_*`` until their
    runtime meaning is verified.  Their widths are tested by consuming every
    chunk/file record and reporting the exact footer left over.
    """
    reader = ByteReader(plain)
    version = reader.u32()
    if version != 4:
        raise ValueError(f"unsupported BLC version {version}; expected release v4")
    marker = reader.read(4)
    if marker != bytes.fromhex("29047701"):
        raise ValueError(f"unexpected v4 marker: {marker.hex(' ')}")
    group_name = reader.utf8()
    group_hash = reader.i64()
    group_file_count = reader.u32()
    group_chunks_length = reader.u64()
    block_type = reader.u8()
    chunk_count = reader.u32()
    files_seen = 0
    encrypted_files = 0
    chunk_unknown_u32 = Counter()
    unknown_suffix_u32 = Counter()
    samples = []
    chunks = []
    parsed_chunks_length = 0
    for chunk_index in range(chunk_count):
        chunk_start = reader.offset
        chunk_md5 = reader.read(16).hex().upper()
        content_md5 = reader.read(16).hex().upper()
        chunk_length = reader.u64()
        parsed_chunks_length += chunk_length
        chunk_block_type = reader.u8()
        chunk_extra_u32 = reader.u32()
        file_count = reader.u32()
        chunk_unknown_u32[chunk_extra_u32] += 1
        chunk_files = []
        for file_index in range(file_count):
            file_start = reader.offset
            file_name = reader.utf8()
            file_name_hash = reader.i64()
            file_chunk_md5 = reader.read(16).hex().upper()
            file_data_md5 = reader.read(16).hex().upper()
            offset = reader.u64()
            length = reader.u64()
            file_block_type = reader.u8()
            use_encrypt = reader.u8() != 0
            iv_seed = reader.u64() if use_encrypt else None
            suffix_u32 = reader.u32()
            if offset + length > chunk_length:
                raise ValueError(
                    f"file exceeds chunk {chunk_md5}: {file_name!r}, "
                    f"offset={offset}, length={length}, chunk_length={chunk_length}"
                )
            if file_chunk_md5 != chunk_md5:
                raise ValueError(
                    f"file/chunk MD5 mismatch at 0x{file_start:x}: "
                    f"{file_chunk_md5} != {chunk_md5}"
                )
            files_seen += 1
            encrypted_files += int(use_encrypt)
            unknown_suffix_u32[suffix_u32] += 1
            record = {
                "name": file_name,
                "name_hash": file_name_hash,
                "chunk_md5": file_chunk_md5,
                "data_md5": file_data_md5,
                "offset": offset,
                "length": length,
                "block_type": file_block_type,
                "use_encrypt": use_encrypt,
                "iv_seed": iv_seed,
                "unknown_suffix_u32": suffix_u32,
            }
            if file_visitor is not None:
                file_visitor(record)
            if len(samples) < sample_limit:
                samples.append(record)
            if file_index < sample_limit:
                chunk_files.append(record)
        chunks.append(
            {
                "index": chunk_index,
                "offset": chunk_start,
                "md5": chunk_md5,
                "content_md5": content_md5,
                "length": chunk_length,
                "block_type": chunk_block_type,
                "unknown_u32": chunk_extra_u32,
                "file_count": file_count,
                "file_samples": chunk_files,
            }
        )
    if files_seen != group_file_count:
        raise ValueError(
            f"group file count mismatch: declared {group_file_count}, parsed {files_seen}"
        )
    if parsed_chunks_length != group_chunks_length:
        raise ValueError(
            f"group chunk length mismatch: declared {group_chunks_length}, "
            f"parsed {parsed_chunks_length}"
        )
    footer_offset = reader.offset
    footer = reader.read(reader.remaining)
    if len(footer) != 14:
        raise ValueError(f"unexpected v4 footer length: {len(footer)}")
    return {
        "version": version,
        "marker": marker.hex(" "),
        "group_name": group_name,
        "group_hash": group_hash,
        "group_file_count": group_file_count,
        "group_chunks_length": group_chunks_length,
        "block_type": block_type,
        "chunk_count": chunk_count,
        "files_seen": files_seen,
        "encrypted_files": encrypted_files,
        "chunk_unknown_u32_counts": dict(chunk_unknown_u32),
        "unknown_suffix_u32_counts": dict(unknown_suffix_u32),
        "file_samples": samples,
        "chunks": chunks,
        "footer_offset": footer_offset,
        "footer_hex": footer.hex(" "),
    }


def safe_output_path(output_root: Path, virtual_name: str) -> Path:
    normalized = virtual_name.replace("\\", "/")
    virtual = PurePosixPath(normalized)
    if virtual.is_absolute() or any(part in ("", ".", "..") or ":" in part for part in virtual.parts):
        raise ValueError(f"unsafe virtual path: {virtual_name!r}")
    root = output_root.resolve()
    destination = root.joinpath(*virtual.parts).resolve()
    if not destination.is_relative_to(root):
        raise ValueError(f"virtual path escapes output root: {virtual_name!r}")
    return destination


def extract_group(
    vfs: Path,
    group_name: str,
    output_root: Path,
    contains: str | None,
    max_files: int,
    overwrite: bool,
) -> dict:
    group = vfs / group_name
    blc_path = group / f"{group_name}.blc"
    if not blc_path.is_file():
        raise ValueError(f"BLC does not exist: {blc_path}")
    if max_files <= 0:
        raise ValueError("--max-files must be positive")
    blc_data = blc_path.read_bytes()
    if len(blc_data) < 12:
        raise ValueError(f"short BLC header: {blc_path}")
    vfs_proto_version = struct.unpack_from("<I", blc_data, 0)[0]
    selected = []
    needle = contains.casefold() if contains else None

    def visitor(record: dict) -> None:
        if len(selected) >= max_files:
            return
        if needle and needle not in record["name"].casefold():
            return
        chk_path = group / f'{record["chunk_md5"]}.chk'
        with chk_path.open("rb") as handle:
            handle.seek(record["offset"])
            stored = handle.read(record["length"])
        if len(stored) != record["length"]:
            raise ValueError(
                f"short CHK read for {record['name']!r}: "
                f"expected {record['length']}, got {len(stored)}"
            )
        decoded = stored
        if record["use_encrypt"]:
            nonce = struct.pack("<IQ", vfs_proto_version, record["iv_seed"])
            decoded = chacha20(stored, nonce)
        stored_md5 = hashlib.md5(stored).hexdigest().upper()
        decoded_md5 = hashlib.md5(decoded).hexdigest().upper()
        if decoded_md5 != record["data_md5"]:
            raise ValueError(
                f"decoded MD5 mismatch for {record['name']!r}: "
                f"expected {record['data_md5']}, got {decoded_md5}"
            )
        destination = safe_output_path(output_root, record["name"])
        if destination.exists() and not overwrite:
            raise FileExistsError(f"output exists; pass --overwrite: {destination}")
        destination.parent.mkdir(parents=True, exist_ok=True)
        temporary = destination.with_name(destination.name + ".part")
        temporary.write_bytes(decoded)
        temporary.replace(destination)
        selected.append(
            {
                **record,
                "source": str(chk_path),
                "output": str(destination),
                "stored_md5": stored_md5,
                "decoded_md5": decoded_md5,
                "expected_md5_matches_stored": stored_md5 == record["data_md5"],
                "expected_md5_matches_decoded": decoded_md5 == record["data_md5"],
                "decoded_prefix_hex": decoded[:32].hex(" "),
            }
        )

    index = parse_blc_v4(chacha20(blc_data[12:], blc_data[:12]), file_visitor=visitor)
    return {
        "group": group_name,
        "vfs_proto_version": vfs_proto_version,
        "filter_contains": contains,
        "max_files": max_files,
        "selected_count": len(selected),
        "files": selected,
        "index_files_seen": index["files_seen"],
    }


def inspect_blc(path: Path, prefix_bytes: int) -> dict:
    with path.open("rb") as handle:
        nonce = handle.read(12)
        encrypted = handle.read(prefix_bytes)
    if len(nonce) != 12:
        raise ValueError(f"short BLC nonce: {path}")
    plain = chacha20(encrypted, nonce)
    return {
        "path": str(path),
        "size": path.stat().st_size,
        "nonce": nonce.hex(" "),
        "decrypted_prefix_hex": plain[:64].hex(" "),
        "version_u32": struct.unpack_from("<I", plain, 0)[0] if len(plain) >= 4 else None,
        "marker_hex": plain[4:12].hex(" "),
        "ascii_runs": ascii_runs(plain),
    }


def inspect_chk(path: Path, prefix_bytes: int) -> dict:
    with path.open("rb") as handle:
        prefix = handle.read(prefix_bytes)
    return {
        "path": str(path),
        "size": path.stat().st_size,
        "prefix_hex": prefix[:32].hex(" "),
        "magic4": prefix[:4].decode("ascii", "replace"),
    }


def iter_groups(vfs: Path, selected: str | None) -> Iterable[Path]:
    groups = sorted(path for path in vfs.iterdir() if path.is_dir())
    if selected:
        groups = [path for path in groups if path.name.upper() == selected.upper()]
    return groups


def build_inventory(
    vfs: Path, prefix_bytes: int, selected: str | None, parse_index: bool
) -> dict:
    groups = []
    for group in iter_groups(vfs, selected):
        blc = sorted(group.glob("*.blc"))
        chk = sorted(group.glob("*.chk"))
        magic_counts = Counter()
        chk_records = []
        for path in chk:
            record = inspect_chk(path, 16)
            magic_counts[record["magic4"]] += 1
            chk_records.append(record)
        blc_records = []
        for path in blc:
            record = inspect_blc(path, prefix_bytes)
            if parse_index:
                data = path.read_bytes()
                record["index"] = parse_blc_v4(chacha20(data[12:], data[:12]))
            blc_records.append(record)
        groups.append(
            {
                "group": group.name,
                "blc": blc_records,
                "chk_count": len(chk),
                "chk_magic_counts": dict(sorted(magic_counts.items())),
                "chk": chk_records,
            }
        )
    return {"vfs": str(vfs), "groups": groups}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("vfs", type=Path, help="Endfield_Data/StreamingAssets/VFS")
    parser.add_argument("--group", help="only inspect one group directory")
    parser.add_argument("--prefix-bytes", type=int, default=4096)
    parser.add_argument("--parse-index", action="store_true", help="parse the observed v4 BLC index")
    parser.add_argument("--json-out", type=Path, help="write inventory JSON outside the game directory")
    parser.add_argument("--extract-out", type=Path, help="extract selected virtual files to this directory")
    parser.add_argument("--path-contains", help="case-insensitive virtual-path substring filter")
    parser.add_argument("--max-files", type=int, default=1, help="hard extraction limit (default: 1)")
    parser.add_argument("--overwrite", action="store_true")
    args = parser.parse_args()
    vfs = args.vfs.resolve()
    if not vfs.is_dir():
        parser.error(f"VFS directory does not exist: {vfs}")
    inventory = build_inventory(vfs, args.prefix_bytes, args.group, args.parse_index)
    if args.extract_out:
        if not args.group:
            parser.error("--extract-out requires --group")
        inventory["extraction"] = extract_group(
            vfs,
            args.group.upper(),
            args.extract_out,
            args.path_contains,
            args.max_files,
            args.overwrite,
        )
    encoded = json.dumps(inventory, ensure_ascii=False, indent=2)
    if args.json_out:
        args.json_out.parent.mkdir(parents=True, exist_ok=True)
        args.json_out.write_text(encoded + "\n", encoding="utf-8")
    else:
        print(encoded)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

