using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using EndfieldCharacterWorkbench.Editor;

namespace EndfieldCharacterWorkbench.Verification
{
// Positive pose checks independently sample decoded key poses; negative cases
// call the adapter only to assert rejection. Generated files remain unchanged.
public static class UnitySharedRigVerification
{
    [Serializable] class Request { public string directory = ""; public string[] configs = new string[0]; }
    [Serializable] class FileDigest { public string path, sha256, kind; }
    [Serializable] class Result { public string schema = "endfield-shared-rig-verification-v1", error; public bool passed, inputsUnchanged, outputsUnchanged; public List<FileDigest> files = new List<FileDigest>(); public List<Check> clips = new List<Check>(); public List<string> directClips = new List<string>(), rejectedInvalidInputs = new List<string>(); }
    [Serializable] class BoneError { public string path; public float degrees, time; public Quaternion source, target; }
    [Serializable] class Check { public string config, clip, referencePolicy = "decoded-key-poses-with-constant-tangents-and-quaternion-continuity"; public int sampleCount, bodyBones, referenceTangentAdjustments; public float maxRotationError, maxPrimarySegmentAngle, sourceKeyPoseMaxError, maxLeftArmElevation = -90, maxRightArmElevation = -90; public string worstBone; public List<BoneError> errors = new List<BoneError>(); }
    public static void Run()
    {
        var request = JsonUtility.FromJson<Request>(File.ReadAllText(Environment.GetEnvironmentVariable("ENDFIELD_SHARED_RIG_CHECK")));
        var result = new Result();
        Directory.CreateDirectory(request.directory);
        var scene = EditorSceneManager.NewPreviewScene();
        try
        {
            foreach (var configPath in request.configs)
            {
                string root = Path.GetDirectoryName(configPath).Replace('\\', '/');
                var config = JsonUtility.FromJson<CharacterAnimationAdapter.Config>(File.ReadAllText(configPath));
                var report = JsonUtility.FromJson<CharacterAnimationAdapter.BuildReport>(File.ReadAllText(root + "/Generated/adaptation-result.json"));
                if (!report.adapted) throw new Exception("Generation failed: " + configPath);
                foreach (var input in config.inputs)
                {
                    string path = Path.GetFullPath(Path.Combine(root, input.path));
                    if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Input outside package");
                    if (Digest(path) != input.sha256) throw new Exception("Input digest differs: " + input.path);
                    result.files.Add(new FileDigest { path = path, sha256 = input.sha256, kind = "input" });
                }
                foreach (var output in report.outputs)
                {
                    string path = Path.GetFullPath(output.path);
                    if (!path.StartsWith(Path.GetFullPath(root + "/Generated") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Digest(path) != output.sha256)
                        throw new Exception("Output digest differs: " + output.path);
                    result.files.Add(new FileDigest { path = path, sha256 = output.sha256, kind = "output" });
                }
                foreach (var input in config.clips)
                {
                    var detail = report.clips.Single(c => c.id == input.id);
                    if (detail.humanoid || detail.retargetMode == "direct") { result.directClips.Add(input.name); continue; }
                    var evidence = JsonUtility.FromJson<CharacterAnimationAdapter.RigEvidence>(File.ReadAllText(root + "/" + input.sourceRig));
                    if (detail.worldBodyPaths.Count < 15 || detail.bodyAxisChecks.Count < 8) throw new Exception("Missing body/axis audit");
                    int tangentAdjustments;
                    float keyPoseError;
                    var sourceClip = ReferenceClip(AssetDatabase.LoadAssetAtPath<AnimationClip>(root + "/" + input.source), detail.worldBodyPaths, out tangentAdjustments, out keyPoseError);
                    var outputClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(detail.output);
                    var source = new GameObject("UnmodifiedSource");
                    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(source, scene);
                    var nodes = new Dictionary<string, Transform>();
                    var target = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(root + "/Generated/Character.prefab"));
                    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(target, scene);
                    try
                    {
                        foreach (var n in evidence.nodes.OrderBy(n => n.path.Count(c => c == '/')).ThenBy(n => n.path.Length))
                        {
                            var t = n.path.Length == 0 ? source.transform : new GameObject(n.path.Split('/').Last()).transform;
                            if (n.path.Length > 0) t.SetParent(nodes[n.parent ?? ""], false);
                            t.localPosition = n.serializedPosition; t.localRotation = n.serializedRotation; t.localScale = n.serializedScale;
                            nodes.Add(n.path, t);
                        }
                        foreach (var a in target.GetComponentsInChildren<Animator>(true)) a.enabled = false;
                        foreach (var b in target.GetComponentsInChildren<MonoBehaviour>(true)) b.enabled = false;
                        foreach (var mode in new[] { "reversed-axis", "incomplete-quaternion", "ambiguous-body-bind", "missing-semantic-map" })
                        {
                            VerifyRejected(mode, target, sourceClip, evidence, detail);
                            result.rejectedInvalidInputs.Add(input.name + ":" + mode);
                        }
                        var targets = target.GetComponentsInChildren<Transform>(true).ToDictionary(t => AnimationUtility.CalculateTransformPath(t, target.transform));
                        var pairs = detail.worldBodyPaths.Select(path => new { path, source = nodes[path], target = targets.Single(p => p.Key == path || p.Key.EndsWith("/" + path, StringComparison.Ordinal)).Value }).ToArray();
                        var allRest = nodes.Values.Concat(targets.Values).Select(t => new { t, p = t.localPosition, q = t.localRotation, s = t.localScale }).ToArray();
                        var check = new Check { config = configPath, clip = input.name, bodyBones = pairs.Length, sampleCount = 25, referenceTangentAdjustments = tangentAdjustments, sourceKeyPoseMaxError = keyPoseError };
                        result.clips.Add(check);
                        for (int frame = 0; frame < check.sampleCount; frame++)
                        {
                            foreach (var n in allRest) { n.t.localPosition = n.p; n.t.localRotation = n.q; n.t.localScale = n.s; }
                            float time = sourceClip.length * frame / (check.sampleCount - 1);
                            sourceClip.SampleAnimation(source, time); outputClip.SampleAnimation(target, time);
                            foreach (var p in pairs)
                            {
                                float angle = Quaternion.Angle(p.source.rotation, p.target.rotation);
                                var error = check.errors.FirstOrDefault(e => e.path == p.path);
                                if (error == null) { error = new BoneError { path = p.path }; check.errors.Add(error); }
                                if (angle > error.degrees) { error.degrees = angle; error.time = time; error.source = p.source.rotation; error.target = p.target.rotation; }
                                if (angle > check.maxRotationError) { check.maxRotationError = angle; check.worstBone = p.path; }
                                foreach (var child in pairs.Where(c => c.source.parent == p.source && c.target.parent == p.target
                                    && detail.bodyAxisChecks.Any(a => a.path == p.path && a.childPath == c.path)))
                                    check.maxPrimarySegmentAngle = Mathf.Max(check.maxPrimarySegmentAngle, Vector3.Angle(child.source.position - p.source.position, child.target.position - p.target.position));
                            }
                            foreach (var side in new[] { "L", "R" })
                            {
                                var upper = pairs.Single(p => p.source.name == "Bip001_" + side + "_UpperArm").target;
                                var lower = pairs.Single(p => p.source.name == "Bip001_" + side + "_Forearm").target;
                                float elevation = 90 - Vector3.Angle(Vector3.up, lower.position - upper.position);
                                if (side == "L") check.maxLeftArmElevation = Mathf.Max(check.maxLeftArmElevation, elevation);
                                else check.maxRightArmElevation = Mathf.Max(check.maxRightArmElevation, elevation);
                            }
                        }
                        if (check.maxRotationError > .25f || check.maxPrimarySegmentAngle > 5) throw new Exception("Source/output pose mismatch: " + input.name);
                    }
                    finally { UnityEngine.Object.DestroyImmediate(source); UnityEngine.Object.DestroyImmediate(target); UnityEngine.Object.DestroyImmediate(sourceClip); }
                }
            }
            foreach (var file in result.files)
                if (Digest(file.path) != file.sha256) throw new Exception("Verification changed a package file: " + file.path);
            result.inputsUnchanged = true; result.outputsUnchanged = true;
            if (result.clips.Count == 0) throw new Exception("No shared retargeted clip to verify");
            result.passed = result.clips.Count > 0;
        }
        catch (Exception error) { result.error = error.ToString(); }
        finally { EditorSceneManager.ClosePreviewScene(scene); File.WriteAllText(Path.Combine(request.directory, "shared-rig-verification.json"), JsonUtility.ToJson(result, true)); }
        EditorApplication.Exit(result.passed ? 0 : 1);
    }
    static string Digest(string path)
    {
        using (var stream = File.OpenRead(path))
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
    }
    static AnimationClip ReferenceClip(AnimationClip raw, IEnumerable<string> bodyPaths, out int adjustments, out float keyPoseError)
    {
        // Independent implementation of the stated reference policy. Raw file,
        // key values and times are preserved; invalid tangent metadata is explicit.
        adjustments = 0;
        var reference = new AnimationClip { frameRate = raw.frameRate, legacy = false };
        foreach (var binding in AnimationUtility.GetCurveBindings(raw).Where(b => b.type == typeof(Transform)))
        {
            var original = AnimationUtility.GetEditorCurve(raw, binding);
            var curve = new AnimationCurve(original.keys) { preWrapMode = original.preWrapMode, postWrapMode = original.postWrapMode };
            for (int i = 0; i < curve.length; i++)
            {
                var key = original.keys[i];
                if (float.IsNaN(key.inTangent) || float.IsInfinity(key.inTangent))
                { AnimationUtility.SetKeyBroken(curve, i, true); AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Constant); adjustments++; }
                if (float.IsNaN(key.outTangent) || float.IsInfinity(key.outTangent))
                { AnimationUtility.SetKeyBroken(curve, i, true); AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Constant); adjustments++; }
            }
            if (curve.keys.Where((k, i) => k.time != original.keys[i].time || k.value != original.keys[i].value).Any()) throw new Exception("Reference changed source key values");
            AnimationUtility.SetEditorCurve(reference, binding, curve);
        }
        var snapshots = bodyPaths.Select(path => new {
            path,
            curves = "xyzw".Select(axis => AnimationUtility.GetEditorCurve(reference, EditorCurveBinding.FloatCurve(path, typeof(Transform), "m_LocalRotation." + axis))).ToArray()
        }).Select(row => new { row.path, times = row.curves.SelectMany(c => c.keys.Select(k => k.time)).Distinct().ToArray(), row.curves }).ToArray();
        reference.EnsureQuaternionContinuity(); keyPoseError = 0;
        foreach (var row in snapshots)
        {
            var after = "xyzw".Select(axis => AnimationUtility.GetEditorCurve(reference, EditorCurveBinding.FloatCurve(row.path, typeof(Transform), "m_LocalRotation." + axis))).ToArray();
            foreach (float time in row.times)
            {
                var beforeQ = new Quaternion(row.curves[0].Evaluate(time), row.curves[1].Evaluate(time), row.curves[2].Evaluate(time), row.curves[3].Evaluate(time));
                var afterQ = new Quaternion(after[0].Evaluate(time), after[1].Evaluate(time), after[2].Evaluate(time), after[3].Evaluate(time));
                float angle = Quaternion.Angle(beforeQ.normalized, afterQ.normalized);
                if (float.IsNaN(angle) || angle > .1f) throw new Exception("Reference changed a decoded key pose: " + row.path);
                keyPoseError = Mathf.Max(keyPoseError, angle);
            }
        }
        return reference;
    }
    static void VerifyRejected(string mode, GameObject target, AnimationClip source, CharacterAnimationAdapter.RigEvidence original, CharacterAnimationAdapter.ClipReport detail)
    {
        var rig = JsonUtility.FromJson<CharacterAnimationAdapter.RigEvidence>(JsonUtility.ToJson(original));
        rig.bodyBoneNames = detail.worldBodyPaths.Select(p => p.Split('/').Last()).ToArray();
        rig.bodyAxisChildNames = detail.bodyAxisChecks.Select(c => c.childPath.Split('/').Last()).Distinct().ToArray();
        var clip = UnityEngine.Object.Instantiate(source);
        AnimationClip unexpected = null;
        string expected = "";
        try
        {
            var node = rig.nodes.Single(n => n.path.EndsWith("/Bip001_L_Forearm", StringComparison.Ordinal));
            if (mode == "reversed-axis") { node.serializedPosition = -node.serializedPosition; expected = "骨轴"; }
            if (mode == "ambiguous-body-bind") { node.reference = "ambiguous-skin-bind-pose"; expected = "歧义"; }
            if (mode == "missing-semantic-map") { rig.bodyBoneNames = new string[0]; expected = "映射"; }
            if (mode == "incomplete-quaternion")
            {
                var binding = AnimationUtility.GetCurveBindings(clip).Single(b => b.path == node.path && b.propertyName == "m_LocalRotation.w");
                AnimationUtility.SetEditorCurve(clip, binding, null); expected = "四元数";
            }
            try { unexpected = CharacterAnimationAdapter.RetargetSourcePose(target, clip, rig, new CharacterAnimationAdapter.ClipReport()); }
            catch (Exception error) { if (error.Message.Contains(expected)) return; throw; }
            throw new Exception("Invalid source was accepted: " + mode);
        }
        finally { if (unexpected) UnityEngine.Object.DestroyImmediate(unexpected); UnityEngine.Object.DestroyImmediate(clip); }
    }
}
}
