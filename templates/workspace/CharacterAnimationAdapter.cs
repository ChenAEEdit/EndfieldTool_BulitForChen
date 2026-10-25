using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace EndfieldCharacterWorkbench.Editor
{
public static class CharacterAnimationAdapter
{
    [Serializable] public class Rules
    {
        public float positionScale = 1;
        public bool dropUnmatched, dropSceneTranslation = true, forceLoop;
        public string normalizeRoot = "";
        public string[] excludedDescendants = new string[0];
        public string[] excludedNodes = new string[0];
    }
    [Serializable] public class InputFile { public string path, sha256; }
    [Serializable] public class ClipInput { public string id, name, source; }
    [Serializable] public class Preview { public bool enabled; }
    [Serializable] public class Config
    {
        public string schema, taskId, runId, model, adapterVersion;
        public float modelImportScale = 1;
        public Rules rules;
        public ClipInput[] clips;
        public InputFile[] inputs;
        public Preview preview;
    }
    [Serializable] public class ClipReport
    {
        public string name, output, error, state;
        public int sourceBindings, retainedBindings, skippedAnimator, skippedOther, droppedSceneTranslation,
            remappedBindings, excludedBindings, repairedTangents;
        public bool adapted;
        public string[] unmatched = new string[0];
    }
    [Serializable] public class OutputFile { public string path, sha256; public long bytes; }
    [Serializable] public class MaterialMetadata { public MaterialDescription[] materials; }
    [Serializable] public class MaterialDescription { public string name; public TextureSlot[] texture_slots; }
    [Serializable] public class TextureSlot { public string slot, exported_png; public UV offset, scale; }
    [Serializable] public class UV { public float X, Y; }
    [Serializable] public class MaterialPreview { public string source, output, baseMap; public bool textured; }
    [Serializable] public class Diagnostics
    {
        public int meshes, vertices, bones, missingBones, unweightedVertices, invalidWeights, nonNormalizedVertices, nonFiniteVertices, duplicatePaths, missingBaseMaps;
        public bool geometryPassed;
        public string[] warnings;
    }
    [Serializable] public class BuildReport
    {
        public string schema = "endfield-unity-adaptation-v1", taskId, runId, configSha256, unityVersion, status, error, invocationId;
        public bool adapted, playbackVerified;
        public bool previewCreated;
        public string previewScene;
        public ClipReport[] clips;
        public OutputFile[] outputs;
        public OutputFile[] metadata;
        public MaterialPreview[] materials = new MaterialPreview[0];
        public Diagnostics diagnostics;
        public float modelImportScale, positionScale;
    }

    [Serializable] public class BatchReceipt
    {
        public string schema = "endfield-unity-invocation-v1", invocationId, configPath, configSha256, error;
        public bool succeeded;
    }

    // Invoked only by the desktop runner. A fresh receipt proves this method actually ran.
    public static void BuildBatch()
    {
        string configPath = Environment.GetEnvironmentVariable("ENDFIELD_ADAPT_CONFIG");
        string invocation = Environment.GetEnvironmentVariable("ENDFIELD_ADAPT_INVOCATION");
        string receiptPath = Environment.GetEnvironmentVariable("ENDFIELD_ADAPT_RECEIPT");
        var receipt = new BatchReceipt { invocationId = invocation, configPath = configPath };
        int exitCode = 1;
        try
        {
            Guid parsed;
            if (!Application.isBatchMode || !Guid.TryParseExact(invocation, "N", out parsed)
                || string.IsNullOrEmpty(receiptPath) || !Path.IsPathRooted(receiptPath) || File.Exists(receiptPath))
                throw new Exception("批处理调用信息无效或回执已存在。");
            receipt.configSha256 = Hash(configPath);
            if (receipt.configSha256 != Environment.GetEnvironmentVariable("ENDFIELD_ADAPT_CONFIG_SHA256"))
                throw new Exception("批处理配置摘要不符。");
            Build(configPath, invocation);
            receipt.succeeded = true; exitCode = 0;
        }
        catch (Exception error) { receipt.error = error.ToString(); Debug.LogException(error); }
        finally
        {
            try
            {
                if (!string.IsNullOrEmpty(receiptPath) && Path.IsPathRooted(receiptPath))
                {
                    using (var stream = new FileStream(receiptPath, FileMode.CreateNew, FileAccess.Write))
                    using (var writer = new StreamWriter(stream)) writer.Write(JsonUtility.ToJson(receipt, true));
                }
            }
            catch (Exception error) { Debug.LogException(error); exitCode = 1; }
            EditorApplication.Exit(exitCode);
        }
    }

    [MenuItem("Endfield Character Tool/适配所选配置并生成预览 Prefab")]
    public static void BuildSelected()
    {
        string path = AssetDatabase.GetAssetPath(Selection.activeObject);
        if (Path.GetFileName(path) != "adaptation.json") throw new Exception("请在 Project 中选择生成包内的 adaptation.json。");
        Build(path);
    }

    public static string MatchPath(string source, IEnumerable<string> paths)
    {
        var exact = paths.Where(path => path == source).ToArray();
        if (exact.Length == 1) return exact[0];
        if (string.IsNullOrEmpty(source) || source.StartsWith("path_", StringComparison.Ordinal)) return null;
        var matches = paths.Where(path => path.EndsWith("/" + source, StringComparison.Ordinal)
            || source.EndsWith("/" + path, StringComparison.Ordinal) && !string.IsNullOrEmpty(path)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public static AnimationClip CreateDirectClip(GameObject target, AnimationClip source, Rules rules, ClipReport report)
    {
        if (!(rules.positionScale > 0) || !Finite(rules.positionScale)) throw new Exception("动画位置倍率无效。");
        var transforms = target.GetComponentsInChildren<Transform>(true);
        var paths = transforms.Select(t => AnimationUtility.CalculateTransformPath(t, target.transform)).ToArray();
        if (paths.Distinct().Count() != paths.Length) throw new Exception("目标层级存在重复路径，无法唯一绑定。");
        var exclusions = (rules.excludedDescendants ?? new string[0]).Select(path => MatchPath(path, paths)
            ?? throw new Exception("排除路径不存在或不唯一：" + path)).ToArray();
        var exactExclusions = new HashSet<string>((rules.excludedNodes ?? new string[0]).Select(path => MatchPath(path, paths)
            ?? throw new Exception("排除节点不存在或不唯一：" + path)));
        string normalized = string.IsNullOrEmpty(rules.normalizeRoot) ? null : MatchPath(rules.normalizeRoot, paths)
            ?? throw new Exception("根位移归零路径不存在或不唯一。");
        var missing = new HashSet<string>();
        var bindings = new List<EditorCurveBinding>();
        var curves = new List<AnimationCurve>();
        var clip = new AnimationClip { name = source.name + "_Direct", frameRate = source.frameRate, legacy = false };
        try
        {
            foreach (var binding in AnimationUtility.GetCurveBindings(source))
            {
                report.sourceBindings++;
                if (binding.type == typeof(Animator)) { report.skippedAnimator++; continue; }
                if (binding.type != typeof(Transform)) { report.skippedOther++; continue; }
                bool position = binding.propertyName.StartsWith("m_LocalPosition.", StringComparison.Ordinal);
                if (rules.dropSceneTranslation && position && string.IsNullOrEmpty(binding.path))
                { report.droppedSceneTranslation++; continue; }
                var mapped = MatchPath(binding.path, paths);
                if (mapped == null) { missing.Add(binding.path); continue; }
                // Excluding a head's descendants preserves the head rotation itself.
                if (exactExclusions.Contains(mapped) || exclusions.Any(prefix => mapped.StartsWith(prefix + "/", StringComparison.Ordinal)))
                { report.excludedBindings++; continue; }
                var original = AnimationUtility.GetEditorCurve(source, binding);
                if (original == null || original.length == 0) throw new Exception("源动画存在空曲线：" + binding.path);
                var curve = new AnimationCurve(original.keys) { preWrapMode = original.preWrapMode, postWrapMode = original.postWrapMode };
                var keys = curve.keys;
                float offset = position && mapped == normalized ? original.Evaluate(0) : 0;
                var left = new List<int>(); var right = new List<int>();
                for (int i = 0; i < keys.Length; i++)
                {
                    if (!Finite(keys[i].time) || !Finite(keys[i].value) || i > 0 && keys[i].time < keys[i - 1].time)
                        throw new Exception("非有限数值或逆序时间：" + binding.path + " / " + binding.propertyName);
                    if (position)
                    {
                        keys[i].value = (keys[i].value - offset) * rules.positionScale;
                        keys[i].inTangent *= rules.positionScale; keys[i].outTangent *= rules.positionScale;
                    }
                    if (!Finite(keys[i].value)) throw new Exception("位置换算超出有效数值范围。");
                    if (!Finite(keys[i].inTangent)) { keys[i].inTangent = 0; left.Add(i); report.repairedTangents++; }
                    if (!Finite(keys[i].outTangent)) { keys[i].outTangent = 0; right.Add(i); report.repairedTangents++; }
                }
                curve.keys = keys;
                foreach (int i in left) { AnimationUtility.SetKeyBroken(curve, i, true); AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Constant); }
                foreach (int i in right) { AnimationUtility.SetKeyBroken(curve, i, true); AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Constant); }
                var remapped = binding; remapped.path = mapped;
                if (mapped != binding.path) report.remappedBindings++;
                if (bindings.Any(previous => previous.path == remapped.path && previous.propertyName == remapped.propertyName))
                    throw new Exception("多条源轨道映射到同一目标：" + mapped + " / " + remapped.propertyName);
                bindings.Add(remapped); curves.Add(curve);
            }
            report.unmatched = missing.OrderBy(path => path, StringComparer.Ordinal).ToArray();
            if (missing.Count > 0 && !rules.dropUnmatched) throw new Exception("存在未匹配或不唯一的路径；请核对报告，或明确选择忽略未匹配轨道。");
            if (bindings.Count == 0) throw new Exception("没有可绑定的 Transform 动画曲线。");
            AnimationUtility.SetEditorCurves(clip, bindings.ToArray(), curves.ToArray());
            clip.EnsureQuaternionContinuity();
            var settings = AnimationUtility.GetAnimationClipSettings(source);
            settings.loopTime = rules.forceLoop || settings.loopTime;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            if (clip.isHumanMotion) throw new Exception("直接骨骼动画仍包含 Humanoid 数据。");
            report.retainedBindings = AnimationUtility.GetCurveBindings(clip).Length;
            return clip;
        }
        catch { UnityEngine.Object.DestroyImmediate(clip); throw; }
    }

    public static void Build(string configPath, string invocationId = "")
    {
        configPath = configPath.Replace('\\', '/');
        if (!Regex.IsMatch(configPath, "^Assets/EndfieldCharacterExports/[0-9a-f]{8}_[0-9a-f]{32}/adaptation.json$"))
            throw new Exception("配置必须位于工具生成的 Assets/EndfieldCharacterExports 目录。");
        string basePath = Path.GetDirectoryName(configPath).Replace('\\', '/');
        var config = JsonUtility.FromJson<Config>(File.ReadAllText(configPath));
        if (config.schema != "endfield-unity-adaptation-config-v1" || config.clips == null || config.clips.Length == 0 || config.rules == null)
            throw new Exception("适配配置格式无效。");
        Guid task;
        if (!Guid.TryParse(config.taskId, out task) || !Guid.TryParseExact(config.runId, "N", out _)
            || basePath != "Assets/EndfieldCharacterExports/" + task.ToString("N").Substring(0, 8) + "_" + config.runId)
            throw new Exception("适配配置与目录归属不符。");
        var report = new BuildReport { taskId = config.taskId, runId = config.runId,
            configSha256 = Hash(configPath), unityVersion = Application.unityVersion, status = "running", invocationId = invocationId,
            modelImportScale = config.modelImportScale, positionScale = config.rules.positionScale };
        string generated = basePath + "/Generated", reportPath = generated + "/adaptation-result.json";
        Directory.CreateDirectory(generated);
        if (Directory.EnumerateFileSystemEntries(generated).Any(path => Directory.Exists(path)
            || Path.GetFileName(path) != "adaptation-result.json" && Path.GetExtension(path) != ".meta"))
            throw new Exception("本包已生成内容。请重新生成独立适配包，保留已有修改。");
        File.WriteAllText(reportPath, JsonUtility.ToJson(report, true));
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        var scene = EditorSceneManager.NewPreviewScene();
        var details = new List<ClipReport>(); var outputs = new List<string>();
        try
        {
            foreach (var input in config.inputs)
                if (Hash(Local(basePath, input.path)) != input.sha256) throw new Exception("输入文件摘要已变化：" + input.path);
            string modelPath = Local(basePath, config.model);
            var importer = AssetImporter.GetAtPath(modelPath) as ModelImporter;
            if (importer == null) throw new Exception("模型未被 Unity 导入。");
            if (!(config.modelImportScale > 0) || !Finite(config.modelImportScale)) throw new Exception("模型单位倍率无效。");
            importer.importAnimation = false; importer.animationType = ModelImporterAnimationType.Generic;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel; importer.optimizeGameObjects = false;
            importer.preserveHierarchy = true; importer.globalScale = config.modelImportScale;
            importer.skinWeights = ModelImporterSkinWeights.Custom; importer.maxBonesPerVertex = 4; importer.minBoneWeight = 0.00001f;
            importer.SaveAndReimport();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            if (!prefab) throw new Exception("无法读取已导入模型。");
            var root = UnityEngine.Object.Instantiate(prefab);
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
            root.name = "Character"; root.transform.localPosition = Vector3.zero;
            var avatar = AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<Avatar>().SingleOrDefault(item => item.isValid && !item.isHuman);
            if (!avatar) throw new Exception("模型没有有效的 Generic Avatar。");
            report.materials = PrepareMaterials(root, basePath, generated, outputs);
            report.diagnostics = Diagnose(root, report.materials);
            if (!report.diagnostics.geometryPassed) throw new Exception("模型诊断发现非法骨骼、权重、路径或顶点，请查看诊断报告。");
            string controllerPath = generated + "/Character.controller";
            var controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath); outputs.Add(controllerPath);
            foreach (var input in config.clips)
            {
                var item = new ClipReport { name = input.name }; details.Add(item);
                try
                {
                    var source = AssetDatabase.LoadAssetAtPath<AnimationClip>(Local(basePath, input.source));
                    if (!source) throw new Exception("源动画未导入。");
                    if (string.IsNullOrEmpty(input.id) || input.id.Any(character => !Uri.IsHexDigit(character))) throw new Exception("片段标识无效。");
                    var clip = CreateDirectClip(root, source, config.rules, item); clip.name = input.name;
                    item.output = generated + "/Clip_" + input.id + ".anim";
                    AssetDatabase.CreateAsset(clip, item.output); outputs.Add(item.output);
                    string stateName = "Clip_" + input.id;
                    var state = controller.layers[0].stateMachine.AddState(stateName); state.motion = clip;
                    state.writeDefaultValues = true;
                    item.state = "Base Layer." + stateName;
                    if (details.Count == 1) controller.layers[0].stateMachine.defaultState = state;
                    item.adapted = true;
                }
                catch (Exception error) { item.error = error.Message; }
            }
            var animator = root.GetComponent<Animator>() ?? root.AddComponent<Animator>();
            animator.avatar = avatar; animator.runtimeAnimatorController = controller; animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            string prefabPath = generated + "/Character.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath); outputs.Add(prefabPath);
            AssetDatabase.SaveAssets();
            if (config.preview != null && config.preview.enabled && details.Count == config.clips.Length && details.All(item => item.adapted))
            {
                report.previewScene = generated + "/CharacterPreview.unity";
                CreatePreview(prefabPath, details, report.previewScene);
                outputs.Add(report.previewScene); report.previewCreated = true;
            }
            foreach (var input in config.inputs)
                if (Hash(Local(basePath, input.path)) != input.sha256) throw new Exception("生成期间输入文件发生变化：" + input.path);
            if (Hash(configPath) != report.configSha256) throw new Exception("生成期间适配配置发生变化。");
            report.adapted = details.Count == config.clips.Length && details.All(item => item.adapted);
            report.status = report.adapted ? "adapted_unverified" : "partial";
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            report.outputs = outputs.Select(path => new OutputFile { path = path, bytes = new FileInfo(path).Length, sha256 = Hash(path) }).ToArray();
            if (config.adapterVersion == "0.9.0" || config.adapterVersion == "0.10.0")
            {
                var metadataPaths = config.inputs.Select(input => Local(basePath, input.path)).Concat(outputs).Concat(new[] {
                    "Assets/EndfieldCharacterWorkbench/Editor/CharacterAnimationAdapter.cs",
                    "Assets/EndfieldCharacterWorkbench/Runtime/CharacterPreviewControls.cs" })
                    .Select(path => path + ".meta").Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal);
                report.metadata = metadataPaths.Select(path => new OutputFile { path = path, bytes = new FileInfo(path).Length, sha256 = Hash(path) }).ToArray();
            }
        }
        catch (Exception error) { report.adapted = false; report.status = "failed"; report.error = error.ToString(); }
        finally
        {
            report.clips = details.ToArray();
            EditorSceneManager.ClosePreviewScene(scene);
            File.WriteAllText(reportPath, JsonUtility.ToJson(report, true)); AssetDatabase.Refresh();
        }
        if (!report.adapted) throw new Exception("适配未全部完成，请查看 " + reportPath);
        Debug.Log("原生动画适配完成；尚未验证播放。结果：" + reportPath);
    }

    static MaterialPreview[] PrepareMaterials(GameObject root, string basePath, string generated, List<string> outputs)
    {
        string model = basePath + "/Model", metadata = model + "/material_properties.json";
        if (!File.Exists(metadata)) throw new Exception("缺少真实材质属性，无法确认基础贴图映射。");
        var definitions = JsonUtility.FromJson<MaterialMetadata>(File.ReadAllText(metadata)).materials;
        var created = new Dictionary<string, Material>(); var report = new List<MaterialPreview>();
        string folder = generated + "/Materials"; Directory.CreateDirectory(folder);
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            var materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++)
            {
                string name = materials[i] ? materials[i].name : "MissingMaterial";
                if (!created.TryGetValue(name, out var material))
                {
                    var definition = definitions.SingleOrDefault(item => item.name == name);
                    var slot = definition == null ? null : (definition.texture_slots ?? new TextureSlot[0]).FirstOrDefault(item =>
                        (item.slot == "_BaseMap" || item.slot == "_MainTex") && !string.IsNullOrEmpty(item.exported_png));
                    string texturePath = slot == null ? null : model + "/" + Path.GetFileName(slot.exported_png);
                    var texture = texturePath == null ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
                    var shader = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline
                        ? Shader.Find("Universal Render Pipeline/Lit") : Shader.Find("Standard");
                    if (!shader) throw new Exception("找不到适用的基础预览 Shader。");
                    material = new Material(shader) { name = name, color = Color.white, mainTexture = texture };
                    if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
                    if (slot != null && slot.scale != null) material.mainTextureScale = new Vector2(slot.scale.X, slot.scale.Y);
                    if (slot != null && slot.offset != null) material.mainTextureOffset = new Vector2(slot.offset.X, slot.offset.Y);
                    if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", .05f);
                    if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", .05f);
                    bool overlay = name.IndexOf("eyeshadow", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("eyewhiteshadow", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("hairshadow", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (overlay)
                    {
                        material.mainTexture = null; material.color = new Color(.08f, .06f, .045f, .16f);
                        if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", null);
                        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", material.color);
                        if (material.HasProperty("_Mode")) material.SetFloat("_Mode", 3);
                        if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1);
                        material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                        material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                        material.SetInt("_ZWrite", 0); material.EnableKeyword("_ALPHAPREMULTIPLY_ON");
                        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); material.renderQueue = 3000;
                    }
                    string path = folder + "/Material_" + report.Count + ".mat";
                    AssetDatabase.CreateAsset(material, path); outputs.Add(path); created.Add(name, material);
                    report.Add(new MaterialPreview { source = name, output = path, baseMap = texturePath, textured = texture && !overlay });
                }
                materials[i] = material;
            }
            renderer.sharedMaterials = materials;
        }
        return report.ToArray();
    }

    static Diagnostics Diagnose(GameObject root, MaterialPreview[] materials)
    {
        var result = new Diagnostics(); var warnings = new List<string>(); var bones = new HashSet<Transform>();
        var paths = root.GetComponentsInChildren<Transform>(true).Select(item => AnimationUtility.CalculateTransformPath(item, root.transform)).ToArray();
        result.duplicatePaths = paths.Length - paths.Distinct().Count();
        foreach (var skin in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            var mesh = skin.sharedMesh; if (!mesh) { result.missingBones++; continue; }
            result.meshes++; result.vertices += mesh.vertexCount;
            foreach (var bone in skin.bones) { if (bone) bones.Add(bone); else result.missingBones++; }
            foreach (var vertex in mesh.vertices) if (!Finite(vertex.x) || !Finite(vertex.y) || !Finite(vertex.z)) result.nonFiniteVertices++;
            var weights = mesh.boneWeights;
            if (weights.Length != mesh.vertexCount) result.unweightedVertices += mesh.vertexCount;
            foreach (var weight in weights)
            {
                var values = new[] { weight.weight0, weight.weight1, weight.weight2, weight.weight3 };
                var indices = new[] { weight.boneIndex0, weight.boneIndex1, weight.boneIndex2, weight.boneIndex3 };
                if (values.Sum() <= .00000001f) result.unweightedVertices++;
                else if (Math.Abs(values.Sum() - 1) > .01f) result.nonNormalizedVertices++;
                for (int i = 0; i < 4; i++) if (!Finite(values[i]) || values[i] < 0 || values[i] > 1
                    || values[i] > 0 && (indices[i] < 0 || indices[i] >= skin.bones.Length)) result.invalidWeights++;
            }
        }
        result.bones = bones.Count; result.missingBaseMaps = materials.Count(item => !item.textured);
        if (result.missingBaseMaps > 0) warnings.Add("部分材质使用基础替代或缺少基础贴图，请查看材质映射记录。");
        warnings.Add("权重检查针对 Unity 当前最多四影响的导入结果；未复原游戏 Shader、布料和 IK。");
        result.geometryPassed = result.meshes > 0 && result.missingBones == 0 && result.unweightedVertices == 0
            && result.invalidWeights == 0 && result.nonNormalizedVertices == 0 && result.nonFiniteVertices == 0 && result.duplicatePaths == 0;
        result.warnings = warnings.ToArray(); return result;
    }

    static void CreatePreview(string prefabPath, List<ClipReport> clips, string scenePath)
    {
        var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        // Batch startup can contain an empty untitled scene, which Unity refuses to extend additively.
        // Replacing that empty startup scene is safe; interactive scenes keep the additive path.
        bool emptyBatchStartup = Application.isBatchMode && active.IsValid() && string.IsNullOrEmpty(active.path)
            && active.rootCount == 0;
        var preview = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, emptyBatchStartup ? NewSceneMode.Single : NewSceneMode.Additive);
        try
        {
            UnityEngine.SceneManagement.SceneManager.SetActiveScene(preview);
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var character = (GameObject)PrefabUtility.InstantiatePrefab(asset, preview);
            var renderers = character.GetComponentsInChildren<Renderer>(true).Where(item => item.enabled && item.gameObject.activeInHierarchy).ToArray();
            if (renderers.Length == 0) throw new Exception("预览模型没有可见 Renderer。");
            Bounds bounds = renderers[0].bounds;
            foreach (var renderer in renderers)
            {
                var value = renderer.bounds;
                if (!Finite(value.center.x) || !Finite(value.center.y) || !Finite(value.center.z)
                    || !Finite(value.size.x) || !Finite(value.size.y) || !Finite(value.size.z))
                    throw new Exception("模型包围盒无效，无法安排预览相机。");
                bounds.Encapsulate(value);
            }
            float radius = bounds.extents.magnitude;
            if (!Finite(radius) || radius < .00001f || radius > 1000000) throw new Exception("模型尺寸无法用于预览，请核对导入倍率。");
            var cameraObject = new GameObject("Preview Camera");
            var camera = cameraObject.AddComponent<Camera>(); cameraObject.tag = "MainCamera";
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.12f, .14f, .18f);
            camera.fieldOfView = 32;
            // Use a conservative portrait aspect, leaving space for the control panel at wider ratios.
            float limitingAngle = Mathf.Atan(Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad / 2) * .65f);
            float distance = radius / Mathf.Sin(limitingAngle) * 1.25f;
            camera.nearClipPlane = Mathf.Max(.0001f, radius / 200);
            camera.farClipPlane = Mathf.Max(camera.nearClipPlane + 1, distance * 10 + radius * 2);
            camera.transform.position = bounds.center + Quaternion.Euler(4, 0, 0) * Vector3.forward * distance;
            camera.transform.LookAt(bounds.center);
            var key = new GameObject("Preview Key Light").AddComponent<Light>();
            key.type = LightType.Directional; key.intensity = 1.25f; key.transform.rotation = Quaternion.Euler(35, -25, 0);
            var fill = new GameObject("Preview Fill Light").AddComponent<Light>();
            fill.type = LightType.Directional; fill.intensity = .45f; fill.transform.rotation = Quaternion.Euler(20, 145, 0);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(.55f, .55f, .55f);
            var controls = new GameObject("Animation Preview Controls").AddComponent<EndfieldCharacterWorkbench.CharacterPreviewControls>();
            controls.animator = character.GetComponent<Animator>(); controls.previewCamera = camera;
            controls.clips = clips.Select(item => AssetDatabase.LoadAssetAtPath<AnimationClip>(item.output)).ToArray();
            controls.statePaths = clips.Select(item => item.state).ToArray(); controls.cameraPivot = bounds.center; controls.cameraDistance = distance;
            controls.cameraRadius = radius;
            // Save the imported rest pose. Runtime controls initialize animation only after Play begins.
            if (!EditorSceneManager.SaveScene(preview, scenePath)) throw new Exception("预览场景保存失败。");
            AssetDatabase.SaveAssets();
        }
        finally
        {
            if (active.IsValid() && active.isLoaded) UnityEngine.SceneManagement.SceneManager.SetActiveScene(active);
            EditorSceneManager.CloseScene(preview, true);
        }
    }

    static string Local(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative)) throw new Exception("包内路径无效。");
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("文件路径超出当前适配包。");
        return Path.GetRelativePath(Directory.GetCurrentDirectory(), path).Replace('\\', '/');
    }
    static string Hash(string path)
    {
        using (var stream = File.OpenRead(path)) using (var hash = SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
    }
    static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
}
}
