using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using EndfieldCharacterWorkbench.Editor;

namespace EndfieldCharacterWorkbench.Verification
{
[InitializeOnLoad]
public static class UnityModelAssemblyVerification
{
    [Serializable] class Report { public bool passed, actualPlay, sceneSaveReload; public string error; public List<string> checks = new List<string>(); }
    static string Output => Environment.GetEnvironmentVariable("ENDFIELD_MODEL_ASSEMBLY_CHECK");
    static string Folder;
    static Report report;
    static double started, pausedAt;
    static float pausedTime;
    static int phase;
    static CharacterPreviewControls controls;
    static GameObject prop;
    static MeshRenderer renderer;
    static UnityModelAssemblyVerification() { if (!string.IsNullOrEmpty(Output)) EditorApplication.update += Tick; }
    static void Check(string name, bool passed) { if (!passed) throw new Exception(name); report.checks.Add(name); }
    static CharacterAnimationAdapter.Rules Strict() => new CharacterAnimationAdapter.Rules { dropSceneTranslation = false };
    static AnimationClip Source(string path)
    {
        var clip = new AnimationClip { frameRate = 30 };
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(Transform), "m_LocalPosition.x"), AnimationCurve.Linear(0, 0, 1, .2f));
        var settings = AnimationUtility.GetAnimationClipSettings(clip); settings.loopTime = true;
        AnimationUtility.SetAnimationClipSettings(clip, settings);
        return clip;
    }
    static void Reject(string name, GameObject target, AnimationClip source)
    {
        try { var unexpected = CharacterAnimationAdapter.CreateDirectClip(target, source, Strict(), new CharacterAnimationAdapter.ClipReport()); UnityEngine.Object.DestroyImmediate(unexpected); }
        catch (Exception) { report.checks.Add(name); return; }
        throw new Exception("Expected rejection: " + name);
    }

    public static void Begin()
    {
        report = new Report();
        try
        {
            Folder = "Assets/ModelAssemblyFixture_" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Folder);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("Character");
            var hand = new GameObject("Hand"); hand.transform.SetParent(root.transform, false);
            prop = GameObject.CreatePrimitive(PrimitiveType.Cube); prop.name = "Prop"; prop.transform.SetParent(hand.transform, false);
            renderer = prop.GetComponent<MeshRenderer>();
            var assembly = new CharacterAnimationAdapter.ModelAssembly { schema = "endfield-model-assembly-v1", bodyKey = "fixture",
                evidence = "actual-transform-parent-and-child-references", sourceEvidenceSha256 = new string('A', 64), visualDependenciesResolved = true,
                nodes = new[] { new CharacterAnimationAdapter.AssemblyNode { key = "prop", path = "Hand/Prop", active = false, rendererType = "MeshRenderer", rendererEnabled = false } },
                lodGroups = new[] { new CharacterAnimationAdapter.LodGroupInput { path = "", size = .5f, referencePoint = new Vector3(0, .25f, 0),
                    enabled = true, fadeMode = 1, animateCrossFading = true, unitPolicy = "source-local-times-inferred-position-scale",
                    levels = new[] { new CharacterAnimationAdapter.LodLevel { height = .1f, fadeWidth = .5f,
                        renderers = new[] { new CharacterAnimationAdapter.LodRenderer { path = "Hand/Prop", rendererType = "MeshRenderer" } } } } } } };
            var config = new CharacterAnimationAdapter.Config { modelKey = "fixture", assembly = "assembly.json", modelImportScale = 100,
                rules = new CharacterAnimationAdapter.Rules { positionScale = 2 },
                inputs = new[] { new CharacterAnimationAdapter.InputFile { path = "assembly.json" } } };
            string assemblyPath = Folder + "/assembly.json";
            File.WriteAllText(assemblyPath, JsonUtility.ToJson(assembly));
            var apply = typeof(CharacterAnimationAdapter).GetMethod("ApplyAssemblyVisibility", BindingFlags.NonPublic | BindingFlags.Static);
            apply.Invoke(null, new object[] { root, config, Folder });
            Check("source assembly restores initial visibility", !prop.activeSelf && !renderer.enabled);
            var lod = root.GetComponent<LODGroup>();
            Check("LOD source references retained without double model scaling", lod && lod.GetLODs().Length == 1 && lod.GetLODs()[0].renderers.Single() == renderer
                && Mathf.Abs(lod.size - 1) < .000001f && Mathf.Abs(lod.localReferencePoint.y - .5f) < .000001f);
            assembly.visualDependenciesResolved = false; File.WriteAllText(assemblyPath, JsonUtility.ToJson(assembly));
            bool stopped = false;
            try { apply.Invoke(null, new object[] { root, config, Folder }); }
            catch (TargetInvocationException ex) { stopped = ex.InnerException.Message.Contains("模型组成包含尚未还原"); }
            Check("unresolved external effect rejected before playback", stopped);
            prop.SetActive(true); renderer.enabled = true;
            var skinObject = new GameObject("Skin"); skinObject.transform.SetParent(root.transform, false);
            var skin = skinObject.AddComponent<SkinnedMeshRenderer>();
            var mesh = new Mesh { name = "FiniteSkin" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right * .1f, Vector3.up * .1f }; mesh.triangles = new[] { 0, 1, 2 };
            mesh.boneWeights = Enumerable.Range(0, 3).Select(_ => new BoneWeight { boneIndex0 = 0, weight0 = 1 }).ToArray();
            mesh.bindposes = new[] { hand.transform.worldToLocalMatrix * skin.transform.localToWorldMatrix }; mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, Folder + "/Skin.asset");
            skin.sharedMesh = mesh; skin.bones = new[] { hand.transform }; skin.rootBone = hand.transform; skin.sharedMaterial = renderer.sharedMaterial;
            var source = Source("Hand");
            AnimationUtility.SetEditorCurve(source, EditorCurveBinding.FloatCurve("Hand/Prop", typeof(GameObject), "m_IsActive"),
                new AnimationCurve(new Keyframe(0, 0), new Keyframe(.25f, 1), new Keyframe(.75f, 0), new Keyframe(1, 0)));
            AnimationUtility.SetEditorCurve(source, EditorCurveBinding.FloatCurve("Hand/Prop", typeof(MeshRenderer), "m_Enabled"),
                new AnimationCurve(new Keyframe(0, 0), new Keyframe(.4f, 1), new Keyframe(.8f, 0), new Keyframe(1, 0)));
            var audit = new CharacterAnimationAdapter.ClipReport();
            var animated = CharacterAnimationAdapter.CreateDirectClip(root, source, Strict(), audit); animated.name = "WithProp";
            Check("visibility and body tracks retained without omission", audit.tracks.Count == 3 && audit.tracks.All(t => t.decision == "retain") && audit.retainedBindings == 3);
            Check("visibility uses stepped keys", AnimationUtility.GetEditorCurve(animated, EditorCurveBinding.FloatCurve("Hand/Prop", typeof(GameObject), "m_IsActive")).Evaluate(.2f) == 0);
            var plain = CharacterAnimationAdapter.CreateDirectClip(root, Source("Hand"), Strict(), new CharacterAnimationAdapter.ClipReport()); plain.name = "Plain";
            Check("legacy Transform-only clip remains supported", AnimationUtility.GetCurveBindings(plain).Length == 1);
            Reject("missing attachment path rejected", root, Source("Missing/UnknownHand"));
            var objectSource = Source("Hand");
            AnimationUtility.SetObjectReferenceCurve(objectSource, EditorCurveBinding.PPtrCurve("Hand/Prop", typeof(MeshFilter), "m_Mesh"),
                new[] { new ObjectReferenceKeyframe { time = 0, value = prop.GetComponent<MeshFilter>().sharedMesh } });
            Reject("unresolved object-reference track rejected in strict mode", root, objectSource);
            var invalid = Source("Hand");
            AnimationUtility.SetEditorCurve(invalid, EditorCurveBinding.FloatCurve("Hand/Prop", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, .5f));
            Reject("non-boolean visibility rejected", root, invalid);
            AssetDatabase.CreateAsset(animated, Folder + "/WithProp.anim"); AssetDatabase.CreateAsset(plain, Folder + "/Plain.anim");
            var controller = AnimatorController.CreateAnimatorControllerAtPath(Folder + "/Fixture.controller");
            var first = controller.layers[0].stateMachine.AddState("WithProp"); first.motion = animated; first.writeDefaultValues = true;
            var second = controller.layers[0].stateMachine.AddState("Plain"); second.motion = plain; second.writeDefaultValues = true;
            controller.layers[0].stateMachine.defaultState = first;
            var animator = root.AddComponent<Animator>();
            var avatar = AvatarBuilder.BuildGenericAvatar(root, ""); Check("fixture Generic Avatar valid", avatar.isValid);
            AssetDatabase.CreateAsset(avatar, Folder + "/FixtureAvatar.asset");
            animator.avatar = avatar; animator.runtimeAnimatorController = controller; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var ui = new GameObject("Controls").AddComponent<CharacterPreviewControls>();
            ui.animator = animator; ui.clips = new[] { animated, plain }; ui.statePaths = new[] { "Base Layer.WithProp", "Base Layer.Plain" }; ui.clipAvatars = new[] { avatar, avatar };
            PrefabUtility.SaveAsPrefabAsset(root, Folder + "/Fixture.prefab");
            Check("scene saved", EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(), Folder + "/Fixture.unity"));
            EditorSceneManager.OpenScene(Folder + "/Fixture.unity", OpenSceneMode.Single);
            var loaded = GameObject.Find("Character").transform.Find("Hand/Prop");
            Check("save reload preserves prop and material", loaded && loaded.GetComponent<MeshRenderer>().sharedMaterial);
            report.sceneSaveReload = true;
            File.WriteAllText(Output, JsonUtility.ToJson(report, true));
            SessionState.SetBool("ModelAssemblyValidationStarted", true);
            EditorApplication.isPlaying = true;
        }
        catch (Exception ex) { Fail(ex); }
    }

    static void Tick()
    {
        if (!SessionState.GetBool("ModelAssemblyValidationStarted", false) || !EditorApplication.isPlaying) return;
        try
        {
            if (report == null) report = JsonUtility.FromJson<Report>(File.ReadAllText(Output));
            if (!controls)
            {
                controls = UnityEngine.Object.FindFirstObjectByType<CharacterPreviewControls>();
                prop = controls.animator.transform.Find("Hand/Prop").gameObject; renderer = prop.GetComponent<MeshRenderer>();
                started = EditorApplication.timeSinceStartup;
            }
            if (EditorApplication.timeSinceStartup - started > 25) throw new Exception("Actual Play checks timed out.");
            if (phase == 0 && controls.animator.GetCurrentAnimatorStateInfo(0).normalizedTime > .05f)
            {
                report.actualPlay = true;
                controls.SeekNormalized(.6f); Check("seek activates prop and renderer", prop.activeSelf && renderer.enabled);
                Check("body motion advances", controls.animator.transform.Find("Hand").localPosition.x > .05f);
                controls.SeekNormalized(.9f); Check("seek deactivates prop and renderer", !prop.activeSelf && !renderer.enabled);
                controls.SeekNormalized(.6f); controls.SetPaused(true); pausedTime = controls.animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
                pausedAt = EditorApplication.timeSinceStartup; phase = 1;
            }
            else if (phase == 1 && EditorApplication.timeSinceStartup - pausedAt > .2)
            {
                Check("pause freezes animation", Mathf.Abs(controls.animator.GetCurrentAnimatorStateInfo(0).normalizedTime - pausedTime) < .00001f);
                controls.Restart(); Check("restart restores initial hidden state", !prop.activeSelf && !renderer.enabled);
                controls.SelectClip(1); Check("switch clears previous prop state", prop.activeSelf && renderer.enabled);
                controls.SelectClip(0); controls.SeekNormalized(.95f); controls.SetSpeed(2); controls.SetPaused(false);
                pausedAt = EditorApplication.timeSinceStartup; phase = 2;
            }
            else if (phase == 2 && EditorApplication.timeSinceStartup - pausedAt > .2)
            {
                Check("actual runtime crosses loop boundary", controls.animator.GetCurrentAnimatorStateInfo(0).normalizedTime > 1);
                Check("no duplicate prop instance", controls.animator.GetComponentsInChildren<Transform>(true).Count(t => t.name == "Prop") == 1);
                foreach (var skin in controls.animator.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    var baked = new Mesh(); skin.BakeMesh(baked);
                    Check("finite skinned vertices", baked.vertexCount > 0 && baked.vertices.All(v => !float.IsNaN(v.x) && !float.IsInfinity(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.z)));
                    UnityEngine.Object.DestroyImmediate(baked);
                }
                report.passed = true; SessionState.SetBool("ModelAssemblyValidationStarted", false);
                File.WriteAllText(Output, JsonUtility.ToJson(report, true)); EditorApplication.Exit(0);
            }
        }
        catch (Exception ex) { Fail(ex); }
    }
    static void Fail(Exception ex)
    {
        if (report == null) report = new Report(); report.error = ex.ToString(); report.passed = false;
        File.WriteAllText(Output, JsonUtility.ToJson(report, true)); Debug.LogException(ex);
        SessionState.SetBool("ModelAssemblyValidationStarted", false); EditorApplication.Exit(1);
    }
}
}
