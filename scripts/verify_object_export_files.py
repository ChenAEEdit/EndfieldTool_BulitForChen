"""Independently read exports; optionally import the FBX in Blender."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import math
from pathlib import Path
import re
import sys


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha256(path):
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(4 * 1024 * 1024), b""):
            value.update(block)
    return value.hexdigest().upper()


def inspect_animation(row):
    path = Path(row["path"])
    numbers = re.compile(r"[-+]?(?:(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?|Infinity|NaN|\.inf|\.nan)", re.IGNORECASE)
    curves = keys = values = changes = unknown = bad_times = bad_values = bad_slopes = reversed_times = 0
    last_value, last_time, changed = None, None, False
    name, max_time = None, 0.0
    with path.open(encoding="utf-8-sig") as stream:
        require(stream.readline().strip() == "%YAML 1.1", "Missing Unity YAML header: " + str(path))
        for line in stream:
            if line.startswith("  m_Name: "):
                name = line.strip().split(": ", 1)[1]
            elif line.startswith("  - curve:"):
                curves += 1
                last_value = last_time = None
                changed = False
            elif line.startswith("        time: "):
                keys += 1
                value = float(line.split(":", 1)[1])
                if not math.isfinite(value):
                    bad_times += 1
                else:
                    if last_time is not None and value < last_time:
                        reversed_times += 1
                    max_time = max(max_time, value)
                    last_time = value
            elif line.startswith("        value: "):
                values += 1
                value = line.split(":", 1)[1].strip()
                if any(not math.isfinite(float(number)) for number in numbers.findall(value)):
                    bad_values += 1
                if last_value is not None and last_value != value and not changed:
                    changes += 1
                    changed = True
                last_value = value
            elif line.startswith(("        inSlope:", "        outSlope:")):
                if any(not math.isfinite(float(number)) for number in numbers.findall(line.split(":", 1)[1])):
                    bad_slopes += 1
            elif re.match(r"^    path: path_\d+\s*$", line):
                unknown += 1
    require(curves == row["curves"] and keys == row["keys"] and keys == values and curves > 0 and keys > 0,
            "Animation curve/key counts disagree: " + str(path))
    require(bad_times == bad_values == reversed_times == 0 and changes > 0 and max_time > 0,
            "Animation values/times are invalid or do not change: " + str(path))
    require(unknown == row["unknownPaths"], "Unknown path count disagrees: " + str(path))
    return {"name": name, "path": str(path), "curves": curves, "keys": keys, "changingCurves": changes,
            "maxKeyTime": max_time, "unknownPaths": unknown, "nonFiniteSlopes": bad_slopes,
            "nonFiniteValues": bad_values, "nonFiniteTimes": bad_times, "reversedTimes": reversed_times}


def inspect_fbx(path):
    import bpy
    bpy.ops.wm.read_factory_settings(use_empty=True)
    require("FINISHED" in bpy.ops.import_scene.fbx(filepath=str(path), use_anim=False), "Blender FBX import failed.")
    meshes = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
    armatures = [obj for obj in bpy.context.scene.objects if obj.type == "ARMATURE"]
    require(meshes and armatures, "Imported FBX lacks meshes or an armature.")
    output = []
    for obj in meshes:
        unweighted = invalid_weights = non_finite = 0
        max_sum_error = 0.0
        for vertex in obj.data.vertices:
            if not all(math.isfinite(value) for value in vertex.co):
                non_finite += 1
            weights = [group.weight for group in vertex.groups]
            if not weights or sum(weights) <= 0:
                unweighted += 1
            invalid_weights += sum(not math.isfinite(value) or value < 0 or value > 1.0001 for value in weights)
            if weights:
                max_sum_error = max(max_sum_error, abs(sum(weights) - 1))
        require(non_finite == invalid_weights == 0, "Invalid FBX coordinates or weights: " + obj.name)
        require(any(mod.type == "ARMATURE" and mod.object in armatures for mod in obj.modifiers), "Missing armature modifier: " + obj.name)
        output.append({"name": obj.name, "vertices": len(obj.data.vertices), "polygons": len(obj.data.polygons),
            "vertexGroups": len(obj.vertex_groups), "unweightedVertices": unweighted,
            "invalidWeights": invalid_weights, "nonFiniteCoordinates": non_finite, "maxWeightSumError": max_sum_error})
    return {"path": str(path), "meshCount": len(meshes), "vertexCount": sum(len(obj.data.vertices) for obj in meshes),
        "armatures": [{"name": obj.name, "bones": len(obj.data.bones)} for obj in armatures], "meshes": output}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ui-report", required=True, type=Path)
    parser.add_argument("--report", required=True, type=Path)
    parser.add_argument("--skip-fbx-import", action="store_true", help="Use regular Python; only verify FBX header, length and SHA256.")
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:])
    blender_version = None
    if not args.skip_fbx_import:
        import bpy
        blender_version = bpy.app.version_string
    result = {"schema": "endfield-export-file-verification-v1", "passed": False,
        "checkedAt": datetime.now(timezone.utc).isoformat(), "blenderVersion": blender_version,
        "uiReport": str(args.ui_report), "filesChecked": 0, "animations": [], "model": None,
        "unityImported": False, "adapted": False, "playbackVerified": False, "error": None}
    try:
        ui = json.loads(args.ui_report.read_text(encoding="utf-8-sig"))
        require(ui["passed"], "Workbench action checks did not pass.")
        for operation in (ui["full"], ui["animationsOnly"]):
            for row in operation["files"]:
                path = Path(row["path"])
                require(path.is_file() and path.stat().st_size == row["bytes"] and sha256(path) == row["sha256"],
                    "Export length or SHA256 mismatch: " + str(path))
                result["filesChecked"] += 1
                if row["kind"] == "AnimationSource":
                    result["animations"].append(inspect_animation(row))
                elif row["kind"] == "FBX":
                    if args.skip_fbx_import:
                        with path.open("rb") as stream:
                            require(stream.read(23).startswith(b"Kaydara FBX Binary"), "Invalid FBX binary header.")
                        result["model"] = {"path": str(path), "fileHeaderValid": True, "geometryImported": False}
                    else:
                        result["model"] = inspect_fbx(path)
                        result["model"]["geometryImported"] = True
        require(result["model"] and len(result["animations"]) == 10, "Incomplete model/animation verification.")
        result["passed"] = True
    except Exception as error:
        result["error"] = str(error)
        raise
    finally:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(result, indent=2, ensure_ascii=False), encoding="utf-8")
        print(json.dumps({"passed": result["passed"], "filesChecked": result["filesChecked"], "report": str(args.report),
            "error": result["error"]}, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    main()
