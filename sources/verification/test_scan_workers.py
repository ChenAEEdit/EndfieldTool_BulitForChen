import contextlib
import importlib.util
import io
from pathlib import Path
import sqlite3
import sys
import tempfile
import time
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "index_game_resources.py"
sys.path.insert(0, str(SCRIPT.parent))
spec = importlib.util.spec_from_file_location("index_game_resources", SCRIPT)
scanner = importlib.util.module_from_spec(spec)
assert spec and spec.loader
spec.loader.exec_module(scanner)


class ScanWorkersTests(unittest.TestCase):
    def test_worker_counts_produce_identical_hits_and_cache_only_complete_chunks(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as temporary:
            root = Path(temporary)
            paths = []
            grouped = {}
            signatures = {"g": "blc-signature"}
            for index, name in enumerate((
                b"chr_0025_ardelia_uimodel",
                b"a_actor_ardelia_ui_overview_loop_01",
                b"a_actor_deepfin_ui_relax_loop",
            )):
                chunk = f"chunk-{index}"
                path = root / f"{chunk}.chk"
                payload = b"\x00" + name + b"\x00" + (b"x" * (4096 + index * 19))
                path.write_bytes(payload)
                record = {"offset": 0, "length": len(payload), "use_encrypt": False,
                          "name": f"{chunk}.ab", "group": "g"}
                paths.append(("g", chunk, path))
                grouped[("g", chunk)] = [record]

            expected = None
            for workers in (1, 4, 8):
                connection = sqlite3.connect(":memory:")
                connection.execute("CREATE TABLE chunks (id TEXT PRIMARY KEY, signature TEXT NOT NULL, sha256 TEXT NOT NULL, hits TEXT NOT NULL)")
                total = sum(path.stat().st_size for _, _, path in paths)
                with contextlib.redirect_stdout(io.StringIO()):
                    hits, completed, reused = scanner.scan_chunks(
                        paths, grouped, signatures, connection, True, workers, total)
                normalized = sorted((row["character"], row["name"], row["kind"]) for row in hits)
                self.assertEqual(reused, 0)
                self.assertEqual(completed, total)
                self.assertEqual(connection.execute("SELECT COUNT(*) FROM chunks").fetchone()[0], len(paths))
                if expected is None:
                    expected = normalized
                self.assertEqual(normalized, expected)

                with contextlib.redirect_stdout(io.StringIO()):
                    cached_hits, cached_bytes, cached_count = scanner.scan_chunks(
                        paths, grouped, signatures, connection, False, workers, total)
                self.assertEqual(cached_count, len(paths))
                self.assertEqual(cached_bytes, total)
                self.assertEqual(sorted((row["character"], row["name"], row["kind"]) for row in cached_hits), expected)
                connection.close()

            connection = sqlite3.connect(":memory:")
            connection.execute("CREATE TABLE chunks (id TEXT PRIMARY KEY, signature TEXT NOT NULL, sha256 TEXT NOT NULL, hits TEXT NOT NULL)")
            original_scan = scanner.scan_chunk
            def fail_one_chunk(path, records, on_progress):
                if path.name == "chunk-1.chk":
                    time.sleep(0.2)
                    raise OSError("synthetic worker failure")
                return original_scan(path, records, on_progress)
            with patch.object(scanner, "scan_chunk", side_effect=fail_one_chunk):
                with contextlib.redirect_stdout(io.StringIO()), self.assertRaises(OSError):
                    scanner.scan_chunks(paths, grouped, signatures, connection, True, 2, total)
            completed_keys = {row[0] for row in connection.execute("SELECT id FROM chunks")}
            self.assertIn("g:chunk-0", completed_keys)
            self.assertNotIn("g:chunk-1", completed_keys)
            self.assertLess(len(completed_keys), len(paths))
            connection.close()


if __name__ == "__main__":
    unittest.main()
