using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace EndfieldCharacterWorkbench.Editor
{
public static class KneeDiagnostics
{
    [Serializable] class Pose { public string name; public Vector3 position, scale; public Quaternion rotation; }
    [Serializable] class Sample { public float time, edge99, maxEdge, compressedEdgeFraction; public Pose[] bones; }
    [Serializable] class Variant { public string walk, mode; public int kneeEdges, sampledVertices; public bool finiteGeometry; public Sample[] samples; public string[] images; }
    [Serializable] class Report { public string schema = "endfield-knee-diagnostic-v1", unityVersion, error, comparisonScene; public Variant[] variants; public bool sourceAssetsModified = false; }
    class Region { public SkinnedMeshRenderer renderer; public Mesh baked; public int[] a, b; public float[] lengths; }

    public static void RunBatch()
    {
        string output = Environment.GetEnvironmentVariable("ENDFIELD_KNEE_REPORT");
        if (string.IsNullOrEmpty(output)) throw new Exception("Missing ENDFIELD_KNEE_REPORT.");
        Directory.CreateDirectory(output);
        var report = new Report { unityVersion = Application.unityVersion }; var variants = new List<Variant>();
        try
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            foreach (string walk in new[] { "ui", "acl" })
            {
                string scenePath = "Assets/EndfieldChenTool/" + (walk == "ui" ? "Chen_UI_Walk.unity" : "Chen_Reextracted.unity");
                var loaded = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                try
                {
                    var original = loaded.GetRootGameObjects().SelectMany(go => go.GetComponentsInChildren<Animator>(true)).Single(a => a.name == "Chen_Qianyu");
                    foreach (string mode in new[] { "current", "direct_source", "without_knee_correctives" })
                        variants.Add(Inspect(original, walk, mode, output));
                }
                finally { EditorSceneManager.CloseScene(loaded, true); }
            }
            report.comparisonScene = CreateUiComparison();
        }
        catch (Exception error) { report.error = error.ToString(); }
        report.variants = variants.ToArray();
        File.WriteAllText(Path.Combine(output, "diagnostic.json"), JsonUtility.ToJson(report, true));
        EditorApplication.Exit(report.error == null ? 0 : 1);
    }

    static string CreateUiComparison()
    {
        string folder = "Assets/EndfieldCharacterDiagnostics/Generated/UI_Knee_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        Directory.CreateDirectory(folder); AssetDatabase.Refresh();
        var scene = EditorSceneManager.OpenScene("Assets/EndfieldChenTool/Chen_UI_Walk.unity", OpenSceneMode.Single);
        var original = scene.GetRootGameObjects().SelectMany(go => go.GetComponentsInChildren<Animator>(true)).Single(a => a.name == "Chen_Qianyu");
        var candidate = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/EndfieldChenTool/Animations/Chen_UI_Walk.anim"));
        foreach (var binding in AnimationUtility.GetCurveBindings(candidate))
            if (binding.type == typeof(Transform) && binding.path.Contains("corrective_Knee")) AnimationUtility.SetEditorCurve(candidate, binding, null);
        candidate.name = "Chen_UI_Walk_KneeCandidate";
        AssetDatabase.CreateAsset(candidate, folder + "/Chen_UI_Walk_KneeCandidate.anim");
        var controller = UnityEditor.Animations.AnimatorController.CreateAnimatorControllerAtPath(folder + "/KneeCandidate.controller");
        var state = controller.layers[0].stateMachine.AddState("KneeCandidate"); state.motion = candidate;
        controller.layers[0].stateMachine.defaultState = state;
        var left = UnityEngine.Object.Instantiate(original.gameObject); var right = UnityEngine.Object.Instantiate(original.gameObject);
        var comparison = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(left, comparison);
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(right, comparison);
        left.name = "Left_Original_Shared_Walk"; right.name = "Right_Knee_Candidate_Shared_Walk";
        left.transform.position += Vector3.left * .45f; right.transform.position += Vector3.right * .45f;
        right.GetComponent<Animator>().runtimeAnimatorController = controller;
        foreach (var behaviour in left.GetComponentsInChildren<MonoBehaviour>(true).Concat(right.GetComponentsInChildren<MonoBehaviour>(true))) behaviour.enabled = false;
        UnityEngine.SceneManagement.SceneManager.SetActiveScene(comparison);
        var camera = new GameObject("Comparison Camera").AddComponent<Camera>(); camera.tag = "MainCamera";
        camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.16f, .18f, .21f);
        camera.transform.position = new Vector3(0, .75f, -3); camera.transform.LookAt(new Vector3(0, .75f, 0));
        camera.orthographic = true; camera.orthographicSize = .47f;
        var light = new GameObject("Comparison Light").AddComponent<Light>(); light.type = LightType.Directional;
        light.transform.rotation = Quaternion.Euler(30, -20, 0); light.intensity = 1.2f;
        string path = folder + "/Chen_UI_Knee_Comparison.unity";
        EditorSceneManager.SaveScene(comparison, path); AssetDatabase.SaveAssets();
        EditorSceneManager.CloseScene(scene, true);
        return path;
    }

    static Variant Inspect(Animator original, string walk, string mode, string output,
        string clipOverride = null, string stateOverride = null, Avatar avatarOverride = null)
    {
        var scene = EditorSceneManager.NewPreviewScene();
        GameObject root = null; AnimationClip direct = null; AnimatorOverrideController temporaryController = null;
        var regions = new List<Region>();
        try
        {
            root = UnityEngine.Object.Instantiate(original.gameObject);
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
            foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true)) behaviour.enabled = false;
            foreach (var transform in root.GetComponentsInChildren<Transform>(true)) transform.gameObject.layer = 31;
            var animator = root.GetComponent<Animator>(); animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            if (avatarOverride) animator.avatar = avatarOverride;
            var transforms = root.GetComponentsInChildren<Transform>(true);
            var positions = transforms.Select(t => t.localPosition).ToArray();
            var rotations = transforms.Select(t => t.localRotation).ToArray();
            var scales = transforms.Select(t => t.localScale).ToArray();
            var knees = transforms.Where(t => t.name == "Bip001_L_Calf" || t.name == "Bip001_R_Calf").ToArray();
            if (knees.Length != 2) throw new Exception("Missing knee transforms.");
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var baked = new Mesh(); renderer.BakeMesh(baked);
                var vertices = baked.vertices; var triangles = baked.triangles;
                var local = vertices.Select(vertex => renderer.transform.TransformPoint(vertex)).ToArray();
                var selected = local.Select(vertex => knees.Any(knee => Vector3.Distance(vertex, knee.position) < .12f)).ToArray();
                var edges = new HashSet<ulong>(); var a = new List<int>(); var b = new List<int>(); var lengths = new List<float>();
                for (int i = 0; i < triangles.Length; i += 3)
                    for (int j = 0; j < 3; j++)
                    {
                        int first = triangles[i + j], second = triangles[i + (j + 1) % 3];
                        if (!selected[first] || !selected[second]) continue;
                        int lo = Math.Min(first, second), hi = Math.Max(first, second);
                        if (!edges.Add(((ulong)(uint)lo << 32) | (uint)hi)) continue;
                        float length = Vector3.Distance(local[lo], local[hi]); if (length < .0001f) continue;
                        a.Add(lo); b.Add(hi); lengths.Add(length);
                    }
                regions.Add(new Region { renderer = renderer, baked = baked, a = a.ToArray(), b = b.ToArray(), lengths = lengths.ToArray() });
            }
            string clipBase = "Assets/EndfieldChenTool/Animations/" + (walk == "ui" ? "Chen_UI_Walk" : "Chen_Walk");
            var current = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipOverride ?? clipBase + ".anim");
            if (mode == "direct_source")
            {
                var source = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipBase + "_Source.anim");
                direct = CharacterAnimationAdapter.CreateDirectClip(root, source, new CharacterAnimationAdapter.Rules
                { dropUnmatched = true, normalizeRoot = walk == "ui" ? "Root" : "" }, new CharacterAnimationAdapter.ClipReport());
                animator.enabled = false;
            }
            else if (mode == "without_knee_correctives")
            {
                direct = UnityEngine.Object.Instantiate(current);
                foreach (var binding in AnimationUtility.GetCurveBindings(direct))
                    if (binding.type == typeof(Transform) && binding.path.Contains("corrective_Knee")) AnimationUtility.SetEditorCurve(direct, binding, null);
                temporaryController = new AnimatorOverrideController(animator.runtimeAnimatorController);
                var overrides = new List<KeyValuePair<AnimationClip, AnimationClip>>(); temporaryController.GetOverrides(overrides);
                if (!overrides.Any(pair => pair.Key == current)) throw new Exception("Current controller does not reference the expected walk clip.");
                temporaryController.ApplyOverrides(overrides.Select(pair => new KeyValuePair<AnimationClip, AnimationClip>(pair.Key, pair.Key == current ? direct : pair.Value)).ToList());
                animator.runtimeAnimatorController = temporaryController;
            }
            var samples = new List<Sample>(); float worst = -1, worstTime = 0; int sampledVertices = 0;
            Action<float> pose = time =>
            {
                for (int i = 0; i < transforms.Length; i++) { transforms[i].localPosition = positions[i]; transforms[i].localRotation = rotations[i]; transforms[i].localScale = scales[i]; }
                if (mode == "direct_source") direct.SampleAnimation(root, time);
                else { animator.Rebind(); animator.Update(0); animator.Play(stateOverride == null ? 0 : Animator.StringToHash(stateOverride), 0, time / current.length); animator.Update(0); }
            };
            float duration = direct ? direct.length : current.length;
            for (int index = 0; index < 25; index++)
            {
                float time = duration * index / 24f; pose(time);
                var ratios = new List<float>();
                foreach (var region in regions)
                {
                    region.renderer.BakeMesh(region.baked);
                    var vertices = region.baked.vertices;
                    sampledVertices += vertices.Length;
                    if (vertices.Any(v => float.IsNaN(v.x) || float.IsInfinity(v.x) || float.IsNaN(v.y) || float.IsInfinity(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.z)))
                        throw new Exception("Non-finite sampled geometry at " + time + " / " + region.renderer.name);
                    for (int i = 0; i < region.a.Length; i++)
                        ratios.Add(Vector3.Distance(region.renderer.transform.TransformPoint(vertices[region.a[i]]), region.renderer.transform.TransformPoint(vertices[region.b[i]])) / region.lengths[i]);
                }
                ratios.Sort(); if (ratios.Count == 0) throw new Exception("No knee region edges found.");
                var sample = new Sample { time = time, edge99 = ratios[Math.Min(ratios.Count - 1, (int)(ratios.Count * .99f))], maxEdge = ratios[ratios.Count - 1],
                    compressedEdgeFraction = ratios.Count(value => value < .25f) / (float)ratios.Count,
                    bones = transforms.Where(t => t.name.Contains("Calf") || t.name.Contains("corrective_Knee") || t.name.EndsWith("_Thigh"))
                        .Select(t => new Pose { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale }).ToArray() };
                samples.Add(sample);
                if (sample.edge99 > worst) { worst = sample.edge99; worstTime = time; }
            }
            pose(worstTime);
            string directory = Path.Combine(output, walk + "_" + mode); Directory.CreateDirectory(directory);
            var center = (knees[0].position + knees[1].position) * .5f;
            var images = new List<string>();
            foreach (int side in new[] { -1, 1 })
            {
                string path = Path.Combine(directory, side < 0 ? "knees_front.png" : "knees_back.png");
                Capture(root, center, side, path); images.Add(path);
            }
            foreach (float fraction in new[] { 0f, .25f, .5f, .75f })
            {
                pose(duration * fraction); center = (knees[0].position + knees[1].position) * .5f;
                foreach (int side in new[] { -1, 2 })
                {
                    string path = Path.Combine(directory, "phase_" + Mathf.RoundToInt(fraction * 100).ToString("D2") + (side == -1 ? "_front.png" : "_side.png"));
                    Capture(root, center, side, path); images.Add(path);
                }
            }
            return new Variant { walk = walk, mode = mode, kneeEdges = regions.Sum(region => region.a.Length), sampledVertices = sampledVertices, finiteGeometry = true, samples = samples.ToArray(), images = images.ToArray() };
        }
        finally
        {
            foreach (var region in regions) UnityEngine.Object.DestroyImmediate(region.baked);
            if (direct) UnityEngine.Object.DestroyImmediate(direct);
            if (temporaryController) UnityEngine.Object.DestroyImmediate(temporaryController);
            EditorSceneManager.ClosePreviewScene(scene);
        }
    }

    static void Capture(GameObject root, Vector3 center, int side, string path)
    {
        var go = new GameObject("Knee diagnostic camera"); var lightObject = new GameObject("Knee diagnostic light");
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, root.scene);
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(lightObject, root.scene);
        var camera = go.AddComponent<Camera>(); camera.scene = root.scene; camera.cullingMask = 1 << 31;
        camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.16f, .18f, .21f);
        camera.orthographic = true; camera.orthographicSize = .30f; camera.nearClipPlane = .01f;
        camera.transform.position = center + (side == 2 ? root.transform.right * 1.5f : root.transform.forward * (side * 1.5f) + root.transform.right * .15f);
        camera.transform.LookAt(center);
        var light = lightObject.AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 1.3f; light.cullingMask = 1 << 31;
        light.transform.rotation = camera.transform.rotation;
        var texture = new RenderTexture(768, 768, 24); var image = new Texture2D(768, 768, TextureFormat.RGB24, false);
        var previous = RenderTexture.active;
        try { camera.targetTexture = texture; camera.Render(); RenderTexture.active = texture; image.ReadPixels(new Rect(0, 0, 768, 768), 0, 0); image.Apply(); File.WriteAllBytes(path, image.EncodeToPNG()); }
        finally { RenderTexture.active = previous; camera.targetTexture = null; texture.Release(); UnityEngine.Object.DestroyImmediate(texture); UnityEngine.Object.DestroyImmediate(image); UnityEngine.Object.DestroyImmediate(go); UnityEngine.Object.DestroyImmediate(lightObject); }
    }
}
}
