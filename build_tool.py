"""Package the locally verified exporters with a Windows GUI. No game assets are bundled."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parent
WORK = ROOT.parent
EVIDENCE = WORK / "outputs/ZMD_unpack"
REPO = WORK / "work/ZMD_unpack"
PACKAGE = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path("D:/AAAChatGPT/EndfieldChenTool")


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def copy(source, target):
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, target)


profiles = ROOT / "profiles"
templates = ROOT / "templates"
licenses = ROOT / "licenses"
for directory in (profiles, templates, licenses, PACKAGE):
    directory.mkdir(parents=True, exist_ok=True)
bundle_map = {}


def add(record, role, group=None):
    name = Path(record["name"]).name
    entry = bundle_map.setdefault(name, {"name": name, "group": group or record["group"], "md5": record["data_md5"], "roles": []})
    if role not in entry["roles"]:
        entry["roles"].append(role)


for folder in ("chen_model_closure", "chen_model_initialdeps"):
    for record in read(EVIDENCE / folder / "extract_report.json")["files"]:
        add(record, "model")
initial = read(EVIDENCE / "initial_entries.json")
add(next(r for r in initial if Path(r["name"]).name == "883c322914dd6cf6f62424d8.ab"), "ui", "0CE8FA57")
records = read(EVIDENCE / "chen_animation_candidates/extract_report.json")["files"]
add(next(r for r in records if Path(r["name"]).name == "418bf85b31936bf1f35d8d9e.ab"), "acl")
profile = {
    "schema": "endfield-chen-tool-profile-v1", "character": "chen", "display_name": "陈千语", "build": "20260821_c24495e5",
    "game_assembly_sha256": "C24495E51B406F03B03890C4788EE618AE022C991405BE5D5B8B787CB775AE89",
    "root_name": "chr_0005_chen_uimodel", "root_path_id": "8239783119078153412", "mesh_count": 10, "vertex_count": 56490,
    "animations": {
        "ui": {"label": "UI 行走 female_walk", "clip_name": "female_walk", "source_asset": "Chen_UI_Walk_Source.anim", "rig": "Generic"},
        "acl": {"label": "ACL 行走 customized walk", "clip_name": "A_actor_chen_customized_state_walk_loop", "source_asset": "Chen_Walk_Source.anim", "rig": "Humanoid"}
    }, "bundles": sorted(bundle_map.values(), key=lambda x: x["name"])
}
(profiles / "chen.json").write_text(json.dumps(profile, ensure_ascii=False, indent=2), encoding="utf-8")
copy(REPO / "work/vfs_probe/endfield_vfs.py", ROOT / "scripts/endfield_vfs.py")
builder = (ROOT / "sources/manual/ChenReextractedBuilder.cs").read_text(encoding="utf-8-sig")
builder = builder.replace("ChenReextractedBuilder", "SceneBuilder").replace("ChenPlaybackControls", "PlaybackControls")
builder = builder.replace("Assets/ChenReextracted", "Assets/EndfieldChenTool").replace("Chen Reextracted/Build and inspect scene", "Endfield Chen Tool/重建 ACL 预览")
for old, new in (("ChenRuntimeCheck", "EndfieldChenTool.RuntimeCheck"), ("ChenRuntimeUiWalk", "EndfieldChenTool.RuntimeUiWalk"), ("ChenRuntimeReport", "EndfieldChenTool.RuntimeReport")):
    builder = builder.replace('"' + old + '"', '"' + new + '"')
reset = '''    static void UseAclWalk()
    {
        uiWalk = false;
        SourcePath = Base + "/Animations/Chen_Walk_Source.anim";
        ClipPath = Base + "/Animations/Chen_Walk.anim";
        ControllerPath = Base + "/Chen.controller";
        ScenePath = Base + "/Chen_Reextracted.unity";
    }
'''
if "    static void UseAclWalk()" not in builder:
    builder = builder.replace("    static void UseUiWalk()", reset + "    static void UseUiWalk()")
builder = builder.replace("public static void BuildInteractive() { Build(false); }", "public static void BuildInteractive() { UseAclWalk(); Build(false); }")
builder = builder.replace("public static void BuildBatch() { Build(true); }", "public static void BuildBatch() { UseAclWalk(); Build(true); }")
builder = builder.replace("        Directory.CreateDirectory(report);", "        Directory.CreateDirectory(report);\n        Directory.CreateDirectory(Base + \"/Materials\");")
open_methods = '''
    [MenuItem("Endfield Chen Tool/打开 UI 行走预览")]
    public static void OpenUiSceneInteractive() { OpenPreview(Base + "/Chen_UI_Walk.unity"); }
    [MenuItem("Endfield Chen Tool/打开 ACL 行走预览")]
    public static void OpenAclSceneInteractive() { OpenPreview(Base + "/Chen_Reextracted.unity"); }
    static void OpenPreview(string path)
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        EditorSceneManager.OpenScene(path);
        EditorApplication.ExecuteMenuItem("Window/General/Game");
        EditorApplication.EnterPlaymode();
    }
'''
builder = builder.replace("    const string Base", open_methods + "\n    const string Base", 1)
index = builder.index("public static class SceneBuilder")
builder = builder[:index] + "namespace EndfieldChenTool.Generated\n{\n" + builder[index:] + "\n}\n"
(templates / "SceneBuilder.cs").write_text(builder, encoding="utf-8")
controls = (EVIDENCE / "unity_stage/ChenPlaybackControls.cs").read_text(encoding="utf-8-sig").replace("ChenPlaybackControls", "PlaybackControls")
index = controls.index("public class PlaybackControls")
controls = controls[:index] + "namespace EndfieldChenTool.Generated\n{\n" + controls[index:] + "\n}\n"
(templates / "PlaybackControls.cs").write_text(controls, encoding="utf-8")
for name, directory in (("model", "ModelExportCompat"), ("animation", "AnimationExportCompat")):
    source = REPO / "work/vfs_probe" / directory / "bin/Release/net10.0"
    target = ROOT / "runtime" / name
    shutil.copytree(source, target, dirs_exist_ok=True)
    for file in ("Program.cs", directory + ".csproj"):
        copy(REPO / "work/vfs_probe" / directory / file, ROOT / "sources/exporters" / directory / file)
model_runtime = ROOT / "runtime/model"
animation_runtime = ROOT / "runtime/animation"
for path in model_runtime.glob("*.dll"):
    if path.name.startswith(("AnimeStudio", "ModelExport")):
        continue
    if not (animation_runtime / path.name).exists():
        copy(path, animation_runtime / path.name)
copy(REPO / "work/upstream/Ruri.RipperHook/LICENSE", licenses / "Ruri.RipperHook-AGPL-3.0.txt")
upstream = next(p for p in (REPO / "work/upstream/AnimeStudio-EIHR").iterdir() if p.is_dir() and p.name.startswith("AnimeStudio-"))
copy(upstream / "LICENSE", licenses / "AnimeStudio-LICENSE.txt")
if (upstream / "THIRD_PARTY_NOTICES.md").is_file():
    copy(upstream / "THIRD_PARTY_NOTICES.md", licenses / "AnimeStudio-THIRD_PARTY_NOTICES.md")
for path in (REPO / "patches").glob("*.patch"):
    copy(path, ROOT / "sources/patches" / path.name)
for path in (REPO / "work/upstream/Ruri.RipperHook/Source/Ruri.ACL").glob("*.cs"):
    copy(path, ROOT / "sources/Ruri.ACL" / path.name)
copy(REPO / "work/upstream/Ruri.RipperHook/Source/Ruri.ACL/Ruri.ACL.csproj", ROOT / "sources/Ruri.ACL/Ruri.ACL.csproj")
copy(upstream / "AnimeStudio.Utility/YAML/AnimationClipConverter.cs", ROOT / "sources/AnimationClipConverter.cs")

env = os.environ.copy()
env["DOTNET_CLI_HOME"] = str(ROOT / "build_state/dotnet")
env["APPDATA"] = str(ROOT / "build_state/appdata")
for variable in ("DOTNET_CLI_HOME", "APPDATA"):
    Path(env[variable]).mkdir(parents=True, exist_ok=True)
env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
env["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
subprocess.run(["dotnet", "build", str(ROOT / "Crypto/VfsCrypto.csproj"), "-c", "Release", "-o", str(ROOT / "runtime/crypto"),
                "--configfile", str(ROOT / "NuGet.Config"), "--verbosity", "minimal"], check=True, env=env)
subprocess.run(["dotnet", "build", str(ROOT / "EndfieldChenTool.csproj"), "-c", "Release", "-o", str(PACKAGE),
                "--configfile", str(ROOT / "NuGet.Config"), "--verbosity", "minimal"], check=True, env=env)
for file in ("pipeline.py", "README.md", "THIRD_PARTY.md"):
    copy(ROOT / file, PACKAGE / file)
for folder in ("profiles", "templates", "scripts", "runtime", "licenses", "sources", "docs"):
    shutil.copytree(ROOT / folder, PACKAGE / folder, dirs_exist_ok=True,
                    ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
for file in ("Program.cs", "EndfieldChenTool.csproj", "NuGet.Config", "build_tool.py"):
    copy(ROOT / file, PACKAGE / "sources/gui" / file)
shutil.copytree(ROOT / "Framework", PACKAGE / "sources/gui/Framework", dirs_exist_ok=True)
for file in ("Program.cs", "VfsCrypto.csproj", "BundleExtractor.cs"):
    copy(ROOT / "Crypto" / file, PACKAGE / "sources/crypto" / file)
print(f"PACKAGED {PACKAGE} / bundles={len(profile['bundles'])}")
