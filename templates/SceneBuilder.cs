using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace EndfieldChenTool.Generated
{
public static class SceneBuilder
{

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

    const string Base = "Assets/EndfieldChenTool";
    const string ModelPath = Base + "/Model/Chen.fbx";
    static string SourcePath = Base + "/Animations/Chen_Walk_Source.anim";
    static string ClipPath = Base + "/Animations/Chen_Walk.anim";
    static string ControllerPath = Base + "/Chen.controller";
    static string ScenePath = Base + "/Chen_Reextracted.unity";
    static bool uiWalk;
    static HumanMapping[] humanMapping;
    static void UseAclWalk()
    {
        uiWalk = false;
        SourcePath = Base + "/Animations/Chen_Walk_Source.anim";
        ClipPath = Base + "/Animations/Chen_Walk.anim";
        ControllerPath = Base + "/Chen.controller";
        ScenePath = Base + "/Chen_Reextracted.unity";
    }
    static void UseUiWalk()
    {
        uiWalk = true;
        SourcePath = Base + "/Animations/Chen_UI_Walk_Source.anim";
        ClipPath = Base + "/Animations/Chen_UI_Walk.anim";
        ControllerPath = Base + "/Chen_UI.controller";
        ScenePath = Base + "/Chen_UI_Walk.unity";
    }

    [Serializable] class AvatarFile { public AvatarSource avatar; }
    [Serializable] class AvatarSource { public HumanDescriptionSource m_HumanDescription; }
    [Serializable] class HumanDescriptionSource { public HumanMapping[] m_Human; }
    [Serializable] class HumanMapping { public string m_BoneName; public string m_HumanName; }
    [Serializable] class Audit
    {
        public string model, clip, scene, unityVersion;
        public bool avatarValid, avatarHuman, humanoidClip, passed;
        public bool savedPoseMatchesModel;
        public string faceCheckStatus;
        public int excludedSharedFaceCurves;
        public float sourceFaceMaximumKeyRange;
        public string[] preservedFacePaths;
        public int transformCount, meshCount, vertexCount, bindings, remappedCurves, missingCurves;
        public int repairedNonFiniteTangents;
        public int removedScenePositionCurves;
        public int normalizedSceneRootCurves;
        public int restoredConstantTangents;
        public Vector3 restSize;
        public float humanScale;
        public string[] missingPaths;
        public Sample[] samples;
        public string error;
    }
    [Serializable] class FaceCheck
    {
        public int boneCount, changedBones;
        public float maximumPositionDelta, maximumRotationDelta, maximumScaleDelta;
        public bool passed;
    }
    [Serializable] class Sample
    {
        public float time;
        public Vector3 size;
        public int nonFiniteVertices;
        public float edgeStretch99, maxEdgeStretch;
        public float hipsHead, hipsLeftFoot;
        public Quaternion leftThighRotation, rightThighRotation;
        public float normalizedTime;
        public FaceCheck face;
    }
    [Serializable] class RuntimeAudit
    {
        public bool passed;
        public float humanScale, legRotationChange;
        public string faceCheckStatus;
        public Sample[] samples;
        public string error;
    }
    static readonly List<Sample> runtimeSamples = new List<Sample>();
    static double runtimeStart;

    public static void CheckRuntimeBatch()
    {
        EditorSceneManager.OpenScene(ScenePath);
        SessionState.SetBool("EndfieldChenTool.RuntimeCheck", true);
        SessionState.SetBool("EndfieldChenTool.RuntimeUiWalk", uiWalk);
        SessionState.SetString("EndfieldChenTool.RuntimeReport", Environment.GetEnvironmentVariable("CHEN_REPORT_DIR"));
        EditorApplication.EnterPlaymode();
    }

    [InitializeOnLoadMethod]
    static void ResumeRuntimeCheck()
    {
        if (!SessionState.GetBool("EndfieldChenTool.RuntimeCheck", false)) return;
        if (SessionState.GetBool("EndfieldChenTool.RuntimeUiWalk", false)) UseUiWalk();
        runtimeStart = EditorApplication.timeSinceStartup;
        EditorApplication.update += RuntimeTick;
    }

    static void RuntimeTick()
    {
        if (!EditorApplication.isPlaying) return;
        string report = SessionState.GetString("EndfieldChenTool.RuntimeReport", "");
        try
        {
            if (EditorApplication.timeSinceStartup - runtimeStart > 90) throw new Exception("Runtime check timed out.");
            var animator = UnityEngine.Object.FindFirstObjectByType<PlaybackControls>().GetComponent<Animator>();
            if (runtimeSamples.Count < 3 && Time.time >= 0.4f + runtimeSamples.Count * 0.6f)
            {
                var renderers = animator.GetComponentsInChildren<SkinnedMeshRenderer>();
                var vertices = Bake(renderers);
                var runtimeSample = new Sample
                {
                    time = Time.time,
                    size = BoundsFor(renderers, vertices).size,
                    nonFiniteVertices = vertices.Sum(a => a.Count(v => !Finite(v))),
                    hipsHead = BoneDistance(animator, HumanBodyBones.Hips, HumanBodyBones.Head),
                    hipsLeftFoot = BoneDistance(animator, HumanBodyBones.Hips, HumanBodyBones.LeftFoot),
                    leftThighRotation = GetBone(animator, HumanBodyBones.LeftUpperLeg).localRotation,
                    rightThighRotation = GetBone(animator, HumanBodyBones.RightUpperLeg).localRotation,
                    normalizedTime = animator.GetCurrentAnimatorStateInfo(0).normalizedTime
                };
                if (uiWalk) runtimeSample.face = MeasureFace(animator.gameObject);
                runtimeSamples.Add(runtimeSample);
                Capture(Camera.main, Path.Combine(report, "chen_runtime_" + runtimeSamples.Count + ".png"), false);
                if (uiWalk) CaptureFace(Camera.main, animator.gameObject, Path.Combine(report, "chen_runtime_face_" + runtimeSamples.Count + ".png"), false);
            }
            if (runtimeSamples.Count < 3) return;
            float angle = Quaternion.Angle(runtimeSamples[0].leftThighRotation, runtimeSamples[2].leftThighRotation);
            var result = new RuntimeAudit
            {
                humanScale = animator.isHuman ? animator.humanScale : 1,
                legRotationChange = angle,
                samples = runtimeSamples.ToArray(),
                faceCheckStatus = uiWalk ? (runtimeSamples.All(s => s.face != null && s.face.passed) ? "passed" : "failed") : "not_requested",
                passed = angle > 2 && (!animator.isHuman || animator.humanScale > .9f && animator.humanScale < 1.1f) && runtimeSamples.All(s => s.nonFiniteVertices == 0 && s.size.y > 1 && s.size.y < 2.5f && s.size.magnitude < 4) && (!uiWalk || runtimeSamples.All(s => s.face != null && s.face.passed))
            };
            File.WriteAllText(Path.Combine(report, "runtime_verification.json"), JsonUtility.ToJson(result, true));
            SessionState.SetBool("EndfieldChenTool.RuntimeCheck", false);
            EditorApplication.update -= RuntimeTick;
            EditorApplication.Exit(result.passed ? 0 : 7);
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(report, "runtime_verification.json"), JsonUtility.ToJson(new RuntimeAudit { error = error.ToString() }, true));
            SessionState.SetBool("EndfieldChenTool.RuntimeCheck", false);
            EditorApplication.Exit(9);
        }
    }

    [MenuItem("Endfield Chen Tool/重建 ACL 预览")]
    public static void BuildInteractive() { UseAclWalk(); Build(false); }
    public static void BuildBatch() { UseAclWalk(); Build(true); }
    public static void BuildUiBatch() { UseUiWalk(); Build(true); }
    public static void CheckUiRuntimeBatch() { UseUiWalk(); CheckRuntimeBatch(); }

    static void Build(bool exit)
    {
        var audit = new Audit { model = ModelPath, clip = ClipPath, scene = ScenePath, unityVersion = Application.unityVersion };
        string report = Environment.GetEnvironmentVariable("CHEN_REPORT_DIR");
        if (String.IsNullOrEmpty(report)) report = Path.GetFullPath(Base + "/Reports");
        Directory.CreateDirectory(report);
        Directory.CreateDirectory(Base + "/Materials");
        try
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            var importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
            if (!importer) throw new Exception("Missing model: " + ModelPath);
            importer.importAnimation = false;
            importer.animationType = ModelImporterAnimationType.Generic;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.optimizeGameObjects = false;
            importer.preserveHierarchy = true;
            importer.globalScale = 100;
            importer.skinWeights = ModelImporterSkinWeights.Custom;
            importer.maxBonesPerVertex = 4;
            importer.minBoneWeight = 0.00001f;
            importer.SaveAndReimport();

            var avatarFile = JsonUtility.FromJson<AvatarFile>(File.ReadAllText(Base + "/Model/avatar_retarget.json"));
            humanMapping = avatarFile.avatar.m_HumanDescription.m_Human;
            var human = new HumanDescription();
            human.human = avatarFile.avatar.m_HumanDescription.m_Human.Select(m => new HumanBone
            {
                boneName = m.m_BoneName,
                humanName = m.m_HumanName,
                limit = new HumanLimit { useDefaultValues = true }
            }).ToArray();
            human.hasTranslationDoF = false;
            human.armStretch = 0;
            human.legStretch = 0;
            human.upperArmTwist = 1;
            human.lowerArmTwist = 0;
            human.upperLegTwist = 1;
            human.lowerLegTwist = 0;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = UnityEngine.Object.Instantiate(prefab);
            root.name = "Chen_Qianyu";
            root.transform.position = Vector3.zero;
            var transforms = root.GetComponentsInChildren<Transform>(true);
            // Keep the imported rest pose separate from sampled preview animation.
            var restPositions = transforms.Select(t => t.localPosition).ToArray();
            var restRotations = transforms.Select(t => t.localRotation).ToArray();
            var restScales = transforms.Select(t => t.localScale).ToArray();
            var restPaths = transforms.Select(t => AnimationUtility.CalculateTransformPath(t, root.transform)).ToArray();
            human.skeleton = transforms.Select(t => new SkeletonBone
            {
                name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale
            }).ToArray();
            var avatar = uiWalk ? AssetDatabase.LoadAllAssetsAtPath(ModelPath).OfType<Avatar>().FirstOrDefault() : AvatarBuilder.BuildHumanAvatar(root, human);
            avatar.name = "Chen_Reextracted_Avatar";
            audit.avatarValid = avatar && avatar.isValid;
            audit.avatarHuman = avatar && avatar.isHuman;
            if (!audit.avatarValid || !uiWalk && !audit.avatarHuman) throw new Exception("Generated Avatar is invalid.");
            if (!uiWalk)
            {
                var savedAvatar = AssetDatabase.LoadAssetAtPath<Avatar>(Base + "/ChenAvatar.asset");
                if (savedAvatar) { EditorUtility.CopySerialized(avatar, savedAvatar); UnityEngine.Object.DestroyImmediate(avatar); avatar = savedAvatar; }
                else AssetDatabase.CreateAsset(avatar, Base + "/ChenAvatar.asset");
            }
            var preservedFacePaths = uiWalk ? new HashSet<string>(FaceTransforms(root).Select(t => AnimationUtility.CalculateTransformPath(t, root.transform))) : new HashSet<string>();
            audit.preservedFacePaths = preservedFacePaths.OrderBy(p => p).ToArray();
            var paths = transforms.ToDictionary(t => AnimationUtility.CalculateTransformPath(t, root.transform), t => t);
            File.WriteAllLines(Path.Combine(report, "hierarchy.txt"), paths.Select(p => p.Key + " pos=" + p.Value.localPosition.ToString("F6") + " scale=" + p.Value.localScale.ToString("F6")));
            audit.transformCount = transforms.Length;
            var source = AssetDatabase.LoadAssetAtPath<AnimationClip>(SourcePath);
            if (!source) throw new Exception("Animation source did not import.");
            var clip = new AnimationClip { name = uiWalk ? "Chen_UI_Walk" : "Chen_Walk", frameRate = source.frameRate, legacy = false };
            var missing = new HashSet<string>();
            var sourceBindings = AnimationUtility.GetCurveBindings(source);
            var targetBindings = new List<EditorCurveBinding>();
            var targetCurves = new List<AnimationCurve>();
            foreach (var binding in sourceBindings)
            {
                if (uiWalk && binding.type == typeof(Transform) && String.IsNullOrEmpty(binding.path) && binding.propertyName.StartsWith("m_LocalPosition", StringComparison.Ordinal))
                { audit.removedScenePositionCurves++; continue; }
                var target = binding;
                if (binding.type == typeof(Transform) && !paths.ContainsKey(binding.path))
                {
                    var candidates = paths.Keys.Where(p => p.EndsWith("/" + binding.path, StringComparison.Ordinal) || binding.path.EndsWith("/" + p, StringComparison.Ordinal)).ToArray();
                    if (candidates.Length != 1) { missing.Add(binding.path); continue; }
                    target.path = candidates[0];
                    audit.remappedCurves++;
                }
                // Shared female locomotion contains another facial rest pose.
                // Keep Chen's face while retaining head and body motion.
                if (uiWalk && binding.type == typeof(Transform) && preservedFacePaths.Contains(target.path))
                {
                    var faceKeys = AnimationUtility.GetEditorCurve(source, binding).keys;
                    audit.excludedSharedFaceCurves++;
                    if (faceKeys.Length > 0) audit.sourceFaceMaximumKeyRange = Math.Max(audit.sourceFaceMaximumKeyRange, faceKeys.Max(k => k.value) - faceKeys.Min(k => k.value));
                    continue;
                }
                targetBindings.Add(target);
                var curve = AnimationUtility.GetEditorCurve(source, binding);
                var keys = curve.keys;
                var constantLeft = new List<int>();
                var constantRight = new List<int>();
                float sceneOffset = uiWalk && binding.type == typeof(Transform) && binding.path == "Root" && binding.propertyName.StartsWith("m_LocalPosition", StringComparison.Ordinal) ? curve.Evaluate(0) : 0;
                if (sceneOffset != 0) audit.normalizedSceneRootCurves++;
                for (int k = 0; k < keys.Length; k++)
                {
                    keys[k].value -= sceneOffset;
                    if (float.IsNaN(keys[k].value) || float.IsInfinity(keys[k].value)) throw new Exception("Nonfinite animation value: " + binding.path + " / " + binding.propertyName);
                    if (float.IsNaN(keys[k].inTangent) || float.IsInfinity(keys[k].inTangent)) { keys[k].inTangent = 0; audit.repairedNonFiniteTangents++; if (k > 0) constantLeft.Add(k); }
                    if (float.IsNaN(keys[k].outTangent) || float.IsInfinity(keys[k].outTangent)) { keys[k].outTangent = 0; audit.repairedNonFiniteTangents++; if (k < keys.Length - 1) constantRight.Add(k); }
                }
                curve.keys = keys;
                foreach (int k in constantLeft) { AnimationUtility.SetKeyBroken(curve, k, true); AnimationUtility.SetKeyLeftTangentMode(curve, k, AnimationUtility.TangentMode.Constant); audit.restoredConstantTangents++; }
                foreach (int k in constantRight) { AnimationUtility.SetKeyBroken(curve, k, true); AnimationUtility.SetKeyRightTangentMode(curve, k, AnimationUtility.TangentMode.Constant); audit.restoredConstantTangents++; }
                targetCurves.Add(curve);
            }
            AnimationUtility.SetEditorCurves(clip, targetBindings.ToArray(), targetCurves.ToArray());
            clip.EnsureQuaternionContinuity();
            audit.missingPaths = missing.OrderBy(s => s).ToArray();
            audit.missingCurves = missing.Count;
            audit.bindings = AnimationUtility.GetCurveBindings(clip).Length;
            audit.humanoidClip = clip.isHumanMotion;
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = true;
            settings.loopBlend = false;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            var existingClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipPath);
            if (existingClip) { EditorUtility.CopySerialized(clip, existingClip); UnityEngine.Object.DestroyImmediate(clip); clip = existingClip; }
            else AssetDatabase.CreateAsset(clip, ClipPath);
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath)) AssetDatabase.DeleteAsset(ControllerPath);
            var controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            var state = controller.layers[0].stateMachine.AddState("Walk");
            state.motion = clip;
            controller.layers[0].stateMachine.defaultState = state;
            var animator = root.GetComponent<Animator>();
            if (!animator) animator = root.AddComponent<Animator>();
            animator.avatar = avatar;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.runtimeAnimatorController = null;
            var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            audit.meshCount = renderers.Length;
            audit.vertexCount = renderers.Sum(r => r.sharedMesh.vertexCount);
            foreach (var renderer in renderers)
            {
                renderer.updateWhenOffscreen = true;
                renderer.quality = SkinQuality.Bone4;
                renderer.sharedMaterials = renderer.sharedMaterials.Select(m => MaterialFor(m)).ToArray();
            }
            var rest = Bake(renderers);
            var restBounds = BoundsFor(renderers, rest);
            audit.restSize = restBounds.size;
            audit.humanScale = animator.isHuman ? animator.humanScale : 1;
            var camera = MakeCamera(restBounds);
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = Color.gray;
            var sun = new GameObject("Key Light").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.2f;
            sun.transform.rotation = Quaternion.Euler(35, 145, 0);
            Capture(camera, Path.Combine(report, "chen_rest.png"));
            animator.runtimeAnimatorController = controller;
            animator.Rebind();
            animator.Update(0);
            var samples = new List<Sample>();
            var sampleTimes = Enumerable.Range(0, 17).Select(i => clip.length * i / 17f).ToList();
            if (uiWalk) sampleTimes.Add(Math.Min(20f / clip.frameRate, clip.length));
            for (int i = 0; i < sampleTimes.Count; i++)
            {
                float time = sampleTimes[i];
                animator.Play("Walk", 0, time / clip.length);
                animator.Update(0.00001f);
                var vertices = Bake(renderers);
                var stretch = new List<float>();
                int invalid = 0;
                for (int m = 0; m < vertices.Length; m++)
                {
                    invalid += vertices[m].Count(v => !Finite(v));
                    var triangles = renderers[m].sharedMesh.triangles;
                    for (int t = 0; t < triangles.Length; t += 3)
                        for (int k = 0; k < 3; k++)
                        {
                            int a = triangles[t + k], b = triangles[t + (k + 1) % 3];
                            float d = Vector3.Distance(rest[m][a], rest[m][b]);
                            if (d > 0.0001f) stretch.Add(Vector3.Distance(vertices[m][a], vertices[m][b]) / d);
                        }
                }
                stretch.Sort();
                var s = new Sample
                {
                    time = time, size = BoundsFor(renderers, vertices).size,
                    nonFiniteVertices = invalid,
                    edgeStretch99 = stretch.Count > 0 ? stretch[(int)(stretch.Count * 0.99f)] : 0,
                    maxEdgeStretch = stretch.Count > 0 ? stretch[stretch.Count - 1] : 0,
                    hipsHead = BoneDistance(animator, HumanBodyBones.Hips, HumanBodyBones.Head),
                    hipsLeftFoot = BoneDistance(animator, HumanBodyBones.Hips, HumanBodyBones.LeftFoot),
                    leftThighRotation = GetBone(animator, HumanBodyBones.LeftUpperLeg).localRotation,
                    rightThighRotation = GetBone(animator, HumanBodyBones.RightUpperLeg).localRotation,
                    normalizedTime = animator.GetCurrentAnimatorStateInfo(0).normalizedTime
                };
                if (uiWalk) s.face = MeasureFace(root);
                samples.Add(s);
                if (uiWalk && (i % 4 == 0 || i == sampleTimes.Count - 1)) CaptureFace(camera, root, Path.Combine(report, "chen_face_frame_" + Mathf.RoundToInt(time * clip.frameRate) + ".png"));
                if (i % 4 == 0) Capture(camera, Path.Combine(report, "chen_walk_" + i.ToString("D2") + ".png"));
            }
            audit.samples = samples.ToArray();
            audit.faceCheckStatus = uiWalk ? (samples.All(s => s.face != null && s.face.passed) ? "passed" : "failed") : "not_requested";
            audit.passed = audit.missingCurves == 0 && samples.All(s => s.nonFiniteVertices == 0 && s.size.magnitude < restBounds.size.magnitude * 3 && s.edgeStretch99 < 3) && (!uiWalk || samples.All(s => s.face != null && s.face.passed));
            // Sampling writes to real Transforms even outside Play mode. Restore
            // the model pose before serializing the scene; runtime starts at frame 0.
            animator.enabled = false;
            for (int i = 0; i < transforms.Length; i++)
            {
                transforms[i].localPosition = restPositions[i];
                transforms[i].localRotation = restRotations[i];
                transforms[i].localScale = restScales[i];
            }
            animator.enabled = true;
            root.AddComponent<PlaybackControls>().clipLabel = uiWalk ? "Female walk / neutral face / loop" : "Customized walk / Humanoid / loop";
            Capture(camera, Path.Combine(report, "chen_saved_rest.png"));
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            // Verify the serialized pose after reopening, not only in memory.
            EditorSceneManager.OpenScene(ScenePath);
            var reloaded = UnityEngine.Object.FindFirstObjectByType<PlaybackControls>().gameObject;
            var reloadedPaths = reloaded.GetComponentsInChildren<Transform>(true)
                .ToDictionary(t => AnimationUtility.CalculateTransformPath(t, reloaded.transform));
            audit.savedPoseMatchesModel = Enumerable.Range(0, restPaths.Length).All(i =>
                Vector3.Distance(reloadedPaths[restPaths[i]].localPosition, restPositions[i]) < .000001f &&
                Quaternion.Angle(reloadedPaths[restPaths[i]].localRotation, restRotations[i]) < .01f &&
                Vector3.Distance(reloadedPaths[restPaths[i]].localScale, restScales[i]) < .000001f);
            audit.passed &= audit.savedPoseMatchesModel;
            File.WriteAllText(Path.Combine(report, "verification.json"), JsonUtility.ToJson(audit, true));
            Debug.Log("[CHEN_REEXTRACTED] " + JsonUtility.ToJson(audit));
            if (exit) EditorApplication.Exit(audit.passed ? 0 : 7);
        }
        catch (Exception error)
        {
            audit.error = error.ToString();
            File.WriteAllText(Path.Combine(report, "verification.json"), JsonUtility.ToJson(audit, true));
            Debug.LogException(error);
            if (exit) EditorApplication.Exit(9);
        }
    }

    static Material MaterialFor(Material source)
    {
        string name = source ? source.name : "Chen";
        string path = Base + "/Materials/" + name + ".mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (!material) { material = new Material(Shader.Find("Standard")); AssetDatabase.CreateAsset(material, path); }
        if (name.IndexOf("eyeshadow", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("eyewhiteshadow", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            // Basic preview fallback for the game's translucent eye-shadow overlay.
            material.mainTexture = null;
            material.color = new Color(.08f, .06f, .045f, .16f);
            material.SetFloat("_Mode", 3);
            material.SetInt("_SrcBlend", (int)BlendMode.One);
            material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            material.SetInt("_ZWrite", 0);
            material.DisableKeyword("_ALPHATEST_ON"); material.DisableKeyword("_ALPHABLEND_ON");
            material.EnableKeyword("_ALPHAPREMULTIPLY_ON"); material.renderQueue = (int)RenderQueue.Transparent;
            material.SetFloat("_Glossiness", 0); material.SetFloat("_SpecularHighlights", 0);
            EditorUtility.SetDirty(material);
            return material;
        }
        string part = name.Contains("body") ? "body" : name.Contains("hair") ? "hair" : name.Contains("iris") ? "iris" : name.Contains("face") || name.Contains("brow") ? "face" : "cloth";
        material.mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(Base + "/Textures/T_actor_chen_" + part + "_01_D.png");
        material.SetFloat("_Glossiness", 0.05f);
        material.color = Color.white;
        EditorUtility.SetDirty(material);
        return material;
    }
    static Vector3[][] Bake(SkinnedMeshRenderer[] renderers)
    {
        return renderers.Select(r => { var mesh = new Mesh(); r.BakeMesh(mesh, false); var v = mesh.vertices; UnityEngine.Object.DestroyImmediate(mesh); return v; }).ToArray();
    }
    static Bounds BoundsFor(SkinnedMeshRenderer[] renderers, Vector3[][] vertices)
    {
        var bounds = new Bounds(); bool first = true;
        for (int i = 0; i < renderers.Length; i++) foreach (var v in vertices[i])
        { var p = renderers[i].transform.TransformPoint(v); if (first) { bounds = new Bounds(p, Vector3.zero); first = false; } else bounds.Encapsulate(p); }
        return bounds;
    }
    static bool Finite(Vector3 v) { return !float.IsNaN(v.x) && !float.IsInfinity(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.z); }
    static Transform GetBone(Animator animator, HumanBodyBones bone)
    {
        if (animator.isHuman) return animator.GetBoneTransform(bone);
        if (humanMapping == null) humanMapping = JsonUtility.FromJson<AvatarFile>(File.ReadAllText(Base + "/Model/avatar_retarget.json")).avatar.m_HumanDescription.m_Human;
        var mapping = humanMapping.First(m => m.m_HumanName == bone.ToString());
        return animator.GetComponentsInChildren<Transform>().First(t => t.name == mapping.m_BoneName);
    }
    static float BoneDistance(Animator a, HumanBodyBones x, HumanBodyBones y) { var t = GetBone(a, x); var u = GetBone(a, y); return t && u ? Vector3.Distance(t.position, u.position) : -1; }
    static Transform[] FaceTransforms(GameObject root)
    {
        var head = root.GetComponentsInChildren<Transform>(true).First(t => t.name == "Bip001_Head");
        var facialMeshes = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r =>
            r.sharedMesh && r.sharedMesh.vertexCount > 0 &&
            (r.name.Contains("face_") || r.name.Contains("iris_") || r.name.Contains("eyebrow_") || r.name.Contains("eyeshadow_")));
        var result = new HashSet<Transform>();
        foreach (var bone in facialMeshes.SelectMany(r => r.bones))
            for (var t = bone; t && t != head && t.IsChildOf(head); t = t.parent) result.Add(t);
        if (result.Count == 0) throw new Exception("No facial skin bones found; cannot verify face preservation.");
        return result.ToArray();
    }
    static FaceCheck MeasureFace(GameObject root)
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        var rest = model.GetComponentsInChildren<Transform>(true).ToDictionary(t => AnimationUtility.CalculateTransformPath(t, model.transform));
        var result = new FaceCheck();
        foreach (var t in FaceTransforms(root))
        {
            var original = rest[AnimationUtility.CalculateTransformPath(t, root.transform)];
            float p = Vector3.Distance(t.localPosition, original.localPosition);
            float q = Quaternion.Angle(t.localRotation, original.localRotation);
            float s = Vector3.Distance(t.localScale, original.localScale);
            result.boneCount++;
            result.maximumPositionDelta = Math.Max(result.maximumPositionDelta, p);
            result.maximumRotationDelta = Math.Max(result.maximumRotationDelta, q);
            result.maximumScaleDelta = Math.Max(result.maximumScaleDelta, s);
            if (p > .00001f || q > .05f || s > .00001f) result.changedBones++;
        }
        result.passed = result.boneCount > 0 && result.changedBones == 0;
        return result;
    }
    static void CaptureFace(Camera camera, GameObject root, string path, bool snapshot = true)
    {
        var skin = root.GetComponentsInChildren<SkinnedMeshRenderer>().First(r => r.name.Contains("face_") && r.sharedMesh.vertexCount > 0);
        var mesh = new Mesh(); skin.BakeMesh(mesh, false);
        var vertices = mesh.vertices.Select(v => skin.transform.TransformPoint(v)).ToArray();
        UnityEngine.Object.DestroyImmediate(mesh);
        var bounds = new Bounds(vertices[0], Vector3.zero); foreach (var v in vertices) bounds.Encapsulate(v);
        bool oldOrtho = camera.orthographic; float oldSize = camera.orthographicSize;
        var oldPosition = camera.transform.position; var oldRotation = camera.transform.rotation;
        try
        {
            camera.orthographic = true; camera.orthographicSize = .145f;
            camera.transform.position = bounds.center + new Vector3(0, 0, .65f); camera.transform.LookAt(bounds.center);
            Capture(camera, path, snapshot);
        }
        finally
        {
            camera.orthographic = oldOrtho; camera.orthographicSize = oldSize;
            camera.transform.SetPositionAndRotation(oldPosition, oldRotation);
        }
    }
    static Camera MakeCamera(Bounds bounds)
    {
        var camera = new GameObject("Main Camera").AddComponent<Camera>();
        camera.tag = "MainCamera";
        camera.fieldOfView = 32;
        camera.nearClipPlane = 0.01f;
        camera.farClipPlane = 100;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.12f, 0.14f, 0.18f);
        float distance = Math.Max(bounds.size.y, bounds.size.x) * 2.15f;
        camera.transform.position = bounds.center + new Vector3(distance * .13f, distance * .06f, distance);
        camera.transform.LookAt(bounds.center);
        camera.gameObject.AddComponent<AudioListener>();
        return camera;
    }
    static void Capture(Camera camera, string path, bool snapshot = true)
    {
        var snapshots = new List<GameObject>();
        var meshes = new List<Mesh>();
        var hidden = new List<SkinnedMeshRenderer>();
        if (snapshot)
        {
            foreach (var skin in UnityEngine.Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsSortMode.None).Where(r => r.enabled))
            {
                var mesh = new Mesh(); skin.BakeMesh(mesh, false); meshes.Add(mesh);
                var obj = new GameObject("Preview_" + skin.name);
                obj.transform.SetParent(skin.transform, false);
                obj.AddComponent<MeshFilter>().sharedMesh = mesh;
                obj.AddComponent<MeshRenderer>().sharedMaterials = skin.sharedMaterials;
                skin.enabled = false;
                hidden.Add(skin); snapshots.Add(obj);
            }
        }
        var target = new RenderTexture(800, 1000, 24);
        var image = new Texture2D(800, 1000, TextureFormat.RGB24, false);
        var previous = RenderTexture.active;
        camera.targetTexture = target;
        camera.Render();
        RenderTexture.active = target;
        image.ReadPixels(new Rect(0, 0, 800, 1000), 0, 0);
        image.Apply();
        File.WriteAllBytes(path, image.EncodeToPNG());
        camera.targetTexture = null;
        RenderTexture.active = previous;
        UnityEngine.Object.DestroyImmediate(image);
        UnityEngine.Object.DestroyImmediate(target);
        foreach (var obj in snapshots) UnityEngine.Object.DestroyImmediate(obj);
        foreach (var mesh in meshes) UnityEngine.Object.DestroyImmediate(mesh);
        foreach (var skin in hidden) skin.enabled = true;
    }
}

}
