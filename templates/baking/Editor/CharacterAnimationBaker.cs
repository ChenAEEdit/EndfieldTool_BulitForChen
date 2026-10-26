#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace EndfieldCharacterWorkbench.Baking
{
    // Standalone: copy this file into Assets/Editor. No Recorder, Timeline or workbench assembly required.
    public sealed class CharacterAnimationBaker : EditorWindow
    {
        [SerializeField] GameObject character;
        [SerializeField] Animator animator;
        [SerializeField] AnimationClip clip;
        [SerializeField] Avatar avatar;
        [SerializeField] int framesPerSecond = 60;
        [SerializeField] bool applyRootMotion;
        [SerializeField] bool blendShapes = true;
        [SerializeField] string outputFolder = "Assets/BakedAnimations";
        [SerializeField] AnimationClip[] previewClips = new AnimationClip[0];
        [SerializeField] Avatar[] previewAvatars = new Avatar[0];
        [SerializeField] string[] previewNames = new string[0];
        [SerializeField] int selected;
        Vector2 scroll;
        string status = "选择包含网格和骨架的角色，以及一条可以在该角色上播放的动画。";

        [MenuItem("Tools/Endfield/动画烘焙与 FBX 导出")]
        public static void Open()
        {
            var window = GetWindow<CharacterAnimationBaker>("动画烘焙与导出");
            window.minSize = new Vector2(480, 500);
        }

        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("角色动画 → 骨骼曲线 → FBX", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("在编辑模式操作。脚本在临时场景的副本上采样，不保存或修改当前场景。", MessageType.Info);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling))
            {
                if (GUILayout.Button("使用 Hierarchy 中选中的角色")) Guard(UseSelection);
                character = (GameObject)EditorGUILayout.ObjectField("角色总根节点", character, typeof(GameObject), true);
                animator = (Animator)EditorGUILayout.ObjectField("源 Animator", animator, typeof(Animator), true);
                if (GUILayout.Button("读取现有工具预览场景的动画列表")) Guard(ReadPreview);
                if (previewClips.Length > 0)
                {
                    int next = EditorGUILayout.Popup("预览中的动画", selected, previewNames);
                    if (next != selected) SelectPreview(next);
                }
                clip = (AnimationClip)EditorGUILayout.ObjectField("输入动画 / 已录制 .anim", clip, typeof(AnimationClip), false);
                avatar = (Avatar)EditorGUILayout.ObjectField("配套 Avatar", avatar, typeof(Avatar), false);
                if (clip) EditorGUILayout.LabelField("输入", (clip.isHumanMotion ? "Humanoid" : "Transform / Generic") + " · " + clip.length.ToString("0.###") + " 秒");
                framesPerSecond = EditorGUILayout.IntSlider("采样帧率", framesPerSecond, 15, 120);
                applyRootMotion = EditorGUILayout.Toggle(new GUIContent("求值 Apply Root Motion", "应与正确播放时源 Animator 的设置一致。根节点运动也记录为 Transform 曲线。"), applyRootMotion);
                blendShapes = EditorGUILayout.Toggle("记录 BlendShape 权重", blendShapes);
                outputFolder = EditorGUILayout.TextField("输出父目录", outputFolder);
                EditorGUILayout.Space();
                if (GUILayout.Button("烘焙为 .anim", GUILayout.Height(32))) Run(false);
                if (GUILayout.Button("烘焙并导出模型 + 动画 FBX", GUILayout.Height(32))) Run(true);
            }
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(status, MessageType.None);
            EditorGUILayout.HelpBox("输入可以是原始适配动作，也可以是 Recorder 已录制的 .anim。导出 FBX 需要 FBX Exporter 5.x。每次写入新目录。\n本版求值单条动画；不执行原场景脚本、控制器混合、布料物理或脚本 IK。已有变形也会被烘焙。", MessageType.Info);
            EditorGUILayout.EndScrollView();
        }

        void UseSelection()
        {
            character = Selection.activeGameObject;
            if (!character) throw new Exception("请在 Hierarchy 中选择角色总根节点。");
            var candidates = character.GetComponentsInChildren<Animator>(true);
            if (candidates.Length != 1) throw new Exception("所选层级中需要恰好一个 Animator；请调整角色总根节点。");
            animator = candidates[0]; avatar = animator.avatar; applyRootMotion = animator.applyRootMotion;
            status = "已选择 " + character.name + "。可以读取预览动画列表，也可以手动拖入动画。";
        }

        void ReadPreview()
        {
            if (!character) UseSelection();
            if (!character.scene.IsValid()) throw new Exception("请选择已打开场景中的角色实例。");
            var matches = new List<MonoBehaviour>();
            foreach (var root in character.scene.GetRootGameObjects())
                foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true))
                    if (component && component.GetType().FullName == "EndfieldCharacterWorkbench.CharacterPreviewControls")
                    {
                        var target = Field<Animator>(component, "animator");
                        if (target && (target.transform == character.transform || target.transform.IsChildOf(character.transform))) matches.Add(component);
                    }
            if (matches.Count != 1) throw new Exception("没有找到唯一的配套预览控制器。可手动指定动画与 Avatar。");
            animator = Field<Animator>(matches[0], "animator");
            previewClips = Field<AnimationClip[]>(matches[0], "clips") ?? new AnimationClip[0];
            previewAvatars = Field<Avatar[]>(matches[0], "clipAvatars") ?? new Avatar[0];
            var names = Field<string[]>(matches[0], "displayNames") ?? new string[0];
            if (previewClips.Length == 0) throw new Exception("预览列表为空。");
            previewNames = previewClips.Select((item, i) => i < names.Length && !string.IsNullOrEmpty(names[i]) ? names[i] : item ? item.name : "缺失动画").ToArray();
            SelectPreview(0); applyRootMotion = animator.applyRootMotion;
            status = "已读取 " + previewClips.Length + " 条动画；切换列表时会同时选择配套 Avatar。";
        }

        static T Field<T>(object target, string name) where T : class
        { return target.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target) as T; }

        void SelectPreview(int index)
        {
            selected = Mathf.Clamp(index, 0, previewClips.Length - 1); clip = previewClips[selected];
            avatar = selected < previewAvatars.Length && previewAvatars[selected] ? previewAvatars[selected] : animator ? animator.avatar : null;
        }

        void Guard(Action action)
        { try { action(); } catch (Exception e) { status = e.Message; Debug.LogException(e); } }

        void Run(bool fbx)
        {
            Guard(() =>
            {
                var result = Bake(new Request { character = character, animator = animator, clip = clip, avatar = avatar,
                    framesPerSecond = framesPerSecond, applyRootMotion = applyRootMotion, blendShapes = blendShapes,
                    outputFolder = outputFolder, exportFbx = fbx });
                status = "完成：" + result.folder + "\n" + result.frames + " 个采样点，" + result.transforms + " 个 Transform。UE 导入尚需检查。";
                EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<AnimationClip>(result.animation));
            });
        }

        [Serializable] public sealed class Request
        {
            public GameObject character;
            public Animator animator;
            public AnimationClip clip;
            public Avatar avatar;
            public int framesPerSecond = 60;
            public bool applyRootMotion, blendShapes = true, exportFbx;
            public string outputFolder = "Assets/BakedAnimations";
        }

        [Serializable] public sealed class Result
        {
            public string schema = "endfield-animation-bake-v1", status = "started", folder, animation, modelFbx, animationFbx;
            public string sourceClip, sourceClipName, sourceCharacter, sourceAvatar, unityVersion, error = "";
            public bool humanoidInput, applyRootMotion, blendShapes, ueVerified;
            public int framesPerSecond, fbxFramesPerSecond, frames, transforms, skinBones, blendShapeChannels, movingTransforms;
            public float duration, bindPoseError;
            public string[] transformPaths;
            public string[] limitations = { "single-clip evaluation; no controller layers or transitions", "no scene scripts, cloth simulation, script IK or game shaders", "existing source deformations remain", "FBX export is not proof of Unreal compatibility" };
        }

        sealed class Channel
        {
            public Transform target;
            public string path;
            public List<Keyframe>[] keys = Enumerable.Range(0, 10).Select(_ => new List<Keyframe>()).ToArray();
            Quaternion previous;
            Vector3 firstPosition, firstScale;
            Quaternion firstRotation;
            public bool moving;
            public void Sample(float time, int frame)
            {
                var p = target.localPosition; var q = target.localRotation.normalized; var s = target.localScale;
                if (frame > 0 && Quaternion.Dot(previous, q) < 0) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                var values = new[] { p.x, p.y, p.z, q.x, q.y, q.z, q.w, s.x, s.y, s.z };
                for (int i = 0; i < values.Length; i++)
                {
                    if (!Finite(values[i])) throw new Exception("采样出现非有限值：" + path + "，时间 " + time);
                    keys[i].Add(new Keyframe(time, values[i]));
                }
                if (frame == 0) { firstPosition = p; firstRotation = q; firstScale = s; }
                else if ((p - firstPosition).sqrMagnitude > 1e-10f || Quaternion.Angle(q, firstRotation) > .001f || (s - firstScale).sqrMagnitude > 1e-10f) moving = true;
                previous = q;
            }
        }

        sealed class ShapeChannel
        {
            public SkinnedMeshRenderer renderer;
            public int index;
            public string path, property;
            public List<Keyframe> keys = new List<Keyframe>();
        }

        struct Pose
        {
            public Transform target;
            public Vector3 position, scale;
            public Quaternion rotation;
            public void Restore() { target.localPosition = position; target.localRotation = rotation; target.localScale = scale; }
        }

        public static Result Bake(Request request)
        {
            Validate(request);
            // Resolve optional dependency before creating any output.
            var exporter = request.exportFbx ? new FbxBridge() : null;
            var result = new Result { sourceClip = AssetDatabase.GetAssetPath(request.clip), sourceClipName = request.clip.name, sourceCharacter = request.character.name,
                sourceAvatar = request.avatar ? request.avatar.name : "", unityVersion = Application.unityVersion,
                humanoidInput = request.clip.isHumanMotion, applyRootMotion = request.applyRootMotion, blendShapes = request.blendShapes,
                framesPerSecond = request.framesPerSecond, duration = request.clip.length };
            Scene scene = default(Scene);
            PlayableGraph graph = default(PlayableGraph);
            Avatar generatedAvatar = null;
            AnimationClip baked = null, exportClip = null;
            bool saved = false;
            try
            {
                scene = EditorSceneManager.NewPreviewScene();
                var host = EditorUtility.CreateGameObjectWithHideFlags("Animation Bake Temporary Host", HideFlags.HideAndDontSave);
                SceneManager.MoveGameObjectToScene(host, scene); host.SetActive(false);
                var source = PrefabUtility.GetCorrespondingObjectFromSource(request.character);
                // A fresh prefab avoids carrying edit-mode animation preview poses into missing channels.
                var copy = Object.Instantiate(source ? source : request.character, host.transform, false);
                copy.name = request.character.name;
                copy.transform.localPosition = Vector3.zero; copy.transform.localRotation = Quaternion.identity;
                copy.transform.localScale = request.character.transform.lossyScale;
                foreach (var behaviour in copy.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                foreach (var body in copy.GetComponentsInChildren<Rigidbody>(true)) { body.isKinematic = true; body.detectCollisions = false; }
                foreach (var collider in copy.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
                var animatorPath = AnimationUtility.CalculateTransformPath(request.animator.transform, request.character.transform);
                var animatorTransform = Find(copy.transform, animatorPath);
                var targetAnimator = animatorTransform ? animatorTransform.GetComponent<Animator>() : null;
                if (!targetAnimator) throw new Exception("角色副本中找不到源 Animator 的对应节点。");
                var transforms = copy.GetComponentsInChildren<Transform>(true);
                var paths = transforms.Select(t => AnimationUtility.CalculateTransformPath(t, copy.transform)).ToArray();
                ValidatePaths(transforms, paths);
                var skins = copy.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(s => s.sharedMesh && s.enabled && ActiveWithin(s.transform, copy.transform)).ToArray();
                if (skins.Length == 0) throw new Exception("角色层级中没有启用的蒙皮网格。请选中包含网格和骨架的总根节点。");
                var bones = ValidateSkins(copy.transform, skins);
                result.skinBones = bones.Length;
                RestoreBindPose(skins, out result.bindPoseError);
                var rest = transforms.Select(t => new Pose { target = t, position = t.localPosition, rotation = t.localRotation, scale = t.localScale }).ToArray();
                var shapeRest = skins.Select(s => Enumerable.Range(0, s.sharedMesh.blendShapeCount).Select(s.GetBlendShapeWeight).ToArray()).ToArray();
                ValidateBindings(request.clip, animatorTransform);
                targetAnimator.runtimeAnimatorController = null;
                var chosenAvatar = request.avatar ? request.avatar : request.animator.avatar;
                if (!request.clip.isHumanMotion && (!chosenAvatar || chosenAvatar.isHuman))
                {
                    generatedAvatar = AvatarBuilder.BuildGenericAvatar(targetAnimator.gameObject, "");
                    generatedAvatar.name = copy.name + "_BakedGenericAvatar";
                    chosenAvatar = generatedAvatar;
                }
                if (!chosenAvatar || !chosenAvatar.isValid || request.clip.isHumanMotion && !chosenAvatar.isHuman)
                    throw new Exception("动画缺少有效的配套 Avatar；Humanoid 必须使用该角色的 Human Avatar。");
                result.sourceAvatar = chosenAvatar.name;
                targetAnimator.avatar = chosenAvatar;
                targetAnimator.applyRootMotion = request.applyRootMotion;
                targetAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                targetAnimator.fireEvents = false; targetAnimator.enabled = true;
                copy.SetActive(true); host.SetActive(true);
                targetAnimator.Rebind();
                foreach (var pose in rest) pose.Restore();
                var channels = transforms.Select((t, i) => new Channel { target = t, path = paths[i] }).ToArray();
                var shapes = new List<ShapeChannel>();
                if (request.blendShapes)
                    foreach (var skin in skins)
                        for (int i = 0; i < skin.sharedMesh.blendShapeCount; i++)
                            shapes.Add(new ShapeChannel { renderer = skin, index = i, path = AnimationUtility.CalculateTransformPath(skin.transform, copy.transform), property = "blendShape." + skin.sharedMesh.GetBlendShapeName(i) });
                int intervals = Mathf.CeilToInt(request.clip.length * request.framesPerSecond);
                if ((long)(intervals + 1) * (channels.Length * 10L + shapes.Count) > 6000000)
                    throw new Exception("本次超过 600 万个采样键，请降低帧率或缩短输入动画。");
                graph = PlayableGraph.Create("Endfield Offline Animation Bake");
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimationClipPlayable.Create(graph, request.clip);
                playable.SetApplyFootIK(false); playable.SetApplyPlayableIK(false);
                var output = AnimationPlayableOutput.Create(graph, "Character", targetAnimator);
                output.SetSourcePlayable(playable);
                graph.Play(); playable.SetTime(0); graph.Evaluate(0);
                float previousTime = 0;
                for (int frame = 0; frame <= intervals; frame++)
                {
                    float time = Mathf.Min(frame / (float)request.framesPerSecond, request.clip.length);
                    if (!Application.isBatchMode && frame % 15 == 0 && EditorUtility.DisplayCancelableProgressBar("烘焙角色动画", request.clip.name + " · " + time.ToString("0.00") + " 秒", frame / (float)intervals))
                        throw new OperationCanceledException("已取消；原角色和场景未修改。");
                    if (frame > 0) graph.Evaluate(time - previousTime);
                    foreach (var channel in channels) channel.Sample(time, frame);
                    foreach (var shape in shapes)
                    {
                        float weight = shape.renderer.GetBlendShapeWeight(shape.index);
                        if (!Finite(weight)) throw new Exception("BlendShape 权重不是有限数：" + shape.property);
                        shape.keys.Add(new Keyframe(time, weight));
                    }
                    previousTime = time;
                }
                graph.Destroy();
                baked = new AnimationClip { name = SafeName(request.clip.name) + "_Baked", frameRate = request.framesPerSecond };
                string[] properties = { "m_LocalPosition.x", "m_LocalPosition.y", "m_LocalPosition.z", "m_LocalRotation.x", "m_LocalRotation.y", "m_LocalRotation.z", "m_LocalRotation.w", "m_LocalScale.x", "m_LocalScale.y", "m_LocalScale.z" };
                foreach (var channel in channels)
                    for (int i = 0; i < properties.Length; i++) PutCurve(baked, channel.path, typeof(Transform), properties[i], channel.keys[i]);
                foreach (var shape in shapes) PutCurve(baked, shape.path, typeof(SkinnedMeshRenderer), shape.property, shape.keys);
                baked.EnsureQuaternionContinuity();
                var settings = AnimationUtility.GetAnimationClipSettings(baked); settings.loopTime = request.clip.isLooping;
                AnimationUtility.SetAnimationClipSettings(baked, settings);
                result.frames = intervals + 1; result.transforms = channels.Length; result.blendShapeChannels = shapes.Count;
                result.movingTransforms = channels.Count(c => c.moving); result.transformPaths = paths;
                result.folder = NewFolder(request.outputFolder, SafeName(request.character.name) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6));
                result.animation = result.folder + "/" + baked.name + ".anim";
                AssetDatabase.CreateAsset(baked, result.animation); saved = true; AssetDatabase.SaveAssets();
                if (baked.isHumanMotion || AnimationUtility.GetCurveBindings(baked).Length != channels.Length * 10 + shapes.Count)
                    throw new Exception("生成动画的曲线类型或数量与采样记录不一致。");
                if (exporter != null)
                {
                    foreach (var pose in rest) pose.Restore();
                    for (int i = 0; i < skins.Length; i++) for (int j = 0; j < shapeRest[i].Length; j++) skins[i].SetBlendShapeWeight(j, shapeRest[i][j]);
                    foreach (var item in copy.GetComponentsInChildren<Animator>(true)) Object.DestroyImmediate(item);
                    foreach (var item in copy.GetComponentsInChildren<Animation>(true)) Object.DestroyImmediate(item);
                    // FBX has no Unity activeSelf state. Hidden LOD meshes would otherwise reappear together on import.
                    foreach (var renderer in copy.GetComponentsInChildren<Renderer>(true))
                        if (!renderer.enabled || !ActiveWithin(renderer.transform, copy.transform))
                        {
                            var filter = renderer.GetComponent<MeshFilter>();
                            if (filter) Object.DestroyImmediate(filter);
                            Object.DestroyImmediate(renderer);
                        }
                    result.modelFbx = result.folder + "/" + SafeName(copy.name) + "_Model.fbx";
                    exporter.Export(result.modelFbx, copy, false);
                    exportClip = Object.Instantiate(baked); exportClip.legacy = true;
                    // FBX Exporter samples rotations on its own uniform grid. Include a fractional final bake frame exactly.
                    result.fbxFramesPerSecond = ExportRate(request.framesPerSecond, baked.length);
                    exportClip.frameRate = result.fbxFramesPerSecond;
                    var legacy = copy.AddComponent<Animation>(); legacy.playAutomatically = false;
                    legacy.AddClip(exportClip, exportClip.name); legacy.clip = exportClip;
                    result.animationFbx = result.folder + "/" + SafeName(request.clip.name) + ".fbx";
                    exporter.Export(result.animationFbx, copy, true);
                }
                result.status = "completed";
                WriteReport(result); AssetDatabase.Refresh();
                Debug.Log("动画烘焙完成：" + result.folder + "。未验证 UE 导入。", request.character);
                return result;
            }
            catch (Exception e)
            {
                result.status = e is OperationCanceledException ? "cancelled" : "failed"; result.error = e.Message;
                if (!string.IsNullOrEmpty(result.folder)) { WriteReport(result); AssetDatabase.Refresh(); }
                throw;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                if (graph.IsValid()) graph.Destroy();
                if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
                if (exportClip) Object.DestroyImmediate(exportClip);
                if (generatedAvatar) Object.DestroyImmediate(generatedAvatar);
                if (baked && !saved) Object.DestroyImmediate(baked);
            }
        }

        static void Validate(Request request)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new Exception("请退出 Play 模式后烘焙。");
            if (AnimationMode.InAnimationMode()) throw new Exception("请先关闭 Animation / Timeline 的动画预览，再执行烘焙。");
            if (request == null || !request.character || !request.animator || !request.clip) throw new Exception("请填写角色总根节点、源 Animator 和输入动画。");
            if (request.animator.transform != request.character.transform && !request.animator.transform.IsChildOf(request.character.transform)) throw new Exception("源 Animator 必须位于角色总根节点内部。");
            if (request.character.GetComponentsInChildren<Animator>(true).Length != 1) throw new Exception("角色层级必须恰好包含一个 Animator。");
            if (!Finite(request.clip.length) || request.clip.length <= 0 || request.framesPerSecond < 15 || request.framesPerSecond > 120) throw new Exception("动画时长或采样帧率无效。");
            if (AnimationUtility.GetObjectReferenceCurveBindings(request.clip).Length > 0) throw new Exception("输入包含对象引用动画；本版仅导出 Transform 与 BlendShape，请先分离这类轨道。");
            OutputPath(request.outputFolder);
        }

        static void ValidatePaths(Transform[] transforms, string[] paths)
        {
            if (paths.Distinct(StringComparer.Ordinal).Count() != paths.Length) throw new Exception("角色存在重复骨骼路径，无法唯一绑定动画。");
            foreach (var transform in transforms)
                if (transform.name.Contains("/")) throw new Exception("节点名称包含 /，请先修正：" + transform.name);
        }

        static Transform[] ValidateSkins(Transform root, SkinnedMeshRenderer[] skins)
        {
            var bones = new HashSet<Transform>();
            foreach (var skin in skins)
            {
                if (skin.bones.Length == 0 || skin.sharedMesh.bindposes.Length != skin.bones.Length) throw new Exception("蒙皮骨骼与 bindposes 数量不一致：" + skin.name);
                foreach (var bone in skin.bones)
                {
                    if (!bone || bone != root && !bone.IsChildOf(root)) throw new Exception("蒙皮引用缺失或骨骼在所选角色之外：" + skin.name);
                    bones.Add(bone);
                }
            }
            return bones.ToArray();
        }

        static void ValidateBindings(AnimationClip input, Transform animatorRoot)
        {
            var bindings = AnimationUtility.GetCurveBindings(input);
            if (bindings.Length == 0) throw new Exception("输入动画没有可采样的曲线。");
            var unsupported = bindings.Where(b => b.type != typeof(Transform) && b.type != typeof(SkinnedMeshRenderer) && b.type != typeof(Animator)).Select(b => b.type.Name + ":" + b.propertyName).Distinct().Take(8).ToArray();
            if (unsupported.Length > 0) throw new Exception("输入包含本版不支持的组件曲线：\n" + string.Join("\n", unsupported));
            var missing = bindings
                .Where(b => b.type == typeof(Transform) || b.type == typeof(SkinnedMeshRenderer))
                .Where(b => !Find(animatorRoot, b.path)).Select(b => b.path).Distinct().Take(8).ToArray();
            if (missing.Length > 0) throw new Exception("输入动画路径与源 Animator 层级不匹配：\n" + string.Join("\n", missing));
            foreach (var binding in bindings.Where(b => b.type == typeof(SkinnedMeshRenderer)))
            {
                var skin = Find(animatorRoot, binding.path).GetComponent<SkinnedMeshRenderer>();
                if (!skin || !skin.sharedMesh || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal)
                    || skin.sharedMesh.GetBlendShapeIndex(binding.propertyName.Substring(11)) < 0)
                    throw new Exception("输入包含找不到的 BlendShape 或不支持的蒙皮属性：" + binding.path + ":" + binding.propertyName);
            }
        }

        static Transform Find(Transform root, string path) { return string.IsNullOrEmpty(path) ? root : root.Find(path); }
        static bool ActiveWithin(Transform target, Transform root)
        {
            for (var current = target; current && current != root; current = current.parent)
                if (!current.gameObject.activeSelf) return false;
            return true;
        }

        static void RestoreBindPose(SkinnedMeshRenderer[] skins, out float error)
        {
            var worlds = new Dictionary<Transform, Matrix4x4>();
            foreach (var skin in skins)
            {
                var bindposes = skin.sharedMesh.bindposes; var bones = skin.bones;
                for (int i = 0; i < bones.Length; i++)
                {
                    if (Mathf.Abs(bindposes[i].determinant) < 1e-12f) throw new Exception("绑定矩阵不可逆：" + skin.name);
                    var world = skin.transform.localToWorldMatrix * bindposes[i].inverse;
                    Matrix4x4 existing;
                    if (worlds.TryGetValue(bones[i], out existing) && MatrixError(existing, world) > .002f) throw new Exception("不同网格的绑定姿势不一致：" + bones[i].name);
                    worlds[bones[i]] = world;
                }
            }
            foreach (var item in worlds.OrderBy(p => Depth(p.Key)))
            {
                var local = item.Key.parent ? item.Key.parent.worldToLocalMatrix * item.Value : item.Value;
                Vector3 p = local.GetColumn(3), s = local.lossyScale; Quaternion q = local.rotation;
                if (MatrixError(local, Matrix4x4.TRS(p, q, s)) > .002f) throw new Exception("绑定姿势含无法表示为 Transform 的剪切或镜像矩阵：" + item.Key.name);
                item.Key.localPosition = p; item.Key.localRotation = q; item.Key.localScale = s;
            }
            error = 0;
            foreach (var skin in skins)
            {
                var bindposes = skin.sharedMesh.bindposes; var bones = skin.bones;
                for (int i = 0; i < bones.Length; i++) error = Mathf.Max(error, MatrixError(skin.transform.worldToLocalMatrix * bones[i].localToWorldMatrix * bindposes[i], Matrix4x4.identity));
            }
            if (error > .002f) throw new Exception("无法还原一致的绑定姿势，最大矩阵误差 " + error + "。请使用原始绑定姿势的角色模型。");
        }

        static int Depth(Transform t) { int n = 0; while (t.parent) { n++; t = t.parent; } return n; }
        static float MatrixError(Matrix4x4 a, Matrix4x4 b)
        { float error = 0; for (int i = 0; i < 16; i++) { float d = Mathf.Abs(a[i] - b[i]); if (!Finite(d)) return float.PositiveInfinity; error = Mathf.Max(error, d); } return error; }
        static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
        static int ExportRate(int rate, float duration)
        {
            foreach (int factor in new[] { 1, 2, 3, 4, 6, 8 })
            {
                int candidate = rate * factor;
                if (Math.Abs(duration * (double)candidate - Math.Round(duration * (double)candidate)) < .001) return candidate;
            }
            throw new Exception("动画终点不在 FBX 均匀采样帧上，无法保证末帧姿势。请使用源动画帧率重新烘焙，或先只生成 .anim。");
        }

        static void PutCurve(AnimationClip clip, string path, Type type, string property, List<Keyframe> keys)
        {
            var curve = new AnimationCurve(keys.ToArray());
            for (int i = 0; i < curve.length; i++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
                AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
            }
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, type, property), curve);
        }

        static string SafeName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var name = new string(value.Select(c => invalid.Contains(c) || c == '/' || c == '\\' ? '_' : c).ToArray()).Trim().TrimEnd('.');
            return string.IsNullOrEmpty(name) ? "Character" : name;
        }

        static string OutputPath(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || folder.Contains("\\") || !(folder == "Assets" || folder.StartsWith("Assets/", StringComparison.Ordinal))) throw new Exception("输出父目录必须位于 Assets 内，例如 Assets/BakedAnimations。");
            string absolute = Path.GetFullPath(folder), assets = Path.GetFullPath(Application.dataPath);
            if (!absolute.Equals(assets, StringComparison.OrdinalIgnoreCase) && !absolute.StartsWith(assets + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("输出路径超出 Assets。");
            return absolute;
        }

        static string NewFolder(string parent, string name)
        {
            OutputPath(parent); string current = "Assets";
            foreach (string part in parent.Split('/').Skip(1).Concat(new[] { name }))
            {
                if (string.IsNullOrEmpty(part) || part == "." || part == "..") throw new Exception("输出目录包含无效片段。");
                string next = current + "/" + part;
                if (!AssetDatabase.IsValidFolder(next))
                {
                    if (File.Exists(next) || string.IsNullOrEmpty(AssetDatabase.CreateFolder(current, part))) throw new Exception("无法创建输出目录：" + next);
                }
                current = next;
            }
            return current;
        }

        static void WriteReport(Result result)
        { File.WriteAllText(result.folder + "/bake-report.json", JsonUtility.ToJson(result, true), new System.Text.UTF8Encoding(false)); }

        // Reflection keeps the .anim baker usable when the optional FBX Exporter is not installed.
        sealed class FbxBridge
        {
            readonly Type optionsType;
            readonly MethodInfo export;
            public FbxBridge()
            {
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("UnityEditor.Formats.Fbx.Exporter.ModelExporter")).FirstOrDefault(t => t != null);
                optionsType = type?.Assembly.GetType("UnityEditor.Formats.Fbx.Exporter.ExportModelOptions");
                if (type == null || optionsType == null) throw new Exception("请在 Package Manager 安装 FBX Exporter（com.unity.formats.fbx，5.x）。只烘焙 .anim 无需该包。");
                export = type.GetMethod("ExportObject", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(Object), optionsType }, null);
                if (export == null) throw new Exception("当前 FBX Exporter API 不兼容；需要带 ExportModelOptions 的 ExportObject 接口。");
            }
            public void Export(string path, GameObject character, bool animation)
            {
                if (File.Exists(path)) throw new Exception("拒绝覆盖现有 FBX：" + path);
                var options = Activator.CreateInstance(optionsType);
                Set(options, "ExportFormat", "Binary"); Set(options, "ModelAnimIncludeOption", animation ? "Anim" : "Model");
                Set(options, "AnimateSkinnedMesh", true); Set(options, "UseMayaCompatibleNames", false);
                Set(options, "ObjectPosition", "WorldAbsolute"); Set(options, "LODExportType", "Highest");
                Set(options, "ExportUnrendered", true); Set(options, "KeepInstances", false); Set(options, "EmbedTextures", false);
                try
                {
                    var returned = export.Invoke(null, new object[] { Path.GetFullPath(path), character, options }) as string;
                    if (string.IsNullOrEmpty(returned) || !File.Exists(path) || new FileInfo(path).Length == 0) throw new Exception("FBX Exporter 没有生成有效文件：" + path);
                    // These are newly created assets owned by this run; keep the Unity readback free of lossy curve reduction.
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                    var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                    if (!importer) throw new Exception("找不到导出 FBX 的 ModelImporter：" + path);
                    importer.animationType = ModelImporterAnimationType.Generic;
                    importer.importAnimation = animation;
                    importer.animationCompression = ModelImporterAnimationCompression.Off;
                    importer.optimizeGameObjects = false;
                    importer.importBlendShapes = true;
                    importer.SaveAndReimport();
                }
                catch (TargetInvocationException e) { throw new Exception("FBX 导出失败：" + (e.InnerException ?? e).Message, e.InnerException ?? e); }
            }
            void Set(object options, string name, object value)
            {
                var property = optionsType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property == null || !property.CanWrite) throw new Exception("FBX Exporter 缺少设置：" + name);
                property.SetValue(options, property.PropertyType.IsEnum ? Enum.Parse(property.PropertyType, (string)value) : value, null);
            }
        }
    }
}
#endif
