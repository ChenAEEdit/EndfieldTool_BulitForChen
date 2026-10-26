"""Read cached manifest relationships for acceptance; no game writes."""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
from endfield_manifest import EndfieldManifest
from index_formats import parse_blc

root = Path(sys.argv[1])
scan = json.loads((root / "scan-result.json").read_text(encoding="utf-8-sig"))
catalog_path = Path(scan["catalogPath"])
namespace = catalog_path.parents[2]
metadata = namespace / "metadata"
manifest = EndfieldManifest.from_path(metadata / "manifest.bin")
catalog = json.loads(catalog_path.read_text(encoding="utf-8-sig"))
rows = []
for file in metadata.glob("*.blc.bin"):
    group_rows = []
    parse_blc(file.read_bytes(), group_rows.append)
    rows.extend(dict(row, group=file.name.split(".")[0]) for row in group_rows)
by_name = {}
for row in rows:
    name = row["name"].replace("\\", "/").lower().split("/windows/", 1)[-1]
    by_name.setdefault(name, []).append(row)
report = []
for code in ("avywen", "lifeng", "aurora", "wolfgd"):
    actor = next(c for c in catalog["characters"] if c["id"] == code)
    models = [r for r in actor["resources"] if r["kind"] == "Model"]
    found = []
    for candidate in models:
        bundle = manifest.find_bundles([Path(candidate["sourcePath"]).name])[0]
        reverse = [manifest.bundles[i] for i in bundle.direct_reverse_dependencies]
        found.append({"candidate": candidate, "bundle": bundle.name,
                      "dependencies": [manifest.bundles[i].name for i in bundle.direct_dependencies],
                      "reverse": [{"name": b.name, "reverseCount": len(b.direct_reverse_dependencies),
                                   "dependencyCount": len(b.dependencies), "rows": by_name.get(b.name.lower(), [])} for b in reverse]})
    report.append({"actor": code, "candidates": found})
(root / "candidate-relationships.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
for actor in report:
    print(actor["actor"])
    for row in actor["candidates"]:
        print(" ", row["bundle"], "deps", len(row["dependencies"]), "reverse", len(row["reverse"]))
        for reverse in row["reverse"][:15]: print("   ", reverse["name"], "deps", reverse["dependencyCount"], "reverse", reverse["reverseCount"])
