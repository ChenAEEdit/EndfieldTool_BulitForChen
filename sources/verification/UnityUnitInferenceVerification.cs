using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace EndfieldCharacterWorkbench.Verification
{
// Infer source-unit preservation from measured bone distances, never character IDs or body height.
public static class UnityUnitInferenceVerification
{
    [Serializable] class Request { public string directory; public Input[] cases; }
    [Serializable] class Input { public string role, model, evidence, mappings; public string[] clips; }
    [Serializable] class Mappings { public Mapping[] candidates; }
    [Serializable] class Mapping { public string pathId, sourceAsset; public HumanBone[] bones; }
    [Serializable] class HumanBone { public string m_BoneName, m_HumanName; }
    [Serializable] class Rig { public Node[] nodes; }
    [Serializable] class Node { public string path, parent, reference; public Vector3 position, scale; public Quaternion rotation; }
    [Serializable] class Report { public bool passed; public string error; public List<Check> cases = new List<Check>(); }
    [Serializable] class Check
    {
        public string role, model, inference = "source-skin-bind-segment-lengths-vs-imported-FBX";
        public float initialGlobalScale, initialFileScale, modelImportScale, maxRatioDeviation, maxCorrectedRelativeError;
        public int measuredSegments;
        public bool passed;
        public List<PositionCheck> animations = new List<PositionCheck>();
    }
    [Serializable] class PositionCheck { public string clip, status; public int segments; public float positionScale, maxRatioDeviation; }
    static float Median(IEnumerable<float> values) { var a = values.OrderBy(x => x).ToArray(); return a[a.Length / 2]; }
    static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
    public static void Run()
    {
        var request = JsonUtility.FromJson<Request>(File.ReadAllText(Environment.GetEnvironmentVariable("ENDFIELD_UNIT_INFERENCE_CHECK")));
        var report = new Report();
        try
        {
            foreach (var input in request.cases)
            {
                var rig = JsonUtility.FromJson<Rig>(File.ReadAllText(input.evidence));
                var nodes = rig.nodes.ToDictionary(n => n.path);
                var worlds = new Dictionary<string, Matrix4x4>();
                foreach (var node in rig.nodes.OrderBy(n => n.path.Count(c => c == '/')).ThenBy(n => n.path.Length))
                    worlds[node.path] = node.path.Length == 0 ? Matrix4x4.identity : worlds[node.parent ?? ""] * Matrix4x4.TRS(node.position, node.rotation, node.scale);
                var importer = (ModelImporter)AssetImporter.GetAtPath(input.model);
                importer.globalScale = 1;
                importer.SaveAndReimport();
                var check = new Check { role = input.role, model = input.model, initialGlobalScale = importer.globalScale, initialFileScale = importer.fileScale };
                report.cases.Add(check);
                Func<Dictionary<string, float>> measure = () => {
                    var model = AssetDatabase.LoadAssetAtPath<GameObject>(input.model);
                    var transforms = model.GetComponentsInChildren<Transform>(true).ToDictionary(t => AnimationUtility.CalculateTransformPath(t, model.transform));
                    var lengths = new Dictionary<string, float>();
                    foreach (var node in rig.nodes.Where(n => n.reference == "skin-bind-pose" && n.parent != null && nodes[n.parent].reference == "skin-bind-pose"))
                        if (transforms.TryGetValue(node.path, out var t) && transforms.TryGetValue(node.parent, out var p))
                        {
                            float expected = Vector3.Distance(worlds[node.path].MultiplyPoint3x4(Vector3.zero), worlds[node.parent].MultiplyPoint3x4(Vector3.zero));
                            float actual = Vector3.Distance(t.position, p.position);
                            if (expected > .02f && actual > .0000001f && Finite(expected) && Finite(actual)) lengths[node.path] = expected / actual;
                        }
                    return lengths;
                };
                var before = measure();
                if (before.Count < 15) throw new Exception("Insufficient unique measured bone segments: " + input.role);
                check.measuredSegments = before.Count;
                float ratio = Median(before.Values);
                check.maxRatioDeviation = before.Values.Max(v => Mathf.Abs(v / ratio - 1));
                if (check.maxRatioDeviation > .01f || !Finite(ratio) || ratio < .0001f || ratio > 10000) throw new Exception("Nonuniform or invalid FBX unit evidence: " + input.role);
                // Round only if a familiar decimal scale is supported by all measured distances.
                float nearest = Mathf.Pow(10, Mathf.Round(Mathf.Log10(ratio)));
                check.modelImportScale = Mathf.Abs(nearest / ratio - 1) < .0001f ? nearest : ratio;
                importer.globalScale = check.modelImportScale;
                importer.SaveAndReimport();
                var after = measure();
                check.maxCorrectedRelativeError = after.Values.Max(v => Mathf.Abs(v - 1));
                if (check.maxCorrectedRelativeError > .01f) throw new Exception("Inferred scale failed independent reimport measurement: " + input.role);
                var names = rig.nodes.GroupBy(n => n.path.Split('/').Last()).ToDictionary(g => g.Key, g => g.Count());
                var matching = JsonUtility.FromJson<Mappings>(File.ReadAllText(input.mappings)).candidates
                    .Where(c => c.bones.Length >= 15 && c.bones.All(b => names.ContainsKey(b.m_BoneName) && names[b.m_BoneName] == 1))
                    .GroupBy(c => string.Join("|", c.bones.OrderBy(b => b.m_HumanName).Select(b => b.m_HumanName + ":" + b.m_BoneName))).ToArray();
                if (matching.Length != 1) throw new Exception("Ambiguous semantic body mapping; no first-candidate fallback: " + input.role);
                // Equivalent semantic maps can be shared by multiple Avatars; this does not identify clip ownership.
                var mapping = matching.Single().First();
                var primaryNames = new HashSet<string>(mapping.bones.Select(b => b.m_BoneName));
                foreach (string clipPath in input.clips ?? new string[0])
                {
                    var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
                    var position = new PositionCheck { clip = clipPath, status = "insufficient-evidence" };
                    var ratios = new List<float>();
                    foreach (var group in AnimationUtility.GetCurveBindings(clip).Where(b => b.type == typeof(Transform) && b.propertyName.StartsWith("m_LocalPosition.")).GroupBy(b => b.path))
                    {
                        // Facial/cloth/corrective nodes can intentionally translate away from rest.
                        // Use a unique semantic map matching the hierarchy, not statistical outlier removal.
                        if (!primaryNames.Contains(group.Key.Split('/').Last()) || !nodes.TryGetValue(group.Key, out var node) || node.reference != "skin-bind-pose" || node.position.magnitude < .05f) continue;
                        var channels = group.ToDictionary(b => b.propertyName, b => AnimationUtility.GetEditorCurve(clip, b));
                        if (!channels.ContainsKey("m_LocalPosition.x") || !channels.ContainsKey("m_LocalPosition.y") || !channels.ContainsKey("m_LocalPosition.z")) continue;
                        var values = Enumerable.Range(0, 25).Select(i => { float t = clip.length * i / 24;
                            return new Vector3(channels["m_LocalPosition.x"].Evaluate(t), channels["m_LocalPosition.y"].Evaluate(t), channels["m_LocalPosition.z"].Evaluate(t)).magnitude; }).ToArray();
                        if (values.Any(v => !Finite(v) || v < .000001f)) continue;
                        float length = Median(values);
                        if (values.Max(v => Mathf.Abs(v / length - 1)) > .01f) continue;
                        ratios.Add(node.position.magnitude / length);
                    }
                    position.segments = ratios.Count;
                    if (ratios.Count >= 8)
                    {
                        position.positionScale = Median(ratios);
                        position.maxRatioDeviation = ratios.Max(v => Mathf.Abs(v / position.positionScale - 1));
                        position.status = position.maxRatioDeviation <= .01f ? "measured" : "inconsistent";
                    }
                    check.animations.Add(position);
                }
                check.passed = check.animations.Count > 0 && check.animations.All(a => a.status == "measured");
                if (!check.passed) throw new Exception("Animation position units cannot be determined safely: " + input.role);
            }
            report.passed = true;
        }
        catch (Exception error) { report.error = error.ToString(); Debug.LogException(error); }
        File.WriteAllText(Path.Combine(request.directory, "unit-verification.json"), JsonUtility.ToJson(report, true));
        EditorApplication.Exit(report.passed ? 0 : 1);
    }
}
}
