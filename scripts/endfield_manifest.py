#!/usr/bin/env python3
"""Parse Endfield's decompressed ``manifest.hgmmap`` bundle table.

The game stores three length-prefixed index segments followed by a fixed-size
Bundle array. Bundle strings and integer arrays are references into the shared
data area after that array. This module deliberately parses only the fields
whose layout has been verified against ``ManifestDataBinary.InitBinary``.
"""

from __future__ import annotations

import struct
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable


MAGIC = 0xFF11FF11
SECOND_MAGIC = 0xF1F2F3F4
BUNDLE_SIZE = 0x30


class ManifestFormatError(ValueError):
    """Raised when a manifest violates the verified binary layout."""


@dataclass(frozen=True)
class Bundle:
    index: int
    name: str
    dependencies: tuple[int, ...]
    direct_reverse_dependencies: tuple[int, ...]
    direct_dependencies: tuple[int, ...]
    flags: int
    hash_name: int
    hash_version: int
    root_category: int


class EndfieldManifest:
    def __init__(self, data: bytes) -> None:
        self._data = data
        cursor = 0

        magic, cursor = self._read_u32(cursor)
        if magic != MAGIC:
            raise ManifestFormatError(
                f"bad manifest magic 0x{magic:08X}; input must be Brotli-decompressed"
            )
        self.version, cursor = self._read_header_string(cursor)
        second_magic, cursor = self._read_u32(cursor)
        if second_magic != SECOND_MAGIC:
            raise ManifestFormatError(f"bad second magic 0x{second_magic:08X}")
        self.hash, cursor = self._read_header_string(cursor)
        self.perforce_cl, cursor = self._read_header_string(cursor)

        self.asset_info_offset, self.asset_info_length, cursor = self._read_segment(cursor)
        self.bundle_hash_offset, self.bundle_hash_length, cursor = self._read_segment(cursor)
        self.bundle_array_offset, self.bundle_array_length, cursor = self._read_segment(cursor)

        if self.bundle_array_length < 4:
            raise ManifestFormatError("Bundle array segment is shorter than its count")
        self.bundle_count = self._unpack_from("<i", self.bundle_array_offset)[0]
        expected_length = 4 + self.bundle_count * BUNDLE_SIZE
        if self.bundle_count < 0 or self.bundle_array_length != expected_length:
            raise ManifestFormatError(
                "Bundle array length mismatch: "
                f"count={self.bundle_count} length={self.bundle_array_length} "
                f"expected={expected_length}"
            )

        # InitBinary advances one additional int32 after the Bundle segment.
        # It is the byte length of the shared RefValue data that follows.
        self.shared_data_length_offset = cursor
        self._require(cursor, 4, "shared data length")
        self.shared_data_length = self._unpack_from("<I", cursor)[0]
        self.data_offset = cursor + 4
        actual_length = len(self._data) - self.data_offset - 4
        if actual_length < 0 or self.shared_data_length != actual_length:
            raise ManifestFormatError(
                "shared data length mismatch: "
                f"declared={self.shared_data_length} "
                f"actual={actual_length}"
            )
        self.shared_data_trailer = self._unpack_from("<I", len(self._data) - 4)[0]
        if self.shared_data_trailer != self.shared_data_length:
            raise ManifestFormatError(
                "shared data trailer mismatch: "
                f"header={self.shared_data_length} trailer={self.shared_data_trailer}"
            )
        self._bundles: tuple[Bundle, ...] | None = None

    @classmethod
    def from_path(cls, path: Path) -> "EndfieldManifest":
        return cls(path.read_bytes())

    @property
    def bundles(self) -> tuple[Bundle, ...]:
        if self._bundles is None:
            records = []
            record_base = self.bundle_array_offset + 4
            for position in range(self.bundle_count):
                offset = record_base + position * BUNDLE_SIZE
                (
                    stored_index,
                    name_ref,
                    dependencies_ref,
                    reverse_ref,
                    direct_ref,
                    flags,
                    hash_name,
                    hash_version,
                    root_category,
                    _padding,
                ) = self._unpack_from("<iiiiiiqqii", offset)
                if stored_index != position:
                    raise ManifestFormatError(
                        f"Bundle index mismatch at record {position}: {stored_index}"
                    )
                records.append(
                    Bundle(
                        index=stored_index,
                        name=self._read_ref_string(name_ref),
                        dependencies=self._read_ref_int_array(dependencies_ref),
                        direct_reverse_dependencies=self._read_ref_int_array(reverse_ref),
                        direct_dependencies=self._read_ref_int_array(direct_ref),
                        flags=flags,
                        hash_name=hash_name,
                        hash_version=hash_version,
                        root_category=root_category,
                    )
                )
            self._validate_references(records)
            self._bundles = tuple(records)
        return self._bundles

    def find_bundles(self, queries: Iterable[str]) -> list[Bundle]:
        """Resolve exact manifest paths or exact basenames, rejecting ambiguity."""
        result = []
        seen = set()
        for raw_query in queries:
            query = raw_query.strip().replace("\\", "/").casefold()
            if not query:
                raise ValueError("Bundle query must not be empty")
            if "/" in query:
                matches = [item for item in self.bundles if item.name.casefold() == query]
            else:
                matches = [
                    item
                    for item in self.bundles
                    if item.name.rsplit("/", 1)[-1].casefold() == query
                ]
            if not matches:
                raise KeyError(f"Bundle not found: {raw_query}")
            if len(matches) != 1:
                names = ", ".join(item.name for item in matches[:5])
                raise KeyError(f"Ambiguous Bundle basename {raw_query}: {names}")
            if matches[0].index not in seen:
                seen.add(matches[0].index)
                result.append(matches[0])
        return result

    def _validate_references(self, bundles: list[Bundle]) -> None:
        for bundle in bundles:
            for relation, indices in (
                ("dependencies", bundle.dependencies),
                ("direct_reverse_dependencies", bundle.direct_reverse_dependencies),
                ("direct_dependencies", bundle.direct_dependencies),
            ):
                invalid = next((index for index in indices if index < 0 or index >= len(bundles)), None)
                if invalid is not None:
                    raise ManifestFormatError(
                        f"Bundle {bundle.index} {relation} contains invalid index {invalid}"
                    )

    def _read_header_string(self, offset: int) -> tuple[str, int]:
        char_count, offset = self._read_i32(offset)
        if char_count < 0:
            raise ManifestFormatError(f"negative header string length at {offset - 4}")
        byte_count = char_count * 2
        self._require(offset, byte_count, "header UTF-16 string")
        return self._data[offset : offset + byte_count].decode("utf-16le"), offset + byte_count

    def _read_segment(self, offset: int) -> tuple[int, int, int]:
        length, payload = self._read_i32(offset)
        if length < 0:
            raise ManifestFormatError(f"negative segment length at {offset}")
        self._require(payload, length, "manifest segment")
        return payload, length, payload + length

    def _read_ref_string(self, relative_offset: int) -> str:
        offset = self._ref_offset(relative_offset, "RefString")
        byte_count = self._unpack_from("<i", offset)[0]
        if byte_count < 0 or byte_count % 2:
            raise ManifestFormatError(f"invalid RefString byte length {byte_count} at {offset}")
        self._require_shared(relative_offset + 4, byte_count, "RefString payload")
        return self._data[offset + 4 : offset + 4 + byte_count].decode("utf-16le")

    def _read_ref_int_array(self, relative_offset: int) -> tuple[int, ...]:
        offset = self._ref_offset(relative_offset, "RefArray<int>")
        count = self._unpack_from("<i", offset)[0]
        if count < 0:
            raise ManifestFormatError(f"negative RefArray count at {offset}")
        self._require_shared(relative_offset + 4, count * 4, "RefArray<int> payload")
        if count == 0:
            return ()
        return self._unpack_from(f"<{count}i", offset + 4)

    def _ref_offset(self, relative_offset: int, label: str) -> int:
        if relative_offset < 0:
            raise ManifestFormatError(f"negative {label} offset {relative_offset}")
        self._require_shared(relative_offset, 4, label)
        offset = self.data_offset + relative_offset
        return offset

    def _require_shared(self, relative_offset: int, length: int, label: str) -> None:
        if (
            relative_offset < 0
            or length < 0
            or relative_offset + length > self.shared_data_length
        ):
            raise ManifestFormatError(
                f"{label} exceeds shared data bounds: offset={relative_offset} "
                f"length={length} size={self.shared_data_length}"
            )

    def _read_i32(self, offset: int) -> tuple[int, int]:
        return self._unpack_from("<i", offset)[0], offset + 4

    def _read_u32(self, offset: int) -> tuple[int, int]:
        return self._unpack_from("<I", offset)[0], offset + 4

    def _unpack_from(self, fmt: str, offset: int):
        self._require(offset, struct.calcsize(fmt), fmt)
        return struct.unpack_from(fmt, self._data, offset)

    def _require(self, offset: int, length: int, label: str) -> None:
        if offset < 0 or length < 0 or offset + length > len(self._data):
            raise ManifestFormatError(
                f"{label} exceeds file bounds: offset={offset} length={length} size={len(self._data)}"
            )
