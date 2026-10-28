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
        public bool dropUnmatched, dropSceneTranslation = true, forceLoop, inferUnits;
        public string normalizeRoot = "";
        public string[] excludedDescendants = new string[0];
        public string[] excludedNodes = new string[0];
    }
    [Serializable] public class InputFile { public string path, sha256; }
    [Serializable] public class ClipInput { public string id, name, source, sourceRig = "", pathEvidence = ""; }
    [Serializable] public class Preview { public bool enabled; }
    [Serializable] public class Config
    {
        public string schema, taskId, runId, model, modelKey, adapterVersion, targetRig, humanMappings, assembly;
        public float modelImportScale = 1;
        public Rules rules;
        public ClipInput[] clips;
        public InputFile[] inputs;
        public Preview preview;
    }
    [Serializable] public class AssemblyNode { public string key, path, rendererType; public bool active, rendererEnabled; }
    [Serializable] public class LodRenderer { public string path, rendererType; public bool isNull; }
    [Serializable] public class LodLevel { public float height, fadeWidth; public LodRenderer[] renderers; }
    [Serializable] public class LodGroupInput { public string path, unitPolicy; public float size; public Vector3 referencePoint; public int fadeMode; public bool enabled, animateCrossFading; public LodLevel[] levels; }
    [Serializable] public class ModelAssembly { public string schema, bodyKey, evidence, sourceEvidenceSha256; public AssemblyNode[] nodes; public bool visualDependenciesResolved; public string[] unsupportedVisualComponents; public LodGroupInput[] lodGroups; }
    [Serializable] public class ClipReport
    {
        public string id, name, output, error, state;
        public int trackAuditVersion = 1;
        public int sourceBindings, sourceObjectBindings, retainedBindings, skippedAnimator, skippedOther, droppedSceneTranslation,
            remappedBindings, excludedBindings, repairedTangents;
        public bool adapted, humanoid;
        public int movingMuscles, movingBodyRotationChannels;
        public string[] unmatched = new string[0];
        public List<TrackAudit> tracks = new List<TrackAudit>();
        public string sourceRigKey = "", sourceRigName = "", retargetMode = "direct";
        public int retargetedPositionTracks, retargetedRotationTracks, retargetedScaleTracks;
        public float rotationSampleRate;
        public bool retargetPoseChecked;
        public float retargetMaxRotationError;
        public int samplingTangentAdjustments;
        public string samplingReferencePolicy = "decoded-key-poses-with-constant-tangents-and-quaternion-continuity";
        public List<string> sourceRestMissing = new List<string>();
        public List<string> worldBodyPaths = new List<string>();
        public List<string> serializedReferencePaths = new List<string>();
        public List<BodyAxisCheck> bodyAxisChecks = new List<BodyAxisCheck>();
    }
    [Serializable] public class TrackAudit
    {
        public string sourcePath, targetPath = "", property, component, decision = "not-evaluated", reason = "";
        public string sourceHash = "", conversion = "none";
        public int keys, outputKeys;
    }
    [Serializable] public class RigNode { public string path, parent, reference; public Vector3 position, scale, serializedPosition, serializedScale; public Quaternion rotation, serializedRotation; }
    [Serializable] public class BodyAxisCheck { public string path, childPath; public float degrees; }
    [Serializable] public class RigEvidence { public string schema, key, name, restPoseKind; public RigNode[] nodes; public string[] bodyBoneNames = new string[0], bodyAxisChildNames = new string[0]; }
    [Serializable] class PathEvidenceRow { public string hash = "", path = "", status = ""; }
    [Serializable] class PathEvidenceFile { public string schema = ""; public PathEvidenceRow[] paths = new PathEvidenceRow[0]; }
    static void CompleteTrackEvidence(ClipReport report, PathEvidenceFile evidence)
    {
        foreach (var row in report.tracks)
        {
            uint hash;
            if (!(row.sourcePath.StartsWith("path_", StringComparison.Ordinal) && uint.TryParse(row.sourcePath.Substring(5), out hash)))
            {
                hash = 0xffffffff;
                foreach (byte value in System.Text.Encoding.UTF8.GetBytes(row.sourcePath))
                { hash ^= value; for (int i = 0; i < 8; i++) hash = (hash >> 1) ^ ((hash & 1) != 0 ? 0xedb88320u : 0); }
                hash ^= 0xffffffff;
            }
            row.sourceHash = hash.ToString();
            var original = evidence?.paths?.FirstOrDefault(p => p.hash == row.sourceHash);
            if (row.reason == "unresolved-source-hash" && original != null && original.status == "ambiguous-source-hash") row.reason = original.status;
        }
    }
    [Serializable] public class OutputFile { public string path, sha256; public long bytes; }
    [Serializable] public class MaterialMetadata { public MaterialDescription[] materials; }
    [Serializable] public class MaterialDescription { public string name; public TextureSlot[] texture_slots; }
    [Serializable] public class TextureSlot { public string slot, exported_png; public UV offset, scale; }
    [Serializable] public class UV { public float X, Y; }
    [Serializable] public class MaterialPreview { public string source, output, baseMap; public bool textured; }
    [Serializable] public class Diagnostics
    {
        public int meshes, vertices, bones, missingBones, unweightedVertices, invalidWeights, nonNormalizedVertices, nonFiniteVertices, duplicatePaths, missingBaseMaps, lodGroups;
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
        public UnitInferenceReport unitInference;
        public string humanAvatar, humanMappingSource;
    }

    [Serializable] public class UnitInferenceReport
    {
        public bool enabled, passed;
        public string method = "skin-bind-segment-lengths-and-semantic-body-animation-curves";
        public string targetRigEvidence, humanMappingEvidence, error = "";
        public float initialFileScale, modelImportScale, modelRatioDeviation, correctedRelativeError, sharedPositionScale;
        public int measuredModelSegments;
        public List<ClipUnitMeasurement> clips = new List<ClipUnitMeasurement>();
    }
    [Serializable] public class ClipUnitMeasurement
    {
        public string id, name, status = "insufficient-evidence";
        public float positionScale, ratioDeviation;
        public int measuredSegments;
    }
    [Serializable] class UnitHumanMappings { public string schema = ""; public UnitHumanMapping[] candidates = new UnitHumanMapping[0]; }
    [Serializable] class UnitHumanMapping { public string name = "", sourceAsset = "", pathId = ""; public UnitHumanBone[] bones = new UnitHumanBone[0]; }
    [Serializable] class UnitHumanBone { public string m_BoneName = "", m_HumanName = ""; }

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
        string path = ResolvePreviewConfig();
        string generated = Path.GetDirectoryName(path) + "/Generated";
        if (!Directory.Exists(generated) || !Directory.EnumerateFileSystemEntries(generated).Any(file =>
            Directory.Exists(file) || Path.GetFileName(file) != "adaptation-result.json" && !file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)))
            Build(path);
        var config = JsonUtility.FromJson<Config>(File.ReadAllText(path));
        if (config.preview != null && config.preview.enabled) OpenExistingPreview(path, false);
        else
        {
            ValidateExistingResult(path);
            string prefabPath = Path.GetDirectoryName(path).Replace('\\', '/') + "/Generated/Character.prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (!prefab) throw new Exception("已有结果缺少预制体，请保留修改并在工作台建立新包。");
            Selection.activeObject = prefab;
            EditorGUIUtility.PingObject(prefab);
            Debug.Log("已生成并核对动画预制体：" + prefabPath + "。本包未请求预览场景。");
        }
    }

    [MenuItem("Endfield Character Tool/打开并播放动画预览")]
    public static void OpenPreviewSelected() { OpenExistingPreview(ResolvePreviewConfig(), true); }

    static string ResolvePreviewConfig()
    {
        foreach (string path in new[] { AssetDatabase.GetAssetPath(Selection.activeObject),
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().path })
        {
            var match = Regex.Match(path ?? "", @"^(Assets/EndfieldCharacterExports/[0-9a-f]{8}_[0-9a-f]{32})(?:/|$)");
            if (match.Success && File.Exists(match.Groups[1].Value + "/adaptation.json"))
                return match.Groups[1].Value + "/adaptation.json";
        }
        var candidates = Directory.Exists("Assets/EndfieldCharacterExports")
            ? Directory.GetFiles("Assets/EndfieldCharacterExports", "adaptation.json", SearchOption.AllDirectories)
                .Select(path => path.Replace('\\', '/')).Where(path => Regex.IsMatch(path,
                    "^Assets/EndfieldCharacterExports/[0-9a-f]{8}_[0-9a-f]{32}/adaptation.json$")).ToArray()
            : new string[0];
        if (candidates.Length == 1) return candidates[0];
        throw new Exception(candidates.Length == 0 ? "此项目还没有适配包，请先在工作台生成 Unity 预览。"
            : "项目中有多个适配包，请选择需要的包文件夹、配置、Prefab 或预览场景，再打开动画预览。");
    }

    static BuildReport ValidateExistingResult(string configPath)
    {
        configPath = configPath.Replace('\\', '/');
        if (!Regex.IsMatch(configPath, "^Assets/EndfieldCharacterExports/[0-9a-f]{8}_[0-9a-f]{32}/adaptation.json$"))
            throw new Exception("预览配置必须位于生成的适配包内。");
        string basePath = Path.GetDirectoryName(configPath).Replace('\\', '/');
        string generated = basePath + "/Generated", reportPath = generated + "/adaptation-result.json";
        if (!File.Exists(reportPath)) throw new Exception("本包尚未完成生成，请先在工作台生成 Unity 预览。");
        var config = JsonUtility.FromJson<Config>(File.ReadAllText(configPath));
        var report = JsonUtility.FromJson<BuildReport>(File.ReadAllText(reportPath));
        if (config.schema != "endfield-unity-adaptation-config-v1" || report.schema != "endfield-unity-adaptation-v1"
            || report.taskId != config.taskId || report.runId != config.runId || report.configSha256 != Hash(configPath)
            || report.status != "adapted_unverified" || !report.adapted || report.clips == null
            || report.clips.Length != config.clips.Length || report.clips.Any(item => !item.adapted))
            throw new Exception("已有生成结果不完整或与本包不符。请查看失败报告，或在工作台建立新包；旧文件已保留。");
        Guid task;
        if (!Guid.TryParse(config.taskId, out task) || !Guid.TryParseExact(config.runId, "N", out _)
            || basePath != "Assets/EndfieldCharacterExports/" + task.ToString("N").Substring(0, 8) + "_" + config.runId)
            throw new Exception("预览配置与包目录不符。");
        if (config.inputs == null || report.outputs == null || report.outputs.Length == 0)
            throw new Exception("已有结果缺少文件清单，无法安全打开。");
        foreach (var input in config.inputs)
            if (Hash(Local(basePath, input.path)) != input.sha256) throw new Exception("预览源文件已变化，请保留修改并生成新包。");
        foreach (var input in config.clips)
        {
            var source = AssetDatabase.LoadAssetAtPath<AnimationClip>(Local(basePath, input.source));
            var analysis = new ClipReport();
            var row = report.clips.SingleOrDefault(item => item.output == generated + "/Clip_" + input.id + ".anim");
            if (source && NeedsHumanMotion(source, analysis) && (row == null || !row.humanoid))
                throw new Exception("此旧包丢弃了主体肌肉通道，会出现 T 字姿势。请用新版工作台重新生成预览，旧包已保留。");
        }
        var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var output in report.outputs)
        {
            if (!outputs.Add(output.path) || !Path.GetFullPath(output.path).StartsWith(Path.GetFullPath(generated) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(output.path) || new FileInfo(output.path).Length != output.bytes || Hash(output.path) != output.sha256)
                throw new Exception("已有生成文件缺失或已修改，请保留修改并在工作台建立新包。");
        }
        if (report.clips.Any(item => item.humanoid) && (report.humanAvatar != generated + "/CharacterHumanAvatar.asset" || !outputs.Contains(report.humanAvatar)))
            throw new Exception("人形动画缺少配套 Avatar，请保留原包并重新生成。");
        if (config.adapterVersion == "0.9.0" || config.adapterVersion == "0.10.0" || config.adapterVersion == "0.11.1" || config.adapterVersion == "0.12.0" || config.adapterVersion == "0.13.0" || config.adapterVersion == "0.15.0")
        {
            var expected = new HashSet<string>(config.inputs.Select(input => Local(basePath, input.path)).Concat(outputs)
                .Concat(new[] { "Assets/EndfieldCharacterWorkbench/Editor/CharacterAnimationAdapter.cs", "Assets/EndfieldCharacterWorkbench/Runtime/CharacterPreviewControls.cs" })
                .Select(path => path + ".meta"), StringComparer.OrdinalIgnoreCase);
            if (report.metadata == null || report.metadata.Length != expected.Count) throw new Exception("生成结果的元数据清单不完整。");
            foreach (var file in report.metadata)
                if (!expected.Remove(file.path) || !File.Exists(file.path) || new FileInfo(file.path).Length != file.bytes || Hash(file.path) != file.sha256)
                    throw new Exception("资产引用已变化，请先核对结果或生成独立新包。");
        }
        return report;
    }

    public static string OpenExistingPreview(string configPath, bool play)
    {
        configPath = configPath.Replace('\\', '/');
        var report = ValidateExistingResult(configPath);
        string scenePath = Path.GetDirectoryName(configPath).Replace('\\', '/') + "/Generated/CharacterPreview.unity";
        if (!report.previewCreated || report.previewScene != scenePath || !report.outputs.Any(item => item.path == scenePath))
            throw new Exception("本包未生成预览场景。请在工作台勾选生成预览场景后建立新包。");
        var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            if (active.path == scenePath) return scenePath;
            throw new Exception("Unity 当前正在播放其他场景，请先退出 Play 再打开此预览。");
        }
        if (active.path != scenePath)
        {
            if (Application.isBatchMode && active.isDirty) throw new Exception("当前场景有未保存内容，批处理不切换场景。");
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                throw new OperationCanceledException("已取消打开预览，当前场景保留。");
            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
        }
        if (play)
        {
            var gameView = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
            if (!Application.isBatchMode && gameView != null) EditorWindow.GetWindow(gameView).Focus();
            EditorApplication.isPlaying = true;
        }
        Debug.Log(play ? "已打开动画预览并请求 Play：" + scenePath : "预览已生成，已打开场景。点击 Unity 顶部 Play 播放：" + scenePath);
        return scenePath;
    }

    [Serializable] public class PreviewOpenRequest
    { public string schema = "endfield-preview-open-v1", requestId, configPath, configSha256; public bool play = true; }
    [Serializable] public class PreviewOpenReceipt
    { public string schema = "endfield-preview-open-result-v1", requestId, scene, error, status; public bool playRequested; }
    static double nextPreviewRequest;
    [InitializeOnLoadMethod]
    static void RegisterPreviewRequests()
    {
        if (!Application.isBatchMode) { EditorApplication.update -= PollPreviewRequest; EditorApplication.update += PollPreviewRequest; }
    }
    static void PollPreviewRequest()
    {
        if (EditorApplication.timeSinceStartup < nextPreviewRequest || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        nextPreviewRequest = EditorApplication.timeSinceStartup + 1;
        if (File.Exists("Library/EndfieldCharacterWorkbench/preview-open.json")) OpenRequestedPreview();
    }
    // Also usable as -executeMethod after starting a closed project.
    public static void OpenRequestedPreview()
    {
        string requestPath = "Library/EndfieldCharacterWorkbench/preview-open.json";
        string receiptPath = "Library/EndfieldCharacterWorkbench/preview-open-result.json";
        if (!File.Exists(requestPath)) return;
        PreviewOpenRequest request;
        try { request = JsonUtility.FromJson<PreviewOpenRequest>(File.ReadAllText(requestPath)); }
        catch (Exception error) { Debug.LogWarning("预览请求暂不可读：" + error.Message); return; }
        Guid parsed;
        if (request == null || request.schema != "endfield-preview-open-v1" || !Guid.TryParseExact(request.requestId, "N", out parsed)) return;
        if (SessionState.GetString("EndfieldPreviewOpen", "") == request.requestId) return;
        if (File.Exists(receiptPath))
        {
            try { if (JsonUtility.FromJson<PreviewOpenReceipt>(File.ReadAllText(receiptPath)).requestId == request.requestId) return; }
            catch (Exception) { }
        }
        SessionState.SetString("EndfieldPreviewOpen", request.requestId);
        var receipt = new PreviewOpenReceipt { requestId = request.requestId };
        try
        {
            if (string.IsNullOrEmpty(request.configPath) || !Regex.IsMatch(request.configPath,
                "^Assets/EndfieldCharacterExports/[0-9a-f]{8}_[0-9a-f]{32}/adaptation.json$") || Hash(request.configPath) != request.configSha256)
                throw new Exception("打开预览的配置或摘要不符。");
            receipt.scene = OpenExistingPreview(request.configPath, request.play);
            receipt.playRequested = request.play; receipt.status = "opened";
        }
        catch (Exception error) { receipt.status = error is OperationCanceledException ? "cancelled" : "failed"; receipt.error = error.Message; Debug.LogWarning(receipt.error); }
        string temporary = receiptPath + "." + request.requestId + ".tmp";
        File.WriteAllText(temporary, JsonUtility.ToJson(receipt, true));
        if (File.Exists(receiptPath)) File.Replace(temporary, receiptPath, null); else File.Move(temporary, receiptPath);
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

    public static AnimationClip CreateDirectClip(GameObject target, AnimationClip source, Rules rules, ClipReport report, bool humanoid = false, RigEvidence sourceRig = null)
    {
        var sourceBindings = AnimationUtility.GetCurveBindings(source);
        report.sourceBindings = sourceBindings.Length;
        report.sourceObjectBindings = AnimationUtility.GetObjectReferenceCurveBindings(source).Length;
        report.tracks = sourceBindings.Select(binding => new TrackAudit { sourcePath = binding.path, property = binding.propertyName,
            component = binding.type.FullName, keys = AnimationUtility.GetEditorCurve(source, binding)?.length ?? 0 }).ToList();
        var missing = new HashSet<string>();
        foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(source))
        {
            bool omit = rules.dropUnmatched;
            if (!omit) missing.Add(binding.path);
            report.tracks.Add(new TrackAudit { sourcePath = binding.path, property = binding.propertyName, component = binding.type.FullName,
                keys = AnimationUtility.GetObjectReferenceCurve(source, binding).Length, decision = omit ? "omit" : "reject", reason = "unsupported-object-reference-track" });
        }
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
        var bindings = new List<EditorCurveBinding>();
        var curves = new List<AnimationCurve>();
        var clip = new AnimationClip { name = source.name + "_Direct", frameRate = source.frameRate, legacy = false };
        AnimationClip convertedSource = null;
        try
        {
            if (sourceRig != null && !humanoid) convertedSource = RetargetSourcePose(target, source, sourceRig, report);
            if (convertedSource) clip.frameRate = Mathf.Max(source.frameRate, report.rotationSampleRate);
            for (int bindingIndex = 0; bindingIndex < sourceBindings.Length; bindingIndex++)
            {
                var binding = sourceBindings[bindingIndex];
                var audit = report.tracks[bindingIndex];
                bool muscleBinding = binding.type == typeof(Animator);
                if (muscleBinding && !humanoid)
                {
                    report.skippedAnimator++; missing.Add(binding.path);
                    audit.decision = rules.dropUnmatched ? "omit" : "reject"; audit.reason = "animator-track-in-generic-mode"; continue;
                }
                bool visibility = binding.type == typeof(GameObject) && binding.propertyName == "m_IsActive"
                    || typeof(Renderer).IsAssignableFrom(binding.type) && binding.propertyName == "m_Enabled";
                bool blendShape = binding.type == typeof(SkinnedMeshRenderer) && binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal);
                if (binding.type != typeof(Transform) && !muscleBinding && !visibility && !blendShape)
                {
                    report.skippedOther++; missing.Add(binding.path);
                    audit.decision = rules.dropUnmatched ? "omit" : "reject"; audit.reason = "unsupported-component-track"; continue;
                }
                bool position = binding.propertyName.StartsWith("m_LocalPosition.", StringComparison.Ordinal);
                if (rules.dropSceneTranslation && position && string.IsNullOrEmpty(binding.path))
                { report.droppedSceneTranslation++; audit.decision = "omit"; audit.reason = "scene-translation-disabled"; continue; }
                var mapped = muscleBinding ? "" : MatchPath(binding.path, paths);
                if (convertedSource && report.sourceRestMissing.Contains(binding.path))
                {
                    missing.Add(binding.path); audit.decision = rules.dropUnmatched ? "omit" : "reject"; audit.reason = "missing-source-rest-pose"; continue;
                }
                if (mapped == null)
                {
                    missing.Add(binding.path); audit.decision = rules.dropUnmatched ? "omit" : "reject";
                    audit.reason = binding.path.StartsWith("path_", StringComparison.Ordinal) ? "unresolved-source-hash"
                        : paths.Count(path => path.EndsWith("/" + binding.path, StringComparison.Ordinal) || binding.path.EndsWith("/" + path, StringComparison.Ordinal) && path.Length > 0) > 1 ? "ambiguous-target-path" : "missing-target-node";
                    continue;
                }
                audit.targetPath = mapped;
                var targetNode = transforms[Array.IndexOf(paths, mapped)];
                if (visibility && binding.type != typeof(GameObject) && !targetNode.GetComponent(binding.type))
                    throw new Exception("显隐轨道找不到对应 Renderer：" + mapped);
                if (blendShape)
                {
                    var skin = targetNode.GetComponent<SkinnedMeshRenderer>();
                    if (!skin || !skin.sharedMesh || skin.sharedMesh.GetBlendShapeIndex(binding.propertyName.Substring("blendShape.".Length)) < 0)
                        throw new Exception("表情轨道找不到对应 BlendShape：" + mapped + " / " + binding.propertyName);
                }
                // Excluding a head's descendants preserves the head rotation itself.
                if (exactExclusions.Contains(mapped) || exclusions.Any(prefix => mapped.StartsWith(prefix + "/", StringComparison.Ordinal)))
                { report.excludedBindings++; audit.decision = "omit"; audit.reason = exactExclusions.Contains(mapped) ? "user-excluded-node" : "user-excluded-descendant"; continue; }
                var original = AnimationUtility.GetEditorCurve(convertedSource ? convertedSource : source, binding);
                if (original == null || original.length == 0) throw new Exception("源动画存在空曲线：" + binding.path);
                var curve = new AnimationCurve(original.keys) { preWrapMode = original.preWrapMode, postWrapMode = original.postWrapMode };
                var keys = curve.keys;
                float offset = position && mapped == normalized ? original.Evaluate(0) : 0;
                var left = new List<int>(); var right = new List<int>();
                for (int i = 0; i < keys.Length; i++)
                {
                    if (!Finite(keys[i].time) || !Finite(keys[i].value) || i > 0 && keys[i].time < keys[i - 1].time)
                        throw new Exception("非有限数值或逆序时间：" + binding.path + " / " + binding.propertyName);
                    if (visibility && keys[i].value != 0 && keys[i].value != 1) throw new Exception("显隐轨道包含非布尔值：" + binding.path);
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
                if (visibility)
                    for (int i = 0; i < keys.Length; i++)
                    {
                        AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Constant);
                        AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Constant);
                    }
                foreach (int i in left) { AnimationUtility.SetKeyBroken(curve, i, true); AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Constant); }
                foreach (int i in right) { AnimationUtility.SetKeyBroken(curve, i, true); AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Constant); }
                var remapped = binding; remapped.path = mapped;
                if (mapped != binding.path) report.remappedBindings++;
                if (bindings.Any(previous => previous.path == remapped.path && previous.propertyName == remapped.propertyName && previous.type == remapped.type))
                {
                    audit.decision = "reject"; audit.reason = "multiple-source-tracks-to-one-target";
                    throw new Exception("多条源轨道映射到同一目标：" + mapped + " / " + remapped.propertyName);
                }
                bindings.Add(remapped); curves.Add(curve);
                audit.decision = "retain"; audit.reason = mapped == binding.path ? "exact-path" : "unique-suffix-path";
                audit.outputKeys = curve.length; audit.conversion = !convertedSource ? "direct"
                    : report.worldBodyPaths.Contains(binding.path) && binding.propertyName.StartsWith("m_LocalRotation.") ? "body-world-orientation"
                    : report.serializedReferencePaths.Contains(binding.path) ? "serialized-body-reference-relative" : "source-bind-relative";
            }
            report.unmatched = missing.OrderBy(path => path, StringComparer.Ordinal).ToArray();
            if (missing.Count > 0 && !rules.dropUnmatched) throw new Exception("存在未匹配或不唯一的路径；请核对报告，或明确选择忽略未匹配轨道。");
            if (bindings.Count == 0) throw new Exception("没有可绑定的 Transform 动画曲线。");
            AnimationUtility.SetEditorCurves(clip, bindings.ToArray(), curves.ToArray());
            if (convertedSource)
            {
                try { VerifyRetargetedClip(target, source, clip, sourceRig, report); }
                catch (Exception error) { throw new Exception("旋转连续性处理前：" + error.Message); }
                report.retargetPoseChecked = false; report.retargetMaxRotationError = 0;
            }
            clip.EnsureQuaternionContinuity();
            var settings = AnimationUtility.GetAnimationClipSettings(source);
            settings.loopTime = rules.forceLoop || settings.loopTime;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            if (clip.isHumanMotion != humanoid) throw new Exception("生成动画的播放模式与主体通道不一致。");
            if (convertedSource) VerifyRetargetedClip(target, source, clip, sourceRig, report);
            report.retainedBindings = AnimationUtility.GetCurveBindings(clip).Length;
            return clip;
        }
        catch { UnityEngine.Object.DestroyImmediate(clip); throw; }
        finally { if (convertedSource) UnityEngine.Object.DestroyImmediate(convertedSource); }
    }

    // Explicit local-rest retargeting. It preserves destination bone offsets and
    // converts the source motion delta; it does not recreate game IK/correctives.
    public static AnimationClip RetargetSourcePose(GameObject target, AnimationClip source, RigEvidence rig, ClipReport report)
    {
        if (rig.schema != "endfield-rig-evidence-v1" || rig.nodes == null || rig.nodes.Length == 0 || rig.restPoseKind != "skin-bind-matrices-with-per-node-provenance")
            throw new Exception("动作原骨架缺少真实绑定矩阵及姿态来源，请重新解析对象。");
        var nodes = rig.nodes.ToDictionary(node => node.path, StringComparer.Ordinal);
        int samplingRepairs;
        var samplingSource = CreateTransformSamplingClip(source, out samplingRepairs);
        source = samplingSource; report.samplingTangentAdjustments = samplingRepairs;
        var targets = target.GetComponentsInChildren<Transform>(true).ToDictionary(t => AnimationUtility.CalculateTransformPath(t, target.transform));
        // The unweighted motion carrier above Hips has no skin bind matrix.
        // Its explicitly recorded serialized reference defines motion deltas;
        // this is not claimed to be a recovered skin bind pose.
        var bodyNodes = rig.nodes.Where(n => rig.bodyBoneNames.Contains(n.path.Split('/').Last())).ToArray();
        var bodyPaths = new HashSet<string>(bodyNodes.Select(n => n.path));
        var carrierPaths = new HashSet<string>(bodyNodes.Where(n => n.parent != null && !bodyPaths.Contains(n.parent)).Select(n => n.parent));
        var converted = UnityEngine.Object.Instantiate(source);
        try
        {
            report.sourceRigKey = rig.key; report.sourceRigName = rig.name; report.retargetMode = "source-local-rest-relative";
            foreach (var group in AnimationUtility.GetCurveBindings(source).Where(b => b.type == typeof(Transform)).GroupBy(b => b.path))
            {
                string mapped = MatchPath(group.Key, targets.Keys);
                RigNode node;
                if (mapped == null) continue;
                if (!nodes.TryGetValue(group.Key, out node))
                { report.sourceRestMissing.Add(group.Key); continue; }
                if (node.reference == "ambiguous-skin-bind-pose" && (bodyPaths.Contains(group.Key) || carrierPaths.Contains(group.Key)))
                    throw new Exception("主体骨骼的绑定姿态存在歧义，不能省略后继续：" + group.Key);
                bool serializedBodyReference = node.reference != "skin-bind-pose" && node.reference != "rig-root-reference"
                    && (bodyPaths.Contains(group.Key) || carrierPaths.Contains(group.Key)) && node.reference != "ambiguous-skin-bind-pose";
                if (!serializedBodyReference && node.reference != "skin-bind-pose" && node.reference != "rig-root-reference")
                { report.sourceRestMissing.Add(group.Key); continue; }
                var referencePosition = serializedBodyReference ? node.serializedPosition : node.position;
                var referenceRotation = serializedBodyReference ? node.serializedRotation : node.rotation;
                var referenceScale = serializedBodyReference ? node.serializedScale : node.scale;
                if (serializedBodyReference) report.serializedReferencePaths.Add(group.Key);
                var destination = targets[mapped];
                string targetParent = destination == target.transform ? null : AnimationUtility.CalculateTransformPath(destination.parent, target.transform);
                bool prefixedRoot = node.parent == "" && group.Key.Length > 0 && !group.Key.Contains("/") && mapped.EndsWith("/" + group.Key, StringComparison.Ordinal);
                bool virtualRoot = group.Key.Length == 0 && mapped.Length == 0;
                if (!virtualRoot && node.parent != null && !prefixedRoot && MatchPath(node.parent, targets.Keys) != targetParent) throw new Exception("源骨架与目标的父子关系不一致，不能按本地姿态换算：" + group.Key);
                if (!Finite(referencePosition.x) || !Finite(referencePosition.y) || !Finite(referencePosition.z) || !Finite(referenceScale.x) || !Finite(referenceScale.y) || !Finite(referenceScale.z)
                    || !Finite(referenceRotation.x) || !Finite(referenceRotation.y) || !Finite(referenceRotation.z) || !Finite(referenceRotation.w) || Quaternion.Dot(referenceRotation, referenceRotation) < .000001f)
                    throw new Exception("源骨架静态姿态包含非法数值：" + group.Key);
                foreach (var binding in group.Where(b => b.propertyName.StartsWith("m_LocalPosition.") || b.propertyName.StartsWith("m_LocalScale.")))
                {
                    var input = AnimationUtility.GetEditorCurve(source, binding);
                    int axis = "xyz".IndexOf(binding.propertyName.Last());
                    if (axis < 0 || input == null) throw new Exception("源位置或缩放曲线无效。");
                    bool position = binding.propertyName.StartsWith("m_LocalPosition.");
                    float offset = position ? destination.localPosition[axis] - referencePosition[axis] : 0;
                    float ratio = 1;
                    if (!position)
                    {
                        if (Math.Abs(referenceScale[axis]) < .000001f) throw new Exception("源静态缩放为零，无法可靠换算：" + group.Key);
                        ratio = destination.localScale[axis] / referenceScale[axis];
                    }
                    var keys = input.keys;
                    for (int i = 0; i < keys.Length; i++) { keys[i].value = keys[i].value * ratio + offset; keys[i].inTangent *= ratio; keys[i].outTangent *= ratio; }
                    AnimationUtility.SetEditorCurve(converted, binding, new AnimationCurve(keys) { preWrapMode = input.preWrapMode, postWrapMode = input.postWrapMode });
                    if (position) report.retargetedPositionTracks++; else report.retargetedScaleTracks++;
                }
                var rotations = group.Where(b => b.propertyName.StartsWith("m_LocalRotation.")).OrderBy(b => "xyzw".IndexOf(b.propertyName.Last())).ToArray();
                var offsetRotation = destination.localRotation * Quaternion.Inverse(referenceRotation.normalized);
                if (group.Any(b => b.propertyName.Contains("Euler")) && Quaternion.Angle(offsetRotation, Quaternion.identity) > .001f)
                    throw new Exception("此源骨架姿态换算不支持欧拉旋转轨道，请保留源文件并检查：" + group.Key);
                if (rotations.Length == 0 || Quaternion.Angle(offsetRotation, Quaternion.identity) < .001f) continue;
                if (rotations.Length != 4) throw new Exception("源四元数轨道不完整，不能可靠换算：" + group.Key);
                var inputs = rotations.Select(b => AnimationUtility.GetEditorCurve(source, b)).ToArray();
                float rate = Mathf.Clamp(Mathf.Max(120, source.frameRate), 120, 240);
                int frames = Mathf.CeilToInt(source.length * rate);
                if (frames > 500000) throw new Exception("姿态换算采样超过上限。");
                var times = new SortedSet<float>(inputs.SelectMany(c => c.keys).Select(k => k.time));
                for (int i = 0; i <= frames; i++) times.Add(Mathf.Min(source.length, i / rate));
                var baked = Enumerable.Range(0, 4).Select(_ => new List<Keyframe>()).ToArray();
                Quaternion previous = Quaternion.identity; bool first = true;
                foreach (float time in times)
                {
                    var rotation = offsetRotation * new Quaternion(inputs[0].Evaluate(time), inputs[1].Evaluate(time), inputs[2].Evaluate(time), inputs[3].Evaluate(time)).normalized;
                    if (!first && Quaternion.Dot(previous, rotation) < 0) rotation = new Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w);
                    first = false; previous = rotation;
                    for (int axis = 0; axis < 4; axis++) baked[axis].Add(new Keyframe(time, rotation[axis]));
                }
                for (int axis = 0; axis < 4; axis++)
                {
                    var keys = baked[axis].ToArray();
                    for (int i = 0; i < keys.Length; i++)
                    {
                        keys[i].inTangent = i == 0 ? 0 : (keys[i].value - keys[i - 1].value) / (keys[i].time - keys[i - 1].time);
                        keys[i].outTangent = i + 1 == keys.Length ? 0 : (keys[i + 1].value - keys[i].value) / (keys[i + 1].time - keys[i].time);
                    }
                    AnimationUtility.SetEditorCurve(converted, rotations[axis], new AnimationCurve(keys));
                }
                report.retargetedRotationTracks += 4; report.rotationSampleRate = rate;
            }
            RetargetBodyWorld(target, source, converted, rig, report);
            return converted;
        }
        catch { UnityEngine.Object.DestroyImmediate(converted); throw; }
        finally { UnityEngine.Object.DestroyImmediate(samplingSource); }
    }

    static AnimationClip CreateTransformSamplingClip(AnimationClip source, out int repairs)
    {
        repairs = 0;
        var clip = new AnimationClip { frameRate = source.frameRate, legacy = false };
        try
        {
            var bindings = AnimationUtility.GetCurveBindings(source).Where(b => b.type == typeof(Transform)).ToArray();
            var curves = new List<AnimationCurve>();
            foreach (var binding in bindings)
            {
                var input = AnimationUtility.GetEditorCurve(source, binding);
                var keys = input.keys; var left = new List<int>(); var right = new List<int>();
                for (int i = 0; i < keys.Length; i++)
                {
                    if (!Finite(keys[i].time) || !Finite(keys[i].value)) throw new Exception("源采样曲线包含非法关键帧：" + binding.path);
                    if (!Finite(keys[i].inTangent)) { keys[i].inTangent = 0; left.Add(i); repairs++; }
                    if (!Finite(keys[i].outTangent)) { keys[i].outTangent = 0; right.Add(i); repairs++; }
                }
                var curve = new AnimationCurve(keys) { preWrapMode = input.preWrapMode, postWrapMode = input.postWrapMode };
                foreach (int i in left) { AnimationUtility.SetKeyBroken(curve, i, true); AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Constant); }
                foreach (int i in right) { AnimationUtility.SetKeyBroken(curve, i, true); AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Constant); }
                curves.Add(curve);
            }
            AnimationUtility.SetEditorCurves(clip, bindings, curves.ToArray());
            clip.EnsureQuaternionContinuity();
            return clip;
        }
        catch { UnityEngine.Object.DestroyImmediate(clip); throw; }
    }

    static void RetargetBodyWorld(GameObject target, AnimationClip source, AnimationClip converted, RigEvidence rig, ClipReport report)
    {
        if (rig.bodyBoneNames == null || rig.bodyBoneNames.Length < 15) throw new Exception("源姿态换算缺少可核对的人形骨骼名称映射。");
        GameObject virtualSource = null, virtualTarget = null; AnimationClip sampleTarget = null;
        var scene = EditorSceneManager.NewPreviewScene();
        try
        {
            virtualSource = new GameObject("SourceRigReference");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(virtualSource, scene);
            var sourceTransforms = new Dictionary<string, Transform>();
            foreach (var node in rig.nodes.OrderBy(n => n.path.Count(c => c == '/')).ThenBy(n => n.path.Length))
            {
                var item = node.path.Length == 0 ? virtualSource.transform : new GameObject(node.path.Split('/').Last()).transform;
                if (node.path.Length > 0) item.SetParent(sourceTransforms[node.parent ?? ""], false);
                item.localPosition = node.serializedPosition; item.localRotation = node.serializedRotation; item.localScale = node.serializedScale;
                sourceTransforms.Add(node.path, item);
            }
            virtualTarget = UnityEngine.Object.Instantiate(target);
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(virtualTarget, scene);
            foreach (var animator in virtualTarget.GetComponentsInChildren<Animator>(true)) UnityEngine.Object.DestroyImmediate(animator);
            foreach (var behaviour in virtualTarget.GetComponentsInChildren<MonoBehaviour>(true)) behaviour.enabled = false;
            var targets = virtualTarget.GetComponentsInChildren<Transform>(true).ToDictionary(t => AnimationUtility.CalculateTransformPath(t, virtualTarget.transform));
            var rawBindings = AnimationUtility.GetCurveBindings(source);
            var body = new List<(string path, Transform source, Transform target, EditorCurveBinding[] bindings)>();
            foreach (var name in rig.bodyBoneNames)
            {
                var sourceNodes = sourceTransforms.Where(p => p.Value.name == name).ToArray();
                if (sourceNodes.Length != 1) throw new Exception("源人形骨骼名称缺失或重名：" + name);
                string path = sourceNodes[0].Key, mapped = MatchPath(path, targets.Keys);
                if (mapped == null) throw new Exception("目标缺少源人形骨骼：" + path);
                var bindings = rawBindings.Where(b => b.type == typeof(Transform) && b.path == path && b.propertyName.StartsWith("m_LocalRotation.")).OrderBy(b => "xyzw".IndexOf(b.propertyName.Last())).ToArray();
                if (bindings.Length == 0) continue;
                if (bindings.Length != 4) throw new Exception("主体四元数轨道不完整：" + path);
                // Check semantic body children, excluding character-specific facial
                // and corrective nodes whose positions do not establish a body axis.
                var children = sourceTransforms.Where(p => p.Value.parent == sourceNodes[0].Value
                        && rig.bodyAxisChildNames.Contains(p.Value.name) && p.Value.localPosition.sqrMagnitude > .00000001f)
                    .Select(p => new { source = p.Value, targetPath = MatchPath(p.Key, targets.Keys) }).Where(p => p.targetPath != null)
                    .OrderByDescending(p => p.source.localPosition.sqrMagnitude).ToArray();
                foreach (var child in children)
                {
                    float angle = Vector3.Angle(child.source.localPosition, targets[child.targetPath].localPosition);
                    report.bodyAxisChecks.Add(new BodyAxisCheck { path = path, childPath = AnimationUtility.CalculateTransformPath(child.source, virtualSource.transform), degrees = angle });
                    if (angle > 5) throw new Exception("对应主链的本地骨轴不同，需要显式坐标映射：" + path + " → " + child.source.name + " / " + angle.ToString("F3") + "°");
                }
                body.Add((path, sourceNodes[0].Value, targets[mapped], bindings));
            }
            body = body.OrderBy(p => p.path.Count(c => c == '/')).ToList();
            if (body.Count < 15) throw new Exception("不足15条完整主体旋转，不能可靠换算共享动作。");
            // Reference sampling must use exactly the supported Transform channels.
            // A cloned raw clip can retain Animator/muscle payload that the final
            // Generic clip deliberately omits, changing intermediate carrier poses.
            sampleTarget = new AnimationClip { frameRate = converted.frameRate, legacy = false };
            var sampleBindings = AnimationUtility.GetCurveBindings(converted);
            var remapped = new List<EditorCurveBinding>(); var curves = new List<AnimationCurve>();
            foreach (var binding in sampleBindings)
            {
                if (binding.type != typeof(Transform) || report.sourceRestMissing.Contains(binding.path)) continue;
                string path = MatchPath(binding.path, targets.Keys);
                if (path == null) continue;
                var mapped = binding; mapped.path = path;
                remapped.Add(mapped); curves.Add(AnimationUtility.GetEditorCurve(converted, binding));
            }
            AnimationUtility.SetEditorCurves(sampleTarget, remapped.ToArray(), curves.ToArray());
            var sourceRest = sourceTransforms.Values.Select(t => (t, t.localPosition, t.localRotation, t.localScale)).ToArray();
            var targetRest = targets.Values.Select(t => (t, t.localPosition, t.localRotation, t.localScale)).ToArray();
            float rate = Mathf.Clamp(Mathf.Max(120, source.frameRate), 120, 240);
            int frames = Mathf.CeilToInt(source.length * rate);
            if (frames > 500000) throw new Exception("主体姿态换算采样超过上限。");
            var times = new SortedSet<float>();
            for (int frame = 0; frame <= frames; frame++) times.Add(Mathf.Min(source.length, frame / rate));
            var relevant = new HashSet<string>();
            foreach (var bone in body)
                for (var t = bone.source; t != null; t = t.parent) relevant.Add(AnimationUtility.CalculateTransformPath(t, virtualSource.transform));
            foreach (var binding in rawBindings.Where(b => b.type == typeof(Transform) && relevant.Contains(b.path) && b.propertyName.StartsWith("m_LocalRotation.")))
                foreach (var key in AnimationUtility.GetEditorCurve(source, binding).keys)
                {
                    times.Add(key.time);
                    if ((!Finite(key.inTangent) || !Finite(key.outTangent)) && key.time > 0)
                        times.Add(Mathf.Max(0, key.time - .00001f));
                }
            if (times.Count > 500000) throw new Exception("主体姿态换算时间点超过上限。");
            var values = body.Select(_ => Enumerable.Range(0, 4).Select(__ => new List<Keyframe>()).ToArray()).ToArray();
            var previous = body.Select(_ => Quaternion.identity).ToArray();
            int sampleIndex = 0;
            foreach (float time in times)
            {
                foreach (var p in sourceRest) { p.t.localPosition = p.localPosition; p.t.localRotation = p.localRotation; p.t.localScale = p.localScale; }
                foreach (var p in targetRest) { p.t.localPosition = p.localPosition; p.t.localRotation = p.localRotation; p.t.localScale = p.localScale; }
                source.SampleAnimation(virtualSource, time); sampleTarget.SampleAnimation(virtualTarget, time);
                for (int i = 0; i < body.Count; i++)
                {
                    body[i].target.rotation = body[i].source.rotation;
                    var q = body[i].target.localRotation.normalized;
                    if (sampleIndex > 0 && Quaternion.Dot(previous[i], q) < 0) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                    previous[i] = q;
                    for (int axis = 0; axis < 4; axis++) values[i][axis].Add(new Keyframe(time, q[axis]));
                }
                sampleIndex++;
            }
            for (int i = 0; i < body.Count; i++)
            {
                report.worldBodyPaths.Add(body[i].path);
                for (int axis = 0; axis < 4; axis++)
                {
                    var keys = values[i][axis].ToArray();
                    for (int k = 0; k < keys.Length; k++)
                    {
                        keys[k].inTangent = k == 0 ? 0 : (keys[k].value - keys[k - 1].value) / (keys[k].time - keys[k - 1].time);
                        keys[k].outTangent = k + 1 == keys.Length ? 0 : (keys[k + 1].value - keys[k].value) / (keys[k + 1].time - keys[k].time);
                    }
                    AnimationUtility.SetEditorCurve(converted, body[i].bindings[axis], new AnimationCurve(keys));
                }
            }
            report.retargetMode = "body-world-orientation-and-local-bind-offsets"; report.rotationSampleRate = rate;
            converted.frameRate = rate;
        }
        finally
        {
            if (sampleTarget) UnityEngine.Object.DestroyImmediate(sampleTarget);
            if (virtualSource) UnityEngine.Object.DestroyImmediate(virtualSource);
            if (virtualTarget) UnityEngine.Object.DestroyImmediate(virtualTarget);
            EditorSceneManager.ClosePreviewScene(scene);
        }
    }

    static void VerifyRetargetedClip(GameObject target, AnimationClip source, AnimationClip output, RigEvidence rig, ClipReport report)
    {
        int repairs;
        var samplingSource = CreateTransformSamplingClip(source, out repairs);
        source = samplingSource;
        var scene = EditorSceneManager.NewPreviewScene();
        GameObject reference = null, actual = null;
        try
        {
            reference = new GameObject("UnmodifiedMotionReference");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(reference, scene);
            var nodes = new Dictionary<string, Transform>();
            foreach (var node in rig.nodes.OrderBy(n => n.path.Count(c => c == '/')).ThenBy(n => n.path.Length))
            {
                var t = node.path.Length == 0 ? reference.transform : new GameObject(node.path.Split('/').Last()).transform;
                if (node.path.Length > 0) t.SetParent(nodes[node.parent ?? ""], false);
                t.localPosition = node.serializedPosition; t.localRotation = node.serializedRotation; t.localScale = node.serializedScale;
                nodes.Add(node.path, t);
            }
            actual = UnityEngine.Object.Instantiate(target);
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(actual, scene);
            foreach (var animator in actual.GetComponentsInChildren<Animator>(true)) UnityEngine.Object.DestroyImmediate(animator);
            foreach (var behaviour in actual.GetComponentsInChildren<MonoBehaviour>(true)) behaviour.enabled = false;
            var targets = actual.GetComponentsInChildren<Transform>(true).ToDictionary(t => AnimationUtility.CalculateTransformPath(t, actual.transform));
            var rest = nodes.Values.Concat(targets.Values).Select(t => (t, t.localPosition, t.localRotation, t.localScale)).ToArray();
            string worst = "";
            for (int frame = 0; frame < 25; frame++)
            {
                foreach (var p in rest) { p.t.localPosition = p.localPosition; p.t.localRotation = p.localRotation; p.t.localScale = p.localScale; }
                float time = source.length * frame / 24;
                source.SampleAnimation(reference, time); output.SampleAnimation(actual, time);
                foreach (string path in report.worldBodyPaths)
                {
                    float error = Quaternion.Angle(nodes[path].rotation, targets[MatchPath(path, targets.Keys)].rotation);
                    if (!Finite(error)) throw new Exception("主体姿态核对出现非法旋转：" + path);
                    if (error > report.retargetMaxRotationError) { report.retargetMaxRotationError = error; worst = path; }
                }
            }
            if (report.retargetMaxRotationError > .25f)
                throw new Exception("共享动作生成后的主体姿态与源动画不一致，已停止输出：" + worst + " / " + report.retargetMaxRotationError.ToString("F3") + "°");
            report.retargetPoseChecked = true;
        }
        finally
        {
            if (reference) UnityEngine.Object.DestroyImmediate(reference);
            if (actual) UnityEngine.Object.DestroyImmediate(actual);
            EditorSceneManager.ClosePreviewScene(scene);
            UnityEngine.Object.DestroyImmediate(samplingSource);
        }
    }

    static bool NeedsHumanMotion(AnimationClip source, ClipReport report)
    {
        var muscles = new HashSet<string>(HumanTrait.MuscleName, StringComparer.Ordinal);
        var body = new HashSet<string>(new[] { "Bip001_L_Thigh", "Bip001_R_Thigh", "Bip001_L_Calf", "Bip001_R_Calf",
            "Bip001_L_UpperArm", "Bip001_R_UpperArm", "Bip001_Spine", "Bip001_Spine1", "Bip001_Spine2" });
        foreach (var binding in AnimationUtility.GetCurveBindings(source))
        {
            var curve = AnimationUtility.GetEditorCurve(source, binding);
            if (curve == null || curve.length < 2 || curve.keys.Max(key => key.value) - curve.keys.Min(key => key.value) < .00001f) continue;
            if (binding.type == typeof(Animator) && muscles.Contains(binding.propertyName)) report.movingMuscles++;
            if (binding.type == typeof(Transform) && body.Contains(binding.path.Split('/').Last())
                && (binding.propertyName.StartsWith("m_LocalRotation.") || binding.propertyName.Contains("Euler"))) report.movingBodyRotationChannels++;
        }
        return report.movingMuscles > 0 && report.movingBodyRotationChannels == 0;
    }

    [Serializable] class HumanMappingBone { public string m_BoneName = "", m_HumanName = ""; }
    [Serializable] class HumanMappingCandidate { public string name = "", sourceAsset = "", sourceBundle = "", pathId = ""; public HumanMappingBone[] bones = new HumanMappingBone[0]; }
    [Serializable] class HumanMappings { public string schema = ""; public HumanMappingCandidate[] candidates = new HumanMappingCandidate[0]; }
    static HumanMappingCandidate SelectHumanMapping(GameObject root, string basePath)
    {
        string file = basePath + "/Model/avatar_human_candidates.json";
        if (!File.Exists(file)) throw new Exception("此动画需要 Humanoid 主体通道。请使用新版工具重新生成包含真实人形映射的包。");
        var mappings = JsonUtility.FromJson<HumanMappings>(File.ReadAllText(file));
        if (mappings.schema != "endfield-human-mapping-candidates-v1" || mappings.candidates == null)
            throw new Exception("人形映射来源文件无效。");
        var transforms = root.GetComponentsInChildren<Transform>(true);
        var names = transforms.GroupBy(item => item.name).ToDictionary(group => group.Key, group => group.Count());
        var candidates = mappings.candidates.Where(candidate => candidate.bones != null && candidate.bones.Length >= 15
            && candidate.bones.All(bone => names.ContainsKey(bone.m_BoneName) && names[bone.m_BoneName] == 1)
            && candidate.bones.Select(bone => bone.m_HumanName).Distinct().Count() == candidate.bones.Length).ToArray();
        var distinct = candidates.GroupBy(candidate => string.Join("|", candidate.bones.OrderBy(bone => bone.m_HumanName)
            .Select(bone => bone.m_HumanName + "=" + bone.m_BoneName))).ToArray();
        if (distinct.Length != 1) throw new Exception("没有唯一且完整匹配模型的源人形映射，不能猜测 Humanoid 骨架。");
        return distinct[0].First();
    }
    static Avatar BuildHumanAvatar(GameObject root, string basePath, out string mappingSource)
    {
        var selected = SelectHumanMapping(root, basePath);
        var transforms = root.GetComponentsInChildren<Transform>(true);
        var human = new HumanDescription {
            human = selected.bones.Select(bone => new HumanBone { boneName = bone.m_BoneName, humanName = bone.m_HumanName, limit = new HumanLimit { useDefaultValues = true } }).ToArray(),
            skeleton = transforms.Select(item => new SkeletonBone { name = item.name, position = item.localPosition, rotation = item.localRotation, scale = item.localScale }).ToArray(),
            hasTranslationDoF = false, armStretch = 0, legStretch = 0, upperArmTwist = 1, lowerArmTwist = 0, upperLegTwist = 1, lowerLegTwist = 0 };
        var avatar = AvatarBuilder.BuildHumanAvatar(root, human);
        if (!avatar || !avatar.isValid || !avatar.isHuman) throw new Exception("按源映射建立的人形 Avatar 无效。");
        avatar.name = "CharacterHumanAvatar";
        mappingSource = selected.name + " / " + selected.sourceAsset + " / PathID " + selected.pathId;
        return avatar;
    }

    static float Median(IEnumerable<float> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        if (ordered.Length == 0) throw new Exception("没有可用于倍率推导的测量值。");
        return ordered[ordered.Length / 2];
    }

    static UnitInferenceReport InferUnitScales(Config config, string basePath, string modelPath, ModelImporter importer)
    {
        var report = new UnitInferenceReport { enabled = true,
            targetRigEvidence = config.targetRig, humanMappingEvidence = config.humanMappings,
            initialFileScale = importer.fileScale };
        try
        {
            if (string.IsNullOrEmpty(config.targetRig) || string.IsNullOrEmpty(config.humanMappings))
                throw new Exception("适配包缺少目标骨架或语义映射证据；请重新解析并导出对象。");
            var rig = JsonUtility.FromJson<RigEvidence>(File.ReadAllText(Local(basePath, config.targetRig)));
            if (rig == null || rig.schema != "endfield-rig-evidence-v1" || rig.nodes == null || rig.nodes.Length == 0)
                throw new Exception("目标模型骨架证据格式无效。");
            var nodes = rig.nodes.ToDictionary(node => node.path, StringComparer.Ordinal);
            var worlds = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
            foreach (var node in rig.nodes.OrderBy(node => node.path.Count(c => c == '/')).ThenBy(node => node.path.Length))
            {
                if (string.IsNullOrEmpty(node.path)) worlds[node.path] = Matrix4x4.identity;
                else
                {
                    if (node.parent == null || !worlds.TryGetValue(node.parent, out var parent))
                        throw new Exception("目标骨架层级证据不完整：" + node.path);
                    worlds[node.path] = parent * Matrix4x4.TRS(node.position, node.rotation, node.scale);
                }
            }
            var mappings = JsonUtility.FromJson<UnitHumanMappings>(File.ReadAllText(Local(basePath, config.humanMappings)));
            if (mappings == null || mappings.schema != "endfield-human-mapping-candidates-v1" || mappings.candidates == null)
                throw new Exception("目标模型语义映射证据格式无效。");

            importer.globalScale = 1;
            importer.SaveAndReimport();
            Func<Dictionary<string, float>> measureModel = () =>
            {
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
                if (!model) throw new Exception("倍率测量时无法载入目标模型。");
                var transforms = model.GetComponentsInChildren<Transform>(true)
                    .ToDictionary(transform => AnimationUtility.CalculateTransformPath(transform, model.transform), StringComparer.Ordinal);
                var ratios = new Dictionary<string, float>(StringComparer.Ordinal);
                foreach (var node in rig.nodes.Where(node => node.reference == "skin-bind-pose" && !string.IsNullOrEmpty(node.parent)
                    && nodes.TryGetValue(node.parent, out var parent) && parent.reference == "skin-bind-pose"))
                {
                    if (!transforms.TryGetValue(node.path, out var child) || !transforms.TryGetValue(node.parent, out var parent)) continue;
                    float expected = Vector3.Distance(worlds[node.path].MultiplyPoint3x4(Vector3.zero), worlds[node.parent].MultiplyPoint3x4(Vector3.zero));
                    float actual = Vector3.Distance(child.position, parent.position);
                    if (expected > .02f && actual > .0000001f && Finite(expected) && Finite(actual)) ratios[node.path] = expected / actual;
                }
                return ratios;
            };
            var before = measureModel();
            if (before.Count < 15) throw new Exception("用于模型倍率推导的骨段不足 15 条。");
            float modelRatio = Median(before.Values);
            report.measuredModelSegments = before.Count;
            report.modelRatioDeviation = before.Values.Max(value => Mathf.Abs(value / modelRatio - 1));
            if (!Finite(modelRatio) || modelRatio < .0001f || modelRatio > 10000 || report.modelRatioDeviation > .01f)
                throw new Exception("模型骨段倍率不一致或超出有效范围；已停止自动推导。");
            float decimalScale = Mathf.Pow(10, Mathf.Round(Mathf.Log10(modelRatio)));
            config.modelImportScale = Mathf.Abs(decimalScale / modelRatio - 1) < .0001f ? decimalScale : modelRatio;
            importer.globalScale = config.modelImportScale;
            importer.SaveAndReimport();
            var after = measureModel();
            report.correctedRelativeError = after.Values.Max(value => Mathf.Abs(value - 1));
            if (after.Count < 15 || report.correctedRelativeError > .01f)
                throw new Exception("模型倍率复测误差超过 1%；已停止生成。");

            var rigNodeNames = rig.nodes.GroupBy(node => node.path.Split('/').Last(), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var matching = mappings.candidates.Where(candidate => candidate.bones != null && candidate.bones.Length >= 15
                && candidate.bones.All(bone => rigNodeNames.TryGetValue(bone.m_BoneName, out int count) && count == 1))
                .GroupBy(candidate => string.Join("|", candidate.bones.OrderBy(bone => bone.m_HumanName)
                    .Select(bone => bone.m_HumanName + ":" + bone.m_BoneName)), StringComparer.Ordinal).ToArray();
            if (matching.Length != 1) throw new Exception("主体语义骨骼映射缺失或有歧义；已停止动画倍率推导。");
            var bodyNames = new HashSet<string>(matching[0].First().bones.Select(bone => bone.m_BoneName), StringComparer.Ordinal);
            var perClipScales = new List<float>();
            foreach (var input in config.clips)
            {
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(Local(basePath, input.source));
                if (!clip) throw new Exception("无法载入动画以推导位置倍率：" + input.name);
                var item = new ClipUnitMeasurement { id = input.id, name = input.name };
                report.clips.Add(item);
                var ratios = new List<float>();
                foreach (var group in AnimationUtility.GetCurveBindings(clip)
                    .Where(binding => binding.type == typeof(Transform) && binding.propertyName.StartsWith("m_LocalPosition.", StringComparison.Ordinal))
                    .GroupBy(binding => binding.path, StringComparer.Ordinal))
                {
                    if (!bodyNames.Contains(group.Key.Split('/').Last()) || !nodes.TryGetValue(group.Key, out var node)
                        || node.reference != "skin-bind-pose" || node.position.magnitude < .05f) continue;
                    var channels = group.ToDictionary(binding => binding.propertyName,
                        binding => AnimationUtility.GetEditorCurve(clip, binding), StringComparer.Ordinal);
                    if (!channels.ContainsKey("m_LocalPosition.x") || !channels.ContainsKey("m_LocalPosition.y") || !channels.ContainsKey("m_LocalPosition.z")) continue;
                    var values = Enumerable.Range(0, 25).Select(index =>
                    {
                        float time = clip.length * index / 24f;
                        return new Vector3(channels["m_LocalPosition.x"].Evaluate(time), channels["m_LocalPosition.y"].Evaluate(time),
                            channels["m_LocalPosition.z"].Evaluate(time)).magnitude;
                    }).ToArray();
                    if (values.Any(value => !Finite(value) || value < .000001f)) continue;
                    float length = Median(values);
                    if (values.Max(value => Mathf.Abs(value / length - 1)) > .01f) continue;
                    ratios.Add(node.position.magnitude / length);
                }
                item.measuredSegments = ratios.Count;
                if (ratios.Count < 8) throw new Exception("动画主体位置倍率证据少于 8 条骨段：" + input.name);
                item.positionScale = Median(ratios);
                item.ratioDeviation = ratios.Max(value => Mathf.Abs(value / item.positionScale - 1));
                if (!Finite(item.positionScale) || item.positionScale < .0001f || item.positionScale > 10000 || item.ratioDeviation > .01f)
                    throw new Exception("动画位置倍率测量不一致：" + input.name);
                item.status = "measured";
                perClipScales.Add(item.positionScale);
            }
            if (perClipScales.Count == 0 || perClipScales.Max() / perClipScales.Min() > 1.01f)
                throw new Exception("所选动画的位置倍率彼此不一致；请分批生成并复测。");
            config.rules.positionScale = Median(perClipScales);
            if (Mathf.Abs(config.rules.positionScale - 1) < .0001f) config.rules.positionScale = 1;
            report.modelImportScale = config.modelImportScale;
            report.sharedPositionScale = config.rules.positionScale;
            report.passed = true;
            return report;
        }
        catch (Exception error)
        {
            report.error = error.Message;
            throw new Exception("自动单位推导失败：" + error.Message, error);
        }
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
        string modelPath = Local(basePath, config.model);
        var importer = AssetImporter.GetAtPath(modelPath) as ModelImporter;
        var scene = EditorSceneManager.NewPreviewScene();
        var details = new List<ClipReport>(); var outputs = new List<string>();
        try
        {
            if (importer == null) throw new Exception("模型未被 Unity 导入。");
            foreach (var input in config.inputs)
                if (Hash(Local(basePath, input.path)) != input.sha256) throw new Exception("输入文件摘要已变化：" + input.path);
            if (config.rules.inferUnits)
            {
                report.unitInference = InferUnitScales(config, basePath, modelPath, importer);
                report.modelImportScale = config.modelImportScale;
                report.positionScale = config.rules.positionScale;
                File.WriteAllText(generated + "/unit-inference.json", JsonUtility.ToJson(report.unitInference, true));
                outputs.Add(generated + "/unit-inference.json");
            }
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
            if (!string.IsNullOrEmpty(config.assembly)) ApplyAssemblyVisibility(root, config, basePath);
            var avatar = AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<Avatar>().SingleOrDefault(item => item.isValid && !item.isHuman);
            if (!avatar) throw new Exception("模型没有有效的 Generic Avatar。");
            Avatar humanAvatar = null;
            report.materials = PrepareMaterials(root, basePath, generated, outputs);
            report.diagnostics = Diagnose(root, report.materials);
            if (!report.diagnostics.geometryPassed) throw new Exception("模型诊断发现非法骨骼、权重、路径或顶点，请查看诊断报告。");
            string controllerPath = generated + "/Character.controller";
            var controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath); outputs.Add(controllerPath);
            foreach (var input in config.clips)
            {
                var item = new ClipReport { id = input.id, name = input.name }; details.Add(item);
                PathEvidenceFile evidence = null;
                try
                {
                    foreach (string metadata in new[] { input.pathEvidence, input.sourceRig }.Where(path => !string.IsNullOrEmpty(path)))
                        if (!config.inputs.Any(file => file.path == metadata)) throw new Exception("源骨架或路径证据未包含在摘要清单中。");
                    if (!string.IsNullOrEmpty(input.pathEvidence))
                    {
                        evidence = JsonUtility.FromJson<PathEvidenceFile>(File.ReadAllText(Local(basePath, input.pathEvidence)));
                        if (evidence.schema != "endfield-animation-path-evidence-v1") throw new Exception("动画路径证据格式无效。");
                    }
                    var sourceRig = string.IsNullOrEmpty(input.sourceRig) ? null : JsonUtility.FromJson<RigEvidence>(File.ReadAllText(Local(basePath, input.sourceRig)));
                    var source = AssetDatabase.LoadAssetAtPath<AnimationClip>(Local(basePath, input.source));
                    if (!source) throw new Exception("源动画未导入。");
                    if (string.IsNullOrEmpty(input.id) || input.id.Any(character => !Uri.IsHexDigit(character))) throw new Exception("片段标识无效。");
                    item.humanoid = NeedsHumanMotion(source, item);
                    if (sourceRig != null && !item.humanoid)
                    {
                        var mapping = SelectHumanMapping(root, basePath);
                        sourceRig.bodyBoneNames = mapping.bones.Where(b => b.m_HumanName != "LeftEye" && b.m_HumanName != "RightEye" && b.m_HumanName != "Jaw")
                            .Select(b => b.m_BoneName).ToArray();
                        // Shoulder and thigh attachment offsets depend on chest/pelvis
                        // proportions; they are not longitudinal bone-axis evidence.
                        sourceRig.bodyAxisChildNames = mapping.bones.Where(b => sourceRig.bodyBoneNames.Contains(b.m_BoneName)
                            && b.m_HumanName != "LeftShoulder" && b.m_HumanName != "RightShoulder"
                            && b.m_HumanName != "LeftUpperLeg" && b.m_HumanName != "RightUpperLeg").Select(b => b.m_BoneName).ToArray();
                    }
                    if (item.humanoid && !humanAvatar)
                    {
                        string mappingSource;
                        humanAvatar = BuildHumanAvatar(root, basePath, out mappingSource);
                        report.humanMappingSource = mappingSource;
                        report.humanAvatar = generated + "/CharacterHumanAvatar.asset";
                        AssetDatabase.CreateAsset(humanAvatar, report.humanAvatar); outputs.Add(report.humanAvatar);
                    }
                    var clip = CreateDirectClip(root, source, config.rules, item, item.humanoid, sourceRig); clip.name = input.name;
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
                finally { CompleteTrackEvidence(item, evidence); }
            }
            var animator = root.GetComponent<Animator>() ?? root.AddComponent<Animator>();
            animator.avatar = details.Count > 0 && details[0].humanoid ? humanAvatar : avatar;
            animator.runtimeAnimatorController = controller; animator.applyRootMotion = false;
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
            if (config.adapterVersion == "0.9.0" || config.adapterVersion == "0.10.0" || config.adapterVersion == "0.11.1" || config.adapterVersion == "0.12.0" || config.adapterVersion == "0.13.0")
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

    static void ApplyAssemblyVisibility(GameObject root, Config config, string basePath)
    {
        if (!config.inputs.Any(file => file.path == config.assembly)) throw new Exception("模型组成证据未包含在输入摘要清单中。");
        var assembly = JsonUtility.FromJson<ModelAssembly>(File.ReadAllText(Local(basePath, config.assembly)));
        if (assembly.schema != "endfield-model-assembly-v1" || assembly.bodyKey != config.modelKey
            || assembly.evidence != "actual-transform-parent-and-child-references" || assembly.nodes == null || assembly.nodes.Length == 0
            || !Regex.IsMatch(assembly.sourceEvidenceSha256 ?? "", "^[A-F0-9]{64}$")
            || assembly.nodes.Select(node => node.path).Distinct().Count() != assembly.nodes.Length)
            throw new Exception("模型组成证据无效或有重复节点。");
        if (!assembly.visualDependenciesResolved)
            throw new Exception("模型组成包含尚未还原的外部状态机效果、粒子或时间线；请查看 model-assembly.json，不能标记完整待机通过。");
        var transforms = root.GetComponentsInChildren<Transform>(true);
        var paths = transforms.Select(t => AnimationUtility.CalculateTransformPath(t, root.transform)).ToArray();
        foreach (var node in assembly.nodes)
        {
            string mapped = MatchPath(node.path, paths);
            if (mapped == null) throw new Exception("导出模型缺少组成节点或路径不唯一：" + node.path);
            var target = transforms[Array.IndexOf(paths, mapped)];
            target.gameObject.SetActive(node.active);
            if (!string.IsNullOrEmpty(node.rendererType))
            {
                var renderer = target.GetComponents<Renderer>().SingleOrDefault(r => r.GetType().Name == node.rendererType);
                if (!renderer) throw new Exception("导出模型缺少组成 Renderer：" + node.path);
                renderer.enabled = node.rendererEnabled;
            }
        }
        foreach (var input in assembly.lodGroups ?? new LodGroupInput[0])
        {
            if (input.unitPolicy != "source-local-times-inferred-position-scale" || input.levels == null || input.levels.Length == 0 || input.levels.Length > 8
                || !Finite(input.size) || input.size <= 0 || !Finite(config.rules.positionScale) || config.rules.positionScale <= 0)
                throw new Exception("LOD 组成或单位证据无效。");
            string mapped = MatchPath(input.path, paths);
            if (mapped == null) throw new Exception("找不到唯一的 LODGroup 挂点：" + input.path);
            var target = transforms[Array.IndexOf(paths, mapped)];
            var group = target.GetComponent<LODGroup>();
            if (!group) group = target.gameObject.AddComponent<LODGroup>();
            var levels = input.levels.Select(level => new LOD(level.height, level.renderers.Select(item =>
            {
                if (item.isNull) return null;
                string path = MatchPath(item.path, paths);
                if (path == null) throw new Exception("LOD Renderer 路径缺失或不唯一：" + item.path);
                var renderer = transforms[Array.IndexOf(paths, path)].GetComponents<Renderer>().SingleOrDefault(r => r.GetType().Name == item.rendererType);
                if (!renderer) throw new Exception("LOD Renderer 类型或引用不符：" + item.path);
                return renderer;
            }).ToArray()) { fadeTransitionWidth = level.fadeWidth }).ToArray();
            var point = input.referencePoint * config.rules.positionScale;
            float size = input.size * config.rules.positionScale;
            if (!Finite(point.x) || !Finite(point.y) || !Finite(point.z) || !Finite(size) || size <= 0) throw new Exception("LOD 单位换算超出有效范围。");
            group.SetLODs(levels); group.localReferencePoint = point;
            group.size = size; group.fadeMode = (LODFadeMode)input.fadeMode;
            group.animateCrossFading = input.animateCrossFading; group.enabled = input.enabled;
        }
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
        var result = new Diagnostics { lodGroups = root.GetComponentsInChildren<LODGroup>(true).Length }; var warnings = new List<string>(); var bones = new HashSet<Transform>();
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
            controls.displayNames = clips.Select(item => item.name).ToArray();
            var genericAvatar = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(character.GetComponent<Animator>().avatar)).OfType<Avatar>().FirstOrDefault(item => item.isValid && !item.isHuman);
            if (!genericAvatar)
            {
                var skin = character.GetComponentsInChildren<SkinnedMeshRenderer>(true).First();
                string modelAsset = AssetDatabase.GetAssetPath(skin.sharedMesh);
                genericAvatar = AssetDatabase.LoadAllAssetsAtPath(modelAsset).OfType<Avatar>().FirstOrDefault(item => item.isValid && !item.isHuman);
            }
            var humanAvatar = AssetDatabase.LoadAssetAtPath<Avatar>(Path.GetDirectoryName(prefabPath).Replace('\\', '/') + "/CharacterHumanAvatar.asset");
            controls.clipAvatars = clips.Select(item => item.humanoid ? humanAvatar : genericAvatar).ToArray();
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
