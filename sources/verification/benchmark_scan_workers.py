import argparse
import contextlib
import io
import json
from pathlib import Path
import sqlite3
import sys
import time


ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
import index_game_resources as scanner


def main():
    parser = argparse.ArgumentParser(description="Benchmark scanner workers on actual CHK files.")
    parser.add_argument("--game", required=True, type=Path)
    parser.add_argument("--workspace", required=True, type=Path, help="existing scanner workspace")
    parser.add_argument("--chunks", type=int, default=3)
    parser.add_argument("--rounds", type=int, default=2)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    if args.chunks < 1 or args.rounds < 1:
        parser.error("--chunks and --rounds must be positive")

    game = scanner.normalize_game(args.game.resolve())
    workspace = args.workspace.resolve()
    fingerprint = scanner.digest(game.parent / "GameAssembly.dll")
    namespace = workspace / "index" / fingerprint / scanner.SCANNER_VERSION / scanner.ADAPTER_REVISION
    vfs = game / "StreamingAssets" / "VFS"
    records, signatures, _ = scanner.read_indexes(vfs, namespace / "metadata")
    grouped = {}
    for row in records:
        if row["name"].lower().endswith(".ab") and not row["use_encrypt"]:
            grouped.setdefault((row["group"], row["chunk_md5"]), []).append(row)
    candidates = []
    for group, chunk in grouped:
        path = vfs / group / (chunk + ".chk")
        if path.is_file():
            candidates.append((path.stat().st_size, group, chunk, path))
    selected = sorted(candidates)[:args.chunks]
    paths = [(group, chunk, path) for _, group, chunk, path in selected]
    total = sum(size for size, _, _, _ in selected)
    if not paths:
        raise RuntimeError("No readable CHK files were available for the benchmark.")

    results = []
    expected = None
    for round_index in range(args.rounds):
        worker_order = (1, 4, 8) if round_index % 2 == 0 else (8, 4, 1)
        for workers in worker_order:
            connection = sqlite3.connect(":memory:")
            connection.execute("CREATE TABLE chunks (id TEXT PRIMARY KEY, signature TEXT NOT NULL, sha256 TEXT NOT NULL, hits TEXT NOT NULL)")
            started = time.perf_counter()
            cpu_started = time.process_time()
            with contextlib.redirect_stdout(io.StringIO()):
                hits, scanned_bytes, reused = scanner.scan_chunks(
                    paths, grouped, signatures, connection, True, workers, total)
            elapsed = time.perf_counter() - started
            cpu_seconds = time.process_time() - cpu_started
            hashes = connection.execute("SELECT id,sha256 FROM chunks ORDER BY id").fetchall()
            connection.close()
            normalized = {
                "hits": sorted((row["character"], row["name"], row["kind"], row["group"], row["bundle"], row["chunk"], row["offset"]) for row in hits),
                "hashes": hashes,
            }
            if expected is None:
                expected = normalized
            elif normalized != expected:
                raise AssertionError(f"Worker {workers} produced different hits or CHK digests.")
            if scanned_bytes != total or reused != 0:
                raise AssertionError("Forced benchmark did not scan the full selected CHK set.")
            results.append({"round": round_index + 1, "workers": workers, "seconds": round(elapsed, 3),
                            "cpuSeconds": round(cpu_seconds, 3), "bytes": scanned_bytes,
                            "chunks": len(paths), "hits": len(hits), "sha256": hashes})

    aggregate = []
    for workers in (1, 4, 8):
        rows = [row for row in results if row["workers"] == workers]
        times = sorted(row["seconds"] for row in rows)
        cpu = sorted(row["cpuSeconds"] for row in rows)
        midpoint = len(rows) // 2
        median_time = times[midpoint] if len(rows) % 2 else (times[midpoint - 1] + times[midpoint]) / 2
        median_cpu = cpu[midpoint] if len(rows) % 2 else (cpu[midpoint - 1] + cpu[midpoint]) / 2
        aggregate.append({"workers": workers, "medianSeconds": round(median_time, 3),
                          "medianCpuSeconds": round(median_cpu, 3), "samplesSeconds": times})
    report = {"game": str(game), "chunks": [{"path": str(path), "bytes": size} for size, _, _, path in selected],
              "results": results, "aggregate": aggregate, "outputsIdentical": True}
    output = json.dumps(report, indent=2)
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(output, encoding="utf-8")
    print(output)


if __name__ == "__main__":
    main()
