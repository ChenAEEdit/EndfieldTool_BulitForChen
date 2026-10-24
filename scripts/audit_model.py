"""Blender-side audit of imported mesh weights; no edits to game resources."""
import argparse
import json
import math
from pathlib import Path
import sys

import bpy

parser = argparse.ArgumentParser()
parser.add_argument("--fbx", required=True)
parser.add_argument("--report", required=True)
parser.add_argument("--blend", required=True)
args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
bpy.ops.object.select_all(action="SELECT")
bpy.ops.object.delete(use_global=False)
if hasattr(bpy.ops.wm, "fbx_import"):
    bpy.ops.wm.fbx_import(filepath=str(Path(args.fbx).resolve()))
else:
    bpy.ops.import_scene.fbx(filepath=str(Path(args.fbx).resolve()))
armatures = [o for o in bpy.data.objects if o.type == "ARMATURE"]
bone_names = {b.name for armature in armatures for b in armature.data.bones}
meshes = []
for obj in [o for o in bpy.data.objects if o.type == "MESH"]:
    missing = invalid = nonfinite = not_normalized = absent_bones = 0
    for vertex in obj.data.vertices:
        total = 0
        for group in vertex.groups:
            if group.group >= len(obj.vertex_groups):
                invalid += 1
                continue
            if not math.isfinite(group.weight):
                nonfinite += 1
            if group.weight > 1e-8 and obj.vertex_groups[group.group].name not in bone_names:
                absent_bones += 1
            total += group.weight
        if total <= 1e-8:
            missing += 1
        elif abs(total - 1) > .01:
            not_normalized += 1
    meshes.append(dict(name=obj.name, vertices=len(obj.data.vertices), unweighted=missing,
                       invalid_group_references=invalid, nonfinite_weights=nonfinite,
                       weight_sum_outside_1pct=not_normalized, weighted_groups_without_bones=absent_bones))
fields = ["unweighted", "invalid_group_references", "nonfinite_weights", "weight_sum_outside_1pct", "weighted_groups_without_bones"]
result = dict(schema="endfield-chen-tool-weights-v1", blender=bpy.app.version_string,
              fbx=str(Path(args.fbx).resolve()), armatures=len(armatures), meshes=meshes,
              mesh_count=len(meshes), vertices=sum(x["vertices"] for x in meshes))
result.update({field: sum(x[field] for x in meshes) for field in fields})
result["passed"] = bool(armatures and len(meshes) == 10 and result["vertices"] == 56490 and not any(result[f] for f in fields))
Path(args.report).parent.mkdir(parents=True, exist_ok=True)
Path(args.report).write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
Path(args.blend).parent.mkdir(parents=True, exist_ok=True)
bpy.ops.wm.save_as_mainfile(filepath=str(Path(args.blend).resolve()))
print(json.dumps({key: value for key, value in result.items() if key != "meshes"}, ensure_ascii=False))
if not result["passed"]:
    raise SystemExit(7)
