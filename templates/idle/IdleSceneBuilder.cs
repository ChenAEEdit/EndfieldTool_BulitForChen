using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace EndfieldChenTool.Idle.Editor
{
public static class IdleSceneBuilder
{
    const string Base = "Assets/ChenIdle";
    const string RuntimeKey = "EndfieldChenIdle.RuntimeCheck";
    static readonly Type Core = typeof(EndfieldChenTool.Generated.SceneBuilder);
    static string ReportDirectory => Environment.GetEnvironmentVariable("CHEN_REPORT_DIR");
    [Serializable] public class Candidate { public string id, label, source, clip, scene, controller; }
    [Serializable] class Config { public Candidate[] candidates; }
    [Serializable] class CoreReport { public bool passed, savedPoseMatchesModel; public string error; public int missingCurves; }
    [Serializable] class FacePoseInfo { public int boneCount, changedBones; public float maximumPositionDelta, maximumRotationDelta, maximumScaleDelta; }
    [Serializable] class CandidateReport
    {
        public string id, source, clip, scene;
        public FacePoseInfo faceComparedWithNeutral;
        public Vector3 leftFootAtStart, rightFootAtStart;
        public float maximumLeftFootDisplacement, maximumRightFootDisplacement;
        public float duration, loopPositionError, loopRotationError;
        public int bindings, missingCurves;
        public bool meshPassed, savedPoseMatchesModel, loopPassed, passed;
    }
    [Serializable] class BuildReport { public bool passed; public string error; public CandidateReport[] candidates; public string scene; }
    [Serializable] class RuntimeSample { public float time, normalizedTime; public Vector3 size; public int nonFiniteVertices; public bool expectedState; }
    [Serializable] class RuntimeResult { public string id; public bool passed; public RuntimeSample[] samples; }
    [Serializable] class RuntimeReport { public bool passed; public string error; public RuntimeResult[] candidates; }
    static MethodInfo Method(string name) => Core.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
    static void Set(string name, object value) => Core.GetField(name, BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, value);
    static void Configure(Candidate c)
    {
        Set("uiWalk", false); Set("genericCharacterIdle", true);
        Set("SourcePath", c.source); Set("ClipPath", c.clip); Set("ScenePath", c.scene); Set("ControllerPath", c.controller);
    }
    static Config Load() => JsonUtility.FromJson<Config>(File.ReadAllText(Base + "/candidates.json"));
    static GameObject Character() => UnityEngine.Object.FindObjectsByType<Animator>(FindObjectsSortMode.None).First(a => a.name == "Chen_Qianyu").gameObject;
    static void Capture(GameObject root, string directory, string name, bool live = false)
    {
        Directory.CreateDirectory(directory);
        Method("Capture").Invoke(null, new object[] { Camera.main, Path.Combine(directory, name + "_body.png"), !live });
        Method("CaptureFace").Invoke(null, new object[] { Camera.main, root, Path.Combine(directory, name + "_face.png"), !live });
    }
    static void FreshReport(string path)
    {
        if (!File.Exists(path)) return;
        string history = Path.Combine(Path.GetDirectoryName(path), "history"); Directory.CreateDirectory(history);
        File.Move(path, Path.Combine(history, DateTime.UtcNow.Ticks + "-" + Path.GetFileName(path)));
    }
    [MenuItem("Endfield Chen Tool/打开待机预览")]
    public static void OpenPreview()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        EditorSceneManager.OpenScene(Base + "/Chen_Idle.unity");
        EditorApplication.ExecuteMenuItem("Window/General/Game");
        EditorApplication.EnterPlaymode();
    }
    public static void BuildOverviewBatch() { Build(true); }
    public static void BuildBatch() { Build(false); }
    public static void VerifySavedSceneBatch()
    {
        var report = new BuildReport();
        try
        {
            EditorSceneManager.OpenScene(Base + "/Chen_Idle.unity");
            var root = Character();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/EndfieldChenTool/Model/Chen.fbx");
            var rest = prefab.GetComponentsInChildren<Transform>(true).ToDictionary(t => AnimationUtility.CalculateTransformPath(t, prefab.transform));
            bool pose = root.GetComponentsInChildren<Transform>(true).All(t =>
            {
                var original = rest[AnimationUtility.CalculateTransformPath(t, root.transform)];
                return Vector3.Distance(t.localPosition, original.localPosition) < .000001f &&
                    Quaternion.Angle(t.localRotation, original.localRotation) < .01f &&
                    Vector3.Distance(t.localScale, original.localScale) < .000001f;
            });
            var animator = root.GetComponent<Animator>();
            var controller = animator.runtimeAnimatorController as AnimatorController;
            var controls = root.GetComponent<EndfieldChenTool.Idle.IdleControls>();
            report.passed = pose && animator.avatar && animator.avatar.isValid && !animator.isHuman &&
                controls.states.Length == 6 && controller.layers[0].stateMachine.states.Length == 6 &&
                controller.layers[0].stateMachine.defaultState.name == "Overview" &&
                Load().candidates.All(c => File.Exists(c.scene) && AssetDatabase.LoadAssetAtPath<AnimationClip>(c.clip) && !AssetDatabase.LoadAssetAtPath<AnimationClip>(c.clip).isHumanMotion);
            report.scene = Base + "/Chen_Idle.unity";
            if (!report.passed) report.error = "Saved idle scene, rest pose, or controller references did not match expectations.";
        }
        catch (Exception error) { report.error = error.ToString(); }
        Directory.CreateDirectory(ReportDirectory);
        File.WriteAllText(Path.Combine(ReportDirectory, "saved_scene_verification.json"), JsonUtility.ToJson(report, true));
        EditorApplication.Exit(report.passed ? 0 : 7);
    }
    static void Build(bool overviewOnly)
    {
        string reportDirectory = ReportDirectory;
        Directory.CreateDirectory(reportDirectory);
        var report = new BuildReport();
        var outcomes = new List<CandidateReport>();
        try
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            foreach (var c in Load().candidates.Where(c => !overviewOnly || c.id == "Overview"))
            {
                string detail = Path.Combine(reportDirectory, c.id);
                Directory.CreateDirectory(detail); FreshReport(Path.Combine(detail, "verification.json"));
                Configure(c); Environment.SetEnvironmentVariable("CHEN_REPORT_DIR", detail);
                Method("Build").Invoke(null, new object[] { false });
                string verification = Path.Combine(detail, "verification.json");
                if (!File.Exists(verification)) throw new Exception("No fresh build report: " + c.id);
                var core = JsonUtility.FromJson<CoreReport>(File.ReadAllText(verification));
                if (!core.passed) throw new Exception(c.id + " core mesh check failed: " + core.error);
                var root = Character();
                var animator = root.GetComponent<Animator>();
                var transforms = root.GetComponentsInChildren<Transform>(true);
                var restP = transforms.Select(t => t.localPosition).ToArray();
                var restQ = transforms.Select(t => t.localRotation).ToArray();
                var restS = transforms.Select(t => t.localScale).ToArray();
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(c.clip);
                var item = new CandidateReport { id = c.id, source = c.source, clip = c.clip, scene = c.scene,
                    duration = clip.length, bindings = AnimationUtility.GetCurveBindings(clip).Length,
                    missingCurves = core.missingCurves, meshPassed = core.passed, savedPoseMatchesModel = core.savedPoseMatchesModel };
                var startP = new Vector3[transforms.Length]; var startQ = new Quaternion[transforms.Length];
                for (int i = 0; i < 3; i++)
                {
                    float time = i == 0 ? 0 : i == 1 ? clip.length * .5f : clip.length - .0001f;
                    animator.Play("Walk", 0, time / clip.length); animator.Update(.00001f);
                    Capture(root, detail, "idle_" + i);
                    if (i == 0)
                    {
                        startP = transforms.Select(t => t.localPosition).ToArray(); startQ = transforms.Select(t => t.localRotation).ToArray();
                        item.faceComparedWithNeutral = JsonUtility.FromJson<FacePoseInfo>(JsonUtility.ToJson(Method("MeasureFace").Invoke(null, new object[] { root })));
                        item.leftFootAtStart = transforms.First(t => t.name == "Bip001_L_Foot").position;
                        item.rightFootAtStart = transforms.First(t => t.name == "Bip001_R_Foot").position;
                    }
                    item.maximumLeftFootDisplacement = Math.Max(item.maximumLeftFootDisplacement, Vector3.Distance(item.leftFootAtStart, transforms.First(t => t.name == "Bip001_L_Foot").position));
                    item.maximumRightFootDisplacement = Math.Max(item.maximumRightFootDisplacement, Vector3.Distance(item.rightFootAtStart, transforms.First(t => t.name == "Bip001_R_Foot").position));
                }
                item.loopPositionError = Enumerable.Range(0, transforms.Length).Max(i => Vector3.Distance(startP[i], transforms[i].localPosition));
                item.loopRotationError = Enumerable.Range(0, transforms.Length).Max(i => Quaternion.Angle(startQ[i], transforms[i].localRotation));
                item.loopPassed = item.loopPositionError < .005f && item.loopRotationError < 2;
                item.passed = item.meshPassed && item.savedPoseMatchesModel && item.loopPassed;
                animator.enabled = false;
                for (int i = 0; i < transforms.Length; i++) { transforms[i].localPosition = restP[i]; transforms[i].localRotation = restQ[i]; transforms[i].localScale = restS[i]; }
                var oldControls = root.GetComponent<EndfieldChenTool.Generated.PlaybackControls>();
                if (oldControls) UnityEngine.Object.DestroyImmediate(oldControls);
                var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(c.controller);
                controller.layers[0].stateMachine.states[0].state.name = c.id;
                clip.name = "Chen_Idle_" + c.id; EditorUtility.SetDirty(clip); EditorUtility.SetDirty(controller);
                var controls = root.AddComponent<EndfieldChenTool.Idle.IdleControls>();
                controls.states = new[] { c.id }; controls.labels = new[] { c.label };
                animator.enabled = true;
                EditorSceneManager.SaveScene(root.scene, c.scene); AssetDatabase.SaveAssets();
                File.WriteAllText(Path.Combine(detail, "idle_verification.json"), JsonUtility.ToJson(item, true));
                outcomes.Add(item);
            }
            var candidates = Load().candidates.Where(c => !overviewOnly || c.id == "Overview").ToArray();
            EditorSceneManager.OpenScene(candidates[0].scene);
            var main = Character();
            string controllerPath = Base + "/Chen_Idle.controller";
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath)) AssetDatabase.DeleteAsset(controllerPath);
            var combined = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
            foreach (var c in candidates)
            {
                var state = combined.layers[0].stateMachine.AddState(c.id);
                state.motion = AssetDatabase.LoadAssetAtPath<AnimationClip>(c.clip);
            }
            main.GetComponent<Animator>().runtimeAnimatorController = combined;
            var selector = main.GetComponent<EndfieldChenTool.Idle.IdleControls>();
            selector.states = candidates.Select(c => c.id).ToArray(); selector.labels = candidates.Select(c => c.label).ToArray();
            report.scene = Base + "/Chen_Idle.unity";
            EditorSceneManager.SaveScene(main.scene, report.scene); AssetDatabase.SaveAssets();
            report.candidates = outcomes.ToArray(); report.passed = outcomes.Count > 0 && outcomes.All(c => c.passed);
        }
        catch (Exception error) { report.error = error.ToString(); report.candidates = outcomes.ToArray(); Debug.LogException(error); }
        finally { Environment.SetEnvironmentVariable("CHEN_REPORT_DIR", reportDirectory); }
        File.WriteAllText(Path.Combine(reportDirectory, "verification.json"), JsonUtility.ToJson(report, true));
        EditorApplication.Exit(report.passed ? 0 : 7);
    }

    static readonly List<RuntimeResult> runtimeResults = new List<RuntimeResult>();
    static readonly List<RuntimeSample> runtimeSamples = new List<RuntimeSample>();
    static int current = -1;
    static float started;
    static double began;
    public static void CheckRuntimeBatch()
    {
        EditorSceneManager.OpenScene(Base + "/Chen_Idle.unity");
        SessionState.SetString(RuntimeKey + ".Report", ReportDirectory);
        SessionState.SetBool(RuntimeKey, true); EditorApplication.EnterPlaymode();
    }
    [InitializeOnLoadMethod]
    static void Resume()
    {
        if (!SessionState.GetBool(RuntimeKey, false)) return;
        began = EditorApplication.timeSinceStartup; EditorApplication.update += Tick;
    }
    static void Finish(RuntimeReport report)
    {
        string directory = SessionState.GetString(RuntimeKey + ".Report", "");
        File.WriteAllText(Path.Combine(directory, "runtime_verification.json"), JsonUtility.ToJson(report, true));
        SessionState.SetBool(RuntimeKey, false); EditorApplication.update -= Tick; EditorApplication.Exit(report.passed ? 0 : 7);
    }
    static void Tick()
    {
        if (!EditorApplication.isPlaying) return;
        try
        {
            if (EditorApplication.timeSinceStartup - began > 90) throw new Exception("Idle runtime check timed out.");
            var root = Character(); var animator = root.GetComponent<Animator>();
            var controls = root.GetComponent<EndfieldChenTool.Idle.IdleControls>();
            if (current < 0) { current = 0; started = Time.time; animator.Play(controls.states[current], 0, 0); }
            var state = animator.GetCurrentAnimatorStateInfo(0);
            float duration = AssetDatabase.LoadAssetAtPath<AnimationClip>(Load().candidates.First(c => c.id == controls.states[current]).clip).length;
            float target = runtimeSamples.Count == 0 ? .1f : runtimeSamples.Count == 1 ? duration * .5f : duration + .1f;
            if (Time.time - started < target) return;
            var skins = root.GetComponentsInChildren<SkinnedMeshRenderer>();
            var vertices = (Vector3[][])Method("Bake").Invoke(null, new object[] { skins });
            var bounds = (Bounds)Method("BoundsFor").Invoke(null, new object[] { skins, vertices });
            runtimeSamples.Add(new RuntimeSample { time = Time.time - started, normalizedTime = state.normalizedTime,
                size = bounds.size, expectedState = state.IsName(controls.states[current]),
                nonFiniteVertices = vertices.Sum(vs => vs.Count(v => !float.IsFinite(v.x) || !float.IsFinite(v.y) || !float.IsFinite(v.z))) });
            string directory = SessionState.GetString(RuntimeKey + ".Report", "");
            Capture(root, Path.Combine(directory, controls.states[current]), "runtime_" + runtimeSamples.Count, true);
            if (runtimeSamples.Count < 3) return;
            runtimeResults.Add(new RuntimeResult { id = controls.states[current], samples = runtimeSamples.ToArray(),
                passed = runtimeSamples.All(s => s.expectedState && s.nonFiniteVertices == 0 && s.size.y > 1 && s.size.y < 2.5f && s.size.magnitude < 4)
                    && runtimeSamples[2].normalizedTime > 1 && runtimeSamples[2].normalizedTime > runtimeSamples[0].normalizedTime });
            current++; runtimeSamples.Clear();
            if (current == controls.states.Length) { Finish(new RuntimeReport { candidates = runtimeResults.ToArray(), passed = runtimeResults.All(r => r.passed) }); return; }
            animator.Play(controls.states[current], 0, 0); started = Time.time;
        }
        catch (Exception error) { Finish(new RuntimeReport { error = error.ToString(), candidates = runtimeResults.ToArray() }); }
    }
}
}
