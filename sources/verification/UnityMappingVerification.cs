using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using EndfieldCharacterWorkbench.Editor;

namespace EndfieldCharacterWorkbench.Verification
{
public static class UnityMappingVerification
{
    [Serializable] class Request { public string directory = ""; public string[] configs = new string[0]; }
    [Serializable] class CheckResult { public string name, detail; public bool passed; }
    [Serializable] class Capabilities { public string config; public int cloth, rigidbodies, joints, monoBehaviours, skinnedMeshes, blendShapes; public string[] components, shaders; public bool originalGamePhysicsRestored = false; }
    [Serializable] class Report { public bool passed; public string error; public List<CheckResult> checks = new List<CheckResult>(); public List<Capabilities> capabilities = new List<Capabilities>(); }
    static Report report;
    static void Check(string name, bool condition, string detail = "") { report.checks.Add(new CheckResult { name = name, passed = condition, detail = detail }); if (!condition) throw new Exception(name); }
    static void Reject(string name, Action action) { try { action(); } catch (Exception error) { Check(name, true, error.Message); return; } throw new Exception("Expected rejection: " + name); }
    static AnimationClip Source(string path)
    {
        var clip = new AnimationClip();
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(Transform), "m_LocalPosition.x"), AnimationCurve.Linear(0, 2, 1, 4));
        return clip;
    }
    public static void Run()
    {
        var request = JsonUtility.FromJson<Request>(File.ReadAllText(Environment.GetEnvironmentVariable("ENDFIELD_MAPPING_CHECK")));
        Directory.CreateDirectory(request.directory); report = new Report();
        var root = new GameObject("MappingFixture"); var garbage = new List<UnityEngine.Object> { root };
        try
        {
            var left = new GameObject("Left"); left.transform.parent = root.transform;
            var right = new GameObject("Right"); right.transform.parent = root.transform;
            foreach (var parent in new[] { left, right }) { var bone = new GameObject("Joint"); bone.transform.parent = parent.transform; }
            string[] paths = { "", "Left", "Left/Joint", "Right", "Right/Joint" };
            Check("Exact path wins over duplicate leaf names", CharacterAnimationAdapter.MatchPath("Left/Joint", paths) == "Left/Joint");
            Check("Unique suffix with additional source prefix", CharacterAnimationAdapter.MatchPath("Source/Left/Joint", paths) == "Left/Joint");
            Check("Ambiguous suffix rejected", CharacterAnimationAdapter.MatchPath("Joint", paths) == null);
            Check("Missing path rejected", CharacterAnimationAdapter.MatchPath("Missing", paths) == null);
            Check("Unresolved hash rejected", CharacterAnimationAdapter.MatchPath("path_123456", paths) == null);
            var missing = Source("Joint"); garbage.Add(missing);
            Reject("Strict adaptation rejects ambiguous binding", () => CharacterAnimationAdapter.CreateDirectClip(root, missing, new CharacterAnimationAdapter.Rules(), new CharacterAnimationAdapter.ClipReport()));
            var source = Source("Left/Joint"); garbage.Add(source);
            Reject("Invalid excluded node rejected", () => CharacterAnimationAdapter.CreateDirectClip(root, source, new CharacterAnimationAdapter.Rules { excludedNodes = new[] { "Absent" } }, new CharacterAnimationAdapter.ClipReport()));
            var duplicate = new GameObject("Left"); duplicate.transform.parent = root.transform;
            Reject("Duplicate hierarchy rejected", () => CharacterAnimationAdapter.CreateDirectClip(root, source, new CharacterAnimationAdapter.Rules(), new CharacterAnimationAdapter.ClipReport()));
            UnityEngine.Object.DestroyImmediate(duplicate);
            var collision = Source("Left/Joint"); garbage.Add(collision);
            AnimationUtility.SetEditorCurve(collision, EditorCurveBinding.FloatCurve("Source/Left/Joint", typeof(Transform), "m_LocalPosition.x"), AnimationCurve.Linear(0, 1, 1, 2));
            Reject("Two source tracks cannot overwrite one target", () => CharacterAnimationAdapter.CreateDirectClip(root, collision, new CharacterAnimationAdapter.Rules(), new CharacterAnimationAdapter.ClipReport()));
            var rules = new CharacterAnimationAdapter.Rules { positionScale = 2, normalizeRoot = "Left/Joint", forceLoop = true };
            var output = CharacterAnimationAdapter.CreateDirectClip(root, source, rules, new CharacterAnimationAdapter.ClipReport()); garbage.Add(output);
            var curve = AnimationUtility.GetEditorCurve(output, AnimationUtility.GetCurveBindings(output).Single());
            Check("Root offset removed and delta scaled", Math.Abs(curve.Evaluate(0)) < .0001f && Math.Abs(curve.Evaluate(1) - 4) < .0001f);
            Check("Source root curve preserved", Math.Abs(AnimationUtility.GetEditorCurve(source, AnimationUtility.GetCurveBindings(source).Single()).Evaluate(0) - 2) < .0001f);
            Check("Force loop retained on output", output.isLooping);
            var mixed = Source("Left/Joint"); garbage.Add(mixed);
            AnimationUtility.SetEditorCurve(mixed, EditorCurveBinding.FloatCurve("Absent", typeof(Transform), "m_LocalPosition.x"), AnimationCurve.Linear(0, 0, 1, 1));
            var droppedReport = new CharacterAnimationAdapter.ClipReport();
            var dropped = CharacterAnimationAdapter.CreateDirectClip(root, mixed, new CharacterAnimationAdapter.Rules { dropUnmatched = true }, droppedReport); garbage.Add(dropped);
            Check("Explicit lossy mode records exact dropped path", droppedReport.unmatched.SequenceEqual(new[] { "Absent" }) && droppedReport.retainedBindings == 1);
            foreach (string config in request.configs)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Path.GetDirectoryName(config).Replace('\\', '/') + "/Generated/Character.prefab");
                if (!prefab) throw new Exception("Missing generated prefab " + config);
                var components = prefab.GetComponentsInChildren<Component>(true);
                var skins = prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                report.capabilities.Add(new Capabilities { config = config, cloth = components.Count(c => c is Cloth), rigidbodies = components.Count(c => c is Rigidbody), joints = components.Count(c => c is Joint), monoBehaviours = components.Count(c => c is MonoBehaviour), skinnedMeshes = skins.Length,
                    blendShapes = skins.Sum(s => s.sharedMesh.blendShapeCount), components = components.Where(c => c).Select(c => c.GetType().FullName).Distinct().OrderBy(n => n).ToArray(),
                    shaders = skins.SelectMany(s => s.sharedMaterials).Where(m => m).Select(m => m.shader.name).Distinct().ToArray() });
            }
            report.passed = true;
        }
        catch (Exception error) { report.error = error.ToString(); }
        finally { foreach (var item in garbage) if (item) UnityEngine.Object.DestroyImmediate(item); }
        File.WriteAllText(Path.Combine(request.directory, "mapping-capability-verification.json"), JsonUtility.ToJson(report, true));
        EditorApplication.Exit(report.passed ? 0 : 1);
    }
}
}
