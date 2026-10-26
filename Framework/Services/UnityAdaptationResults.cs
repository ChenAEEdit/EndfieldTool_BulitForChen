using System.Security.Cryptography;
using System.Text.RegularExpressions;
using EndfieldChenTool.Framework.Domain;

namespace EndfieldChenTool.Framework.Services;

public static class UnityAdaptationResults
{
    public static async Task<ObjectOperationResult> ImportAsync(SavedPlan saved, CancellationToken token)
    {
        ValidatePreparation(saved);
        var taskRoot = Path.GetDirectoryName(saved.FilePath)!;
        using var taskLock = new FileStream(Path.Combine(taskRoot, "extraction.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.ReadWrite);
        taskLock.Lock(0, 1);
        try { return await ImportLockedAsync(UnityTaskTarget.Resolve(saved), token); }
        finally { taskLock.Unlock(0, 1); }
    }

    internal static ObjectOperationResult ValidatePreparation(SavedPlan saved)
    {
        string workspace = new WorkspaceStore().Prepare(saved.Plan.Options);
        string expectedPlan = Path.Combine(workspace, "tasks", saved.Plan.Id.ToString("N"), "plan.json");
        if (!Path.GetFullPath(saved.FilePath).Equals(expectedPlan, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("计划不在对应任务目录。");
        UnityProjectFiles.NoLinks(expectedPlan);
        var result = ObjectWorkflow.ReadPreparation(saved) ?? throw new InvalidDataException("请先生成 Unity 适配包。");
        if (result.Mode != "prepare" || result.Status is not ("unity_prepared" or "adapted_unverified")
            || result.UnityAssetRoot == null || !Regex.IsMatch(result.UnityAssetRoot, "^Assets/EndfieldCharacterExports/[0-9a-f]{8}_[0-9a-f]{32}$"))
            throw new InvalidDataException("当前任务最近一次对象操作不是有效的适配包。");
        if (!Guid.TryParseExact(result.RunId, "N", out _) || result.ExpectedAdaptedClips <= 0
            || result.UnityAssetRoot != $"Assets/EndfieldCharacterExports/{saved.Plan.Id.ToString("N")[..8]}_{result.RunId}")
            throw new InvalidDataException("适配包的运行编号或目标目录不符。");
        var bundle = BundleExtractionBackend.ReadResult(saved);
        if (bundle?.Status != "bundles_extracted" || bundle.RunId != result.BundleRunId)
            throw new InvalidDataException("Bundle 已变化，请重新生成适配包。");
        using (var planStream = File.OpenRead(saved.FilePath))
            if (bundle.Extra == null || !bundle.Extra.TryGetValue("planSha256", out var planHash)
                || planHash.GetString() != Convert.ToHexString(SHA256.HashData(planStream)))
                throw new InvalidDataException("计划已变化，请重新提取并生成适配包。");
        return result;
    }

    // The runner keeps the same task lock across import, generation and result verification.
    internal static async Task<ObjectOperationResult> ImportLockedAsync(SavedPlan saved, CancellationToken token,
        string? expectedInvocationId = null)
    {
        var result = ValidatePreparation(saved);
        var taskRoot = Path.GetDirectoryName(saved.FilePath)!;
        string project = Path.GetFullPath(saved.Plan.Options.UnityProject);
        string assetRoot = UnityProjectFiles.Within(project, result.UnityAssetRoot!);
        string generated = UnityProjectFiles.Within(assetRoot, "Generated");
        string reportPath = UnityProjectFiles.Within(generated, "adaptation-result.json");
        string configPath = UnityProjectFiles.Within(assetRoot, "adaptation.json");
        if (!File.Exists(reportPath)) throw new InvalidDataException("没有找到 Unity 原生适配结果。请先导入并生成适配，或复制适配包到计划指定的 Unity 项目并执行生成菜单。");
        if (await HashAsync(configPath, token) != result.UnityConfigSha256)
            throw new InvalidDataException("项目中的适配配置已变化，请重新生成独立适配包。");
        var config = JsonFiles.Read<NativeConfig>(configPath);
        if (config.Schema != "endfield-unity-adaptation-config-v1" || config.TaskId != saved.Plan.Id.ToString()
            || config.RunId != result.RunId || config.Clips.Count != result.ExpectedAdaptedClips)
            throw new InvalidDataException("项目适配配置与当前任务不符。");
        var inputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in config.Inputs)
        {
            string path = UnityProjectFiles.Within(assetRoot, input.Path);
            if (!inputPaths.Add(path) || await HashAsync(path, token) != input.Sha256)
                throw new InvalidDataException("项目中的源文件缺失、重复或摘要已变化。");
        }
        if (!inputPaths.Contains(UnityProjectFiles.Within(assetRoot, config.Model))
            || config.Clips.Any(clip => !inputPaths.Contains(UnityProjectFiles.Within(assetRoot, clip.Source))))
            throw new InvalidDataException("适配配置未声明完整的模型和源动画。");
        string reportHash = await HashAsync(reportPath, token);
        var report = JsonFiles.Read<NativeReport>(reportPath);
        if (report.Schema != "endfield-unity-adaptation-v1" || report.TaskId != saved.Plan.Id.ToString()
            || report.RunId != result.RunId || report.ConfigSha256 != result.UnityConfigSha256
            || report.Status != "adapted_unverified" || !report.Adapted || report.PlaybackVerified
            || expectedInvocationId != null && report.InvocationId != expectedInvocationId
            || report.Clips.Count != result.ExpectedAdaptedClips || report.Clips.Any(row => !row.Adapted))
            throw new InvalidDataException("Unity 适配未完成，或结果不属于本次适配包；请查看 Unity 报告。");
        if (config.AdapterVersion == "0.12.0")
        {
            foreach (var clip in report.Clips)
            {
                if (clip.TrackAuditVersion != 1 || !config.Clips.Any(input => input.Id == clip.Id && input.Name == clip.Name)
                    || clip.SourceBindings < 0 || clip.SourceObjectBindings < 0 || clip.Tracks.Count != clip.SourceBindings + clip.SourceObjectBindings
                    || clip.Tracks.Count(row => row.Decision == "retain") != clip.RetainedBindings
                    || clip.Tracks.Any(row => !uint.TryParse(row.SourceHash, out _) || row.SourceHash != TrackHash(row.SourcePath) || string.IsNullOrEmpty(row.Property) || string.IsNullOrEmpty(row.Component)
                        || row.Keys <= 0 || row.Decision == "retain" && row.OutputKeys <= 0
                        || row.Decision is not ("retain" or "omit") || row.Reason.Length == 0)
                    || clip.Tracks.Select(row => (row.SourcePath, row.Property, row.Component)).Distinct().Count() != clip.Tracks.Count)
                    throw new InvalidDataException("逐轨道审计不完整、重复或与当前动画不符，不能标记为已核对。");
            }
        }
        bool preview = config.Preview?.Enabled == true;
        if (report.Outputs.Count != report.Clips.Count + (preview ? 3 : 2) + report.Materials.Count + (string.IsNullOrEmpty(report.HumanAvatar) ? 0 : 1) || report.Outputs.Select(row => row.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != report.Outputs.Count)
            throw new InvalidDataException("Unity 适配输出不完整或重复。");
        var outputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in report.Outputs)
        {
            string path = UnityProjectFiles.Within(project, file.Path);
            if (!path.StartsWith(generated + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !outputPaths.Add(path) || !File.Exists(path) || file.Bytes <= 0 || new FileInfo(path).Length != file.Bytes)
                throw new InvalidDataException("Unity 适配输出文件、路径或长度无效。");
            if (await HashAsync(path, token) != file.Sha256) throw new InvalidDataException("Unity 适配输出已变化，摘要不符。");
        }
        if (!outputPaths.Contains(Path.Combine(generated, "Character.prefab")) || !outputPaths.Contains(Path.Combine(generated, "Character.controller"))
            || report.Clips.Select(clip => clip.Output).Distinct(StringComparer.OrdinalIgnoreCase).Count() != report.Clips.Count
            || config.Clips.Select(clip => clip.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != config.Clips.Count
            || config.Clips.Any(clip => !Regex.IsMatch(clip.Id, "^[0-9a-fA-F]+$")
                || report.Clips.Count(native => native.Name == clip.Name
                    && UnityProjectFiles.Within(project, native.Output).Equals(Path.Combine(generated, "Clip_" + clip.Id + ".anim"), StringComparison.OrdinalIgnoreCase)
                    && outputPaths.Contains(UnityProjectFiles.Within(project, native.Output))) != 1))
            throw new InvalidDataException("Unity 适配结果缺少控制器、Prefab 或本次选择的动画。");
        string? previewPath = preview ? Path.Combine(generated, "CharacterPreview.unity") : null;
        if (preview && (!report.PreviewCreated || string.IsNullOrEmpty(report.PreviewScene)
            || !UnityProjectFiles.Within(project, report.PreviewScene).Equals(previewPath, StringComparison.OrdinalIgnoreCase)
            || !outputPaths.Contains(previewPath!) || config.Clips.Any(clip => report.Clips.All(native =>
                native.Output != result.UnityAssetRoot + "/Generated/Clip_" + clip.Id + ".anim" || native.State != "Base Layer.Clip_" + clip.Id))))
            throw new InvalidDataException("预览场景或可切换动画状态不完整。");
        if (report.Clips.Any(clip => clip.Humanoid) != !string.IsNullOrEmpty(report.HumanAvatar))
            throw new InvalidDataException("动画播放模式与人形 Avatar 清单不一致。");
        if (!string.IsNullOrEmpty(report.HumanAvatar) && (report.HumanAvatar != result.UnityAssetRoot + "/Generated/CharacterHumanAvatar.asset"
            || !outputPaths.Contains(UnityProjectFiles.Within(project, report.HumanAvatar))))
            throw new InvalidDataException("人形 Avatar 输出缺失或不属于本次包。");
        var materialPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var material in report.Materials)
        {
            string path = UnityProjectFiles.Within(project, material.Output);
            if (!path.StartsWith(Path.Combine(generated, "Materials") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(path) != ".mat" || !outputPaths.Contains(path) || !materialPaths.Add(path))
                throw new InvalidDataException("生成的预览材质路径无效或重复。");
        }
        if (config.AdapterVersion is "0.9.0" or "0.10.0" or "0.11.1" or "0.12.0")
        {
            var metadata = inputPaths.Concat(outputPaths).Concat(new[] {
                UnityProjectFiles.Within(project, "Assets/EndfieldCharacterWorkbench/Editor/CharacterAnimationAdapter.cs"),
                UnityProjectFiles.Within(project, "Assets/EndfieldCharacterWorkbench/Runtime/CharacterPreviewControls.cs") })
                .Select(path => path + ".meta").ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (report.Metadata.Count != metadata.Count)
                throw new InvalidDataException("Unity 元数据清单不完整，不能确认资产引用。");
            foreach (var file in report.Metadata)
            {
                string path = UnityProjectFiles.Within(project, file.Path);
                if (!metadata.Remove(path) || !File.Exists(path) || file.Bytes <= 0 || new FileInfo(path).Length != file.Bytes
                    || await HashAsync(path, token) != file.Sha256)
                    throw new InvalidDataException("Unity 元数据缺失或已变化，请保留现有编辑并重新准备独立包。");
            }
        }
        if (await HashAsync(reportPath, token) != reportHash || await HashAsync(configPath, token) != result.UnityConfigSha256
            || ValidatePreparation(saved).RunId != result.RunId)
            throw new InvalidDataException("读取期间适配报告、配置或任务发生变化，请重新读取。");
        token.ThrowIfCancellationRequested();
        result = result with { Status = "adapted_unverified", Adapted = true, PlaybackVerified = false,
            UnityReportPath = reportPath, UnityPreviewScene = previewPath, DiagnosticReportPath = UnityDiagnosticReport.Export(saved),
            Message = $"已读取 Unity 原生适配结果：{report.Clips.Count} 条动画" + (preview ? "及独立预览场景" : "") + "；尚未验证播放。" };
        JsonFiles.Write(Path.Combine(taskRoot, "objects", result.RunId, "operation.json"), result);
        JsonFiles.Write(Path.Combine(taskRoot, "objects", "preparation.json"), result);
        if (ObjectWorkflow.ReadResult(saved)?.RunId == result.RunId)
            JsonFiles.Write(Path.Combine(taskRoot, "objects", "result.json"), result);
        return result;
    }

    private static Task<string> HashAsync(string path, CancellationToken token) =>
        Task.Run(() => UnityProjectFiles.Hash(path, token), token);

    private static string TrackHash(string path)
    {
        if (path.StartsWith("path_", StringComparison.Ordinal) && uint.TryParse(path[5..], out var unresolved)) return unresolved.ToString();
        uint hash = 0xffffffff;
        foreach (byte value in System.Text.Encoding.UTF8.GetBytes(path))
        { hash ^= value; for (int i = 0; i < 8; i++) hash = (hash >> 1) ^ ((hash & 1) != 0 ? 0xedb88320u : 0); }
        return (hash ^ 0xffffffff).ToString();
    }

    private sealed record NativeConfig
    {
        public string Schema { get; init; } = "";
        public string TaskId { get; init; } = "";
        public string RunId { get; init; } = "";
        public string Model { get; init; } = "";
        public string AdapterVersion { get; init; } = "";
        public PreviewOptions? Preview { get; init; }
        public List<Input> Inputs { get; init; } = [];
        public List<Clip> Clips { get; init; } = [];
    }
    private sealed record Input(string Path, string Sha256);
    private sealed record Clip(string Id, string Name, string Source);
    private sealed record PreviewOptions(bool Enabled);
    private sealed record NativeReport
    {
        public string Schema { get; init; } = "";
        public string TaskId { get; init; } = "";
        public string RunId { get; init; } = "";
        public string ConfigSha256 { get; init; } = "";
        public string InvocationId { get; init; } = "";
        public string Status { get; init; } = "";
        public bool Adapted { get; init; }
        public bool PlaybackVerified { get; init; }
        public bool PreviewCreated { get; init; }
        public string PreviewScene { get; init; } = "";
        public string? HumanAvatar { get; init; }
        public List<NativeClip> Clips { get; init; } = [];
        public List<ObjectOutputFile> Outputs { get; init; } = [];
        public List<ObjectOutputFile> Metadata { get; init; } = [];
        public List<NativeMaterial> Materials { get; init; } = [];
    }
    private sealed record NativeClip
    {
        public string Id { get; init; } = "";
        public int TrackAuditVersion { get; init; }
        public int SourceBindings { get; init; }
        public int SourceObjectBindings { get; init; }
        public int RetainedBindings { get; init; }
        public List<NativeTrack> Tracks { get; init; } = [];
        public bool Adapted { get; init; }
        public bool Humanoid { get; init; }
        public string Name { get; init; } = "";
        public string Output { get; init; } = "";
        public string State { get; init; } = "";
    }
    private sealed record NativeTrack
    {
        public int Keys { get; init; }
        public int OutputKeys { get; init; }
        public string SourceHash { get; init; } = "";
        public string SourcePath { get; init; } = "";
        public string Property { get; init; } = "";
        public string Component { get; init; } = "";
        public string Decision { get; init; } = "";
        public string Reason { get; init; } = "";
    }
    private sealed record NativeMaterial { public string Output { get; init; } = ""; }
}
