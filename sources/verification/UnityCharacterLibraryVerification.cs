using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using EndfieldCharacterWorkbench;
using EndfieldCharacterWorkbench.Editor;

namespace EndfieldCharacterWorkbench.Verification
{
[InitializeOnLoad]
public static class UnityCharacterLibraryVerification
{
    [Serializable] class BootstrapData { public string[] sources; public string bonePath; public Vector3 rest; }
    [Serializable] class RunnerData { public string firstConfig = "", secondConfig = "", libraryRoot = ""; }
    [Serializable] class Report { public bool passed, actualPlay; public string error; public List<string> checks = new List<string>(); }
    static string Reports => Environment.GetEnvironmentVariable("ENDFIELD_LIBRARY_REPORTS");
    static string Output => Reports + "/unity-verification.json";
    static Report report;
    static BootstrapData data;
    static CharacterPreviewControls controls;
    static Transform bone;
    static int phase;
    static double started, phaseAt;
    static float pausedTime;
    static UnityCharacterLibraryVerification() { if (!string.IsNullOrEmpty(Reports)) EditorApplication.update += Tick; }
    static void Check(string name, bool value) { if (!value) throw new Exception(name); report.checks.Add(name); }
    public static void Bootstrap()
    {
        try
        {
            string path = Environment.GetEnvironmentVariable("ENDFIELD_LIBRARY_SOURCE_MODEL");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var selected = prefab.GetComponentsInChildren<Transform>(true).First(t => t.name.IndexOf("Pelvis", StringComparison.OrdinalIgnoreCase) >= 0);
            var data = new BootstrapData { bonePath = AnimationUtility.CalculateTransformPath(selected, prefab.transform), rest = selected.localPosition };
            string folder = "Assets/LibraryMotionSource_" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(folder);
            data.sources = new string[3];
            for (int i = 0; i < 3; i++)
            {
                var clip = new AnimationClip { name = "LibraryFixture" + i, frameRate = 30 };
                string property = i == 1 ? "m_LocalPosition.y" : "m_LocalPosition.x";
                float initial = i == 1 ? data.rest.y : data.rest.x;
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(i == 2 ? "MissingBone" : data.bonePath, typeof(Transform), property), AnimationCurve.Linear(0, initial, 1, initial + .1f));
                var settings = AnimationUtility.GetAnimationClipSettings(clip); settings.loopTime = true; AnimationUtility.SetAnimationClipSettings(clip, settings);
                string target = folder + "/Source" + i + ".anim"; AssetDatabase.CreateAsset(clip, target); AssetDatabase.SaveAssets();
                data.sources[i] = Path.Combine(Reports, "Source" + i + ".anim"); File.Copy(target, data.sources[i], true);
            }
            File.WriteAllText(Reports + "/bootstrap.json", JsonUtility.ToJson(data, true)); EditorApplication.Exit(0);
        }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }
    public static void Begin()
    {
        report = new Report();
        try
        {
            data = JsonUtility.FromJson<BootstrapData>(File.ReadAllText(Reports + "/bootstrap.json"));
            var runner = JsonUtility.FromJson<RunnerData>(File.ReadAllText(Reports + "/runner-verification.json"));
            var sharedConfig = JsonUtility.FromJson<CharacterAnimationAdapter.Config>(File.ReadAllText(runner.firstConfig));
            var unitsConfig = JsonUtility.FromJson<CharacterAnimationAdapter.Config>(File.ReadAllText("Assets/LibraryUnitSources/adaptation.json"));
            string sharedModel = runner.libraryRoot + "/" + sharedConfig.model;
            string originalMeta = File.ReadAllText(sharedModel + ".meta");
            var infer = typeof(CharacterAnimationAdapter).GetMethod("InferUnitScales", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var measurement = (CharacterAnimationAdapter.UnitInferenceReport)infer.Invoke(null,
                new object[] { unitsConfig, "Assets/LibraryUnitSources", sharedModel, AssetImporter.GetAtPath(sharedModel) as ModelImporter, true });
            File.WriteAllText(Reports + "/unit-reuse.json", JsonUtility.ToJson(measurement, true));
            Check("real source unit measurements reuse imported model without reimport", measurement.passed && measurement.measuredModelSegments >= 15
                && Mathf.Abs(measurement.modelImportScale - 100) < .0001f && Mathf.Abs(measurement.sharedPositionScale - 1) < .0001f && File.ReadAllText(sharedModel + ".meta") == originalMeta);
            string scene = CharacterAnimationAdapter.OpenExistingPreview(runner.secondConfig, false);
            Check("one fixed preview scene", scene == "Assets/EndfieldCharacterWorkbench/Preview/CharacterPreview.unity");
            string guid = AssetDatabase.AssetPathToGUID(scene);
            Check("two motions from separate tasks available", UnityEngine.Object.FindFirstObjectByType<CharacterPreviewControls>().clips.Length == 2);
            CharacterAnimationAdapter.OpenExistingPreview(runner.firstConfig, false);
            Check("opening another pack preserves preview scene GUID", AssetDatabase.AssetPathToGUID(scene) == guid);
            Check("one character instance after repeated opens", UnityEngine.Object.FindObjectsByType<Animator>(FindObjectsSortMode.None).Length == 1);
            byte[] savedScene = File.ReadAllBytes(scene);
            try
            {
                File.AppendAllText(scene, "\n# fixture edit\n"); bool refused = false;
                try { CharacterAnimationAdapter.OpenExistingPreview(runner.firstConfig, false); } catch (Exception ex) { refused = ex.Message.Contains("已被修改"); }
                Check("edited shared preview scene refused and retained", refused && File.ReadAllText(scene).Contains("# fixture edit"));
            }
            finally { File.WriteAllBytes(scene, savedScene); }
            var dirty = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); new GameObject("UnrelatedFixtureEdit"); EditorSceneManager.MarkSceneDirty(dirty);
            bool blocked = false;
            try { CharacterAnimationAdapter.OpenExistingPreview(runner.firstConfig, false); } catch (Exception ex) { blocked = ex.Message.Contains("未保存"); }
            Check("unsaved unrelated scene not replaced", blocked && GameObject.Find("UnrelatedFixtureEdit"));
            EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
            controls = UnityEngine.Object.FindFirstObjectByType<CharacterPreviewControls>();
            Check("save reload retains shared model and both motions", controls && controls.clips.Length == 2 && controls.characterLibraryRoot == runner.libraryRoot
                && controls.animator.GetComponentsInChildren<SkinnedMeshRenderer>(true).All(s => s.sharedMesh && s.sharedMaterials.All(m => m)));
            File.WriteAllText(Output, JsonUtility.ToJson(report, true));
            SessionState.SetBool("LibraryVerificationPlay", true); EditorApplication.isPlaying = true;
        }
        catch (Exception error) { Fail(error); }
    }
    static void Tick()
    {
        if (!SessionState.GetBool("LibraryVerificationPlay", false) || !EditorApplication.isPlaying) return;
        try
        {
            if (report == null) report = JsonUtility.FromJson<Report>(File.ReadAllText(Output));
            if (data == null) data = JsonUtility.FromJson<BootstrapData>(File.ReadAllText(Reports + "/bootstrap.json"));
            if (!controls)
            {
                controls = UnityEngine.Object.FindFirstObjectByType<CharacterPreviewControls>(); bone = controls.animator.transform.Find(data.bonePath);
                started = EditorApplication.timeSinceStartup;
            }
            if (EditorApplication.timeSinceStartup - started > 25) throw new Exception("Play timeout.");
            var names = Enumerable.Range(0, controls.clips.Length).Select(controls.DisplayName).ToArray();
            int first = Array.IndexOf(names, "LibraryFixture_first"), second = Array.IndexOf(names, "LibraryFixture_second");
            if (phase == 0 && controls.animator.GetCurrentAnimatorStateInfo(0).normalizedTime > .05f)
            {
                report.actualPlay = true;
                Check("readable motion names retained", first >= 0 && second >= 0);
                controls.SelectClip(first); controls.SetPaused(true); controls.SeekNormalized(.6f);
                Check("first motion moves body bone", Mathf.Abs(bone.localPosition.x - data.rest.x) > .01f);
                pausedTime = controls.animator.GetCurrentAnimatorStateInfo(0).normalizedTime; phaseAt = EditorApplication.timeSinceStartup; phase = 1;
            }
            else if (phase == 1 && EditorApplication.timeSinceStartup - phaseAt > .25)
            {
                Check("pause holds animation time", Mathf.Abs(controls.animator.GetCurrentAnimatorStateInfo(0).normalizedTime - pausedTime) < .01f);
                controls.SelectClip(second); controls.SeekNormalized(.6f);
                Check("new motion plays on same character and restores previous channels", Mathf.Abs(bone.localPosition.y - data.rest.y) > .01f && Mathf.Abs(bone.localPosition.x - data.rest.x) < .001f);
                controls.Restart(); Check("restart restores beginning", controls.animator.GetCurrentAnimatorStateInfo(0).normalizedTime < .01f);
                controls.SeekNormalized(.95f); controls.SetPaused(false); controls.SetSpeed(2); phaseAt = EditorApplication.timeSinceStartup; phase = 2;
            }
            else if (phase == 2 && EditorApplication.timeSinceStartup - phaseAt > .2)
            {
                Check("cross-loop playback", controls.animator.GetCurrentAnimatorStateInfo(0).normalizedTime > 1);
                Check("one character remains during playback", UnityEngine.Object.FindObjectsByType<Animator>(FindObjectsSortMode.None).Length == 1);
                int vertices = 0;
                foreach (var skin in controls.animator.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    var mesh = new Mesh(); skin.BakeMesh(mesh); vertices += mesh.vertexCount;
                    Check("finite skin vertices: " + skin.name, mesh.vertices.All(v => !(float.IsNaN(v.x) || float.IsInfinity(v.x) || float.IsNaN(v.y) || float.IsInfinity(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.z))));
                    UnityEngine.Object.DestroyImmediate(mesh);
                }
                Check("real preserved model vertices exercised", vertices > 1000);
                report.passed = true; SessionState.SetBool("LibraryVerificationPlay", false); File.WriteAllText(Output, JsonUtility.ToJson(report, true)); EditorApplication.Exit(0);
            }
        }
        catch (Exception error) { Fail(error); }
    }
    static void Fail(Exception error)
    {
        if (report == null) report = new Report(); report.error = error.ToString(); report.passed = false;
        File.WriteAllText(Output, JsonUtility.ToJson(report, true)); SessionState.SetBool("LibraryVerificationPlay", false); Debug.LogException(error); EditorApplication.Exit(1);
    }
}
}
